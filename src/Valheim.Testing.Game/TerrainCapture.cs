using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Valheim.Testing.Game;

public sealed record TerrainGridRequest(float X,float Z,float Spacing,int CountX,int CountZ,string Layer)
{
    public void Validate()
    {
        if(!float.IsFinite(X)||!float.IsFinite(Z)||!float.IsFinite(Spacing)||Spacing<.25f||Spacing>256 ||
            CountX<1||CountZ<1||CountX>256||CountZ>256||(long)CountX*CountZ>256 ||
            Math.Abs(X)>20000||Math.Abs(Z)>20000||Math.Abs(X+(CountX-1)*Spacing)>20000||Math.Abs(Z+(CountZ-1)*Spacing)>20000 ||
            (Layer!="generator"&&Layer!="loaded-ground"))throw new ArgumentException("Invalid bounded terrain grid.");
    }
    internal string[] Arguments()=>[X.ToString("R",CultureInfo.InvariantCulture),Z.ToString("R",CultureInfo.InvariantCulture),
        Spacing.ToString("R",CultureInfo.InvariantCulture),CountX.ToString(CultureInfo.InvariantCulture),CountZ.ToString(CultureInfo.InvariantCulture),Layer];
}

/// <summary>Validated captured inputs. Replay is exact and is not an independent correctness assertion.</summary>
public sealed class TerrainCapture
{
    private readonly JsonElement payload;
    public TerrainGridRequest Grid { get; }
    public string WorldUid { get; }
    public string GameVersion { get; }
    public string Provenance { get; }
    public ReplayTerrain Terrain { get; }
    private TerrainCapture(JsonElement data,TerrainGridRequest grid,string provenance,IEnumerable<TerrainSample> samples)
    {
        payload=data.Clone();Grid=grid;Provenance=provenance;
        WorldUid=data.GetProperty("worldUid").GetString()!;GameVersion=data.GetProperty("gameVersion").GetString()!;
        Terrain=new ReplayTerrain(provenance,grid.Layer,samples);
    }
    public static TerrainCapture Read(GameActor actor,TerrainGridRequest grid,string provenance)
    {
        grid.Validate();if(string.IsNullOrWhiteSpace(provenance))throw new ArgumentException("Describe the capture source.");
        var capability=actor.RequireCapability("valheim.world/terrain-grid");
        return FromObservation(actor.Observe(capability,grid.Arguments()),grid,provenance);
    }
    public static TerrainCapture FromObservation(Observation observation,TerrainGridRequest expected,string provenance)
    {
        expected.Validate();observation.RequireComplete("terrain-grid");
        if(string.IsNullOrWhiteSpace(provenance))throw new ArgumentException("Describe the capture source.");
        var d=observation.Data;
        if(d.GetProperty("formatVersion").GetInt32()!=1 || d.GetProperty("source").GetString()!="terrain-grid" ||
            !d.GetProperty("complete").GetBoolean() || d.GetProperty("units").GetString()!="metres" ||
            d.GetProperty("consistency").GetString()!="per-sample")throw new InvalidDataException("Unsupported or incomplete terrain capture.");
        var actual=new TerrainGridRequest(d.GetProperty("originX").GetSingle(),d.GetProperty("originZ").GetSingle(),
            d.GetProperty("spacing").GetSingle(),d.GetProperty("countX").GetInt32(),d.GetProperty("countZ").GetInt32(),d.GetProperty("layer").GetString()!);
        if(actual!=expected)throw new InvalidDataException("Capture does not match requested grid/layer.");
        if(!long.TryParse(d.GetProperty("worldUid").GetString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out _) ||
            string.IsNullOrWhiteSpace(d.GetProperty("gameVersion").GetString()) ||
            !Guid.TryParse(d.GetProperty("gameAssemblyId").GetString(),out _) || d.GetProperty("worldGenVersion").GetInt32()<0 ||
            !DateTimeOffset.TryParse(d.GetProperty("startedUtc").GetString(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var start) ||
            !DateTimeOffset.TryParse(d.GetProperty("finishedUtc").GetString(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var end) || end<start)
            throw new InvalidDataException("Capture provenance is incomplete.");
        var rows=d.GetProperty("samples");if(rows.GetArrayLength()!=expected.CountX*expected.CountZ)throw new InvalidDataException("Missing or extra samples.");
        var samples=new List<TerrainSample>();int i=0;
        foreach(var row in rows.EnumerateArray())
        {
            float x=expected.X+i%expected.CountX*expected.Spacing,z=expected.Z+i/expected.CountX*expected.Spacing;i++;
            if(!row.GetProperty("complete").GetBoolean() || row.GetProperty("x").GetSingle()!=x || row.GetProperty("z").GetSingle()!=z)
                throw new InvalidDataException("Missing, reordered or misplaced sample.");
            float h=row.GetProperty("height").GetSingle();TerrainBiome biome=TerrainBiome.Unknown;float? weight=null,width=null;
            if(expected.Layer=="generator")
            {
                // The game's own names, exactly (AshLands); a number, another case or Unknown is refused.
                var cell=row.GetProperty("biome");string? name=cell.ValueKind==JsonValueKind.String?cell.GetString():null;
                if(name==null||name==nameof(TerrainBiome.Unknown)||!Enum.GetNames<TerrainBiome>().Contains(name,StringComparer.Ordinal))
                    throw new InvalidDataException("Unsupported captured biome.");
                biome=Enum.Parse<TerrainBiome>(name);
                weight=row.GetProperty("riverWeight").GetSingle();width=row.GetProperty("riverWidth").GetSingle();
                if(!float.IsFinite(weight.Value)||!float.IsFinite(width.Value)||weight<0||weight>1||width<0)throw new InvalidDataException("Invalid river sample.");
            }
            else if(row.GetProperty("biome").ValueKind!=JsonValueKind.Null || row.GetProperty("riverWeight").ValueKind!=JsonValueKind.Null ||
                row.GetProperty("riverWidth").ValueKind!=JsonValueKind.Null)throw new InvalidDataException("Loaded ground must not masquerade as generator facts.");
            if(!float.IsFinite(h))throw new InvalidDataException("Non-finite terrain sample.");
            samples.Add(new TerrainSample(x,z,h,biome,weight,width));
        }
        return new TerrainCapture(d,expected,provenance,samples);
    }
    private static string Digest(JsonElement data)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
    public void Save(string path)
    {
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(file,new { fileVersion=1,provenance=Provenance,sha256=Digest(payload),payload },new JsonSerializerOptions{WriteIndented=true});
    }
    public static TerrainCapture Load(string path)
    {
        if(new FileInfo(path).Length>1048576)throw new InvalidDataException("Capture file exceeds 1MiB.");
        using var doc=JsonDocument.Parse(File.ReadAllText(path));var root=doc.RootElement;var d=root.GetProperty("payload");
        if(root.GetProperty("fileVersion").GetInt32()!=1 || root.GetProperty("sha256").GetString()!=Digest(d))throw new InvalidDataException("Capture checksum/version mismatch.");
        var grid=new TerrainGridRequest(d.GetProperty("originX").GetSingle(),d.GetProperty("originZ").GetSingle(),d.GetProperty("spacing").GetSingle(),
            d.GetProperty("countX").GetInt32(),d.GetProperty("countZ").GetInt32(),d.GetProperty("layer").GetString()!);
        return FromObservation(new Observation("terrain-grid",d.GetProperty("complete").GetBoolean(),d),grid,root.GetProperty("provenance").GetString()!);
    }
}
