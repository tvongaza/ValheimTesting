using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using valheim_cli.Testing;

if(args.Length != 5) { Console.Error.WriteLine("TerrainCheck <host> <port> <expectations-file> <height-plan.json> <new-output-dir>\nRead-only attachment; never launches, teleports, changes or stops a game."); return 2; }
var report = new ScenarioReport("terrain-height-check");
string output = Path.GetFullPath(args[4]); bool ownsOutput = false;
try
{
    if(Path.Exists(output)) throw new IOException("Use a new output directory.");
    var plan = JsonSerializer.Deserialize<HeightPlan>(File.ReadAllText(args[3]), new JsonSerializerOptions { PropertyNameCaseInsensitive=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow }) ?? throw new ArgumentException("Empty plan.");
    TerrainProbe.Validate(plan.Layer,plan.ExpectedFrom,plan.Samples,plan.Tolerance);
    if(!PlanExpectations.TryLoad(args[2],true,out string pins,out string error)) throw new ArgumentException(error);
    Directory.CreateDirectory(output); ownsOutput=true;
    report.Provenance["planSha256"]=WorldFixture.Hash(args[3]); report.Provenance["pinsSha256"]=WorldFixture.Hash(args[2]);
    using var actor = new GameActor("terrain-observer", new RecordingTransport(new CliTransport(args[0],int.Parse(args[1])),Path.Combine(output,"commands.jsonl")));
    report.Step("verify fixture and plugin pins",()=>actor.VerifyEnvironment(pins));
    report.Step("compare declared terrain heights",()=>{
        var result=TerrainProbe.Compare(actor,plan.Layer,plan.ExpectedFrom,plan.Samples,plan.Tolerance);
        File.WriteAllText(Path.Combine(output,"terrain.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        if(!result.Passed) throw new InvalidOperationException("Height mismatch; inspect every residual in terrain.json.");
    });
}
catch(Exception error) { try { report.Step("terrain check failed",()=>throw new InvalidOperationException(error.Message,error)); } catch {} Console.Error.WriteLine(error.Message); }
finally { if(ownsOutput) report.Write(output); }
return report.Passed?0:1;
public sealed class HeightPlan
{
    public string Layer {get;set;}="";
    public string ExpectedFrom {get;set;}="";
    public float Tolerance {get;set;}
    public List<HeightExpectation> Samples {get;set;}=[];
}
