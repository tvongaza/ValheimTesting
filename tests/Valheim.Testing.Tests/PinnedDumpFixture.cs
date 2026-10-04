using System.Globalization;
using Valheim.Testing;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;

/// <summary>
/// Pinned dumps for tests, made by the real producer: a scripted transport stands in for the game and answers the dump
/// with a reply derived from the CSV, and <see cref="WorldDump.CaptureCoreAsync"/> writes the CSV and manifest. No test
/// writes a manifest by hand, so every manifest here is one a capture would write.
/// </summary>
internal sealed class PinnedDumpFixture : IDisposable
{
    public const string HostDirectory = "C:/dumps";
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("pinned-dump-").FullName;

    /// <summary>The real 2 October 2026 captures (Valheim 1.0.16, disposable world DumpContract1), as checked in.</summary>
    public static string Real(string layer) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorldDump", $"dumpcontract1-{layer}.json");

    public WorldDumpManifest Capture(string name, string csv, int step, string uid = "1716468958", (float X, float Z, float Radius)? window = null)
    {
        var grid = GridDumpTerrain.Read(new StringReader(csv), name);
        string file = $"world.{name}_s{step}.csv";
        string reply = string.Format(CultureInfo.InvariantCulture, "OK: WORLD_DUMP samples={0} step={1}", grid.CountX * grid.CountZ, step) +
            (window is { } w ? string.Format(CultureInfo.InvariantCulture, " window={0},{1},{2} extent={3:F0},{4:F0}..{5:F0},{6:F0}",
                w.X, w.Z, w.Radius, grid.OriginX, grid.OriginZ, grid.MaxX, grid.MaxZ) : "") + $" ms=1 world={HostDirectory}\\{file}";
        var transport = new ScriptedTransport()
            .On("cli_world", _ => ScriptedTransport.Ok($"WORLD name=Fixture seed=fixture-seed uid={uid} worldgen=2 files=- files_hashed=not_at_load dir="))
            .OnPrefix("cli_world_dump ", _ => ScriptedTransport.Ok(reply));
        using var actor = transport.Actor();
        return WorldDump.CaptureCoreAsync(actor, (_, local, _) => Fetch(local, file, csv), HostDirectory, name, step, window,
            "fixture-build", Directory, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static Task<FetchedDirectory> Fetch(string local, string file, string content)
    {
        System.IO.Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, file), content);
        return Task.FromResult(new FetchedDirectory(local, new string('0', 64), content.Length, 1));
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}
