using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

// examples/ObserveCheck's one flow (inputs, pins, probe, report) against a scripted game: what each removed single-probe
// example did, through one program. The probes themselves are covered by their own tests (PaintProbeTests and the rest).
public sealed class ObserveCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("observe-check-").FullName;
    private readonly ScriptedTransport _game = new ScriptedTransport()
        .Extension("valheim.world", "terrain-paint", a => new { source = "loaded-terrain-paint", complete = true, x = 32f, z = 0f, r = 1f, g = 0f, b = .5f, a = .25f, units = "rgba01" });
    private int _connects;

    public ObserveCheckTests()
    {
        File.WriteAllText(Pins, "worlduid=7\n");
        Plan(1);
    }
    public void Dispose() => Directory.Delete(_root, true);

    private string Pins => Path.Combine(_root, "pins.txt");
    private string PaintPlanFile => Path.Combine(_root, "paint.json");
    private string Output => Path.Combine(_root, "out");
    private void Plan(float r) => File.WriteAllText(PaintPlanFile,
        $$"""{"expectedFrom":"declared fixture mask","tolerance":0.01,"samples":[{"x":32,"z":0,"r":{{r}},"g":0,"b":0.5,"a":0.25}]}""");
    private int Run(params string[] probe) => ObserveCheck.Run(["127.0.0.1", "5555", Pins, Output, .. probe], (_, _) => { _connects++; return _game; });
    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement;

    [Fact]
    public void AMatchingPlanPassesAfterThePinsHold()
    {
        Assert.Equal(0, Run("paint", PaintPlanFile));
        Assert.True(Result().GetProperty("Passed").GetBoolean());
        Assert.StartsWith("cli_expect --strict worlduid=7", _game.Commands[0], StringComparison.Ordinal);
        Assert.Contains(_game.Commands, c => c.StartsWith("cli_extension valheim.world/terrain-paint 32 0", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Output, "paint.json")));
        Assert.True(File.Exists(Path.Combine(Output, "commands.jsonl")));
    }

    [Fact]
    public void AMismatchFailsWithEveryResidualKept()
    {
        Plan(0);
        Assert.Equal(1, Run("paint", PaintPlanFile));
        Assert.False(Result().GetProperty("Passed").GetBoolean());
        Assert.Contains("\"Passed\": false", File.ReadAllText(Path.Combine(Output, "paint.json")));
    }

    [Fact]
    public void PinsThatDoNotHoldStopTheRunBeforeTheProbe()
    {
        _game.PinsHold = false;
        Assert.Equal(1, Run("paint", PaintPlanFile));
        Assert.DoesNotContain(_game.Commands, c => c.Contains("terrain-paint", StringComparison.Ordinal));
        var steps = Result().GetProperty("Steps").EnumerateArray().ToArray();
        Assert.Equal("Preflight", Assert.Single(steps).GetProperty("Phase").GetString());
    }

    [Theory]
    [InlineData("no-such-probe")]
    [InlineData("paint")]
    [InlineData("session", "join", "localhost:2456")]
    [InlineData("capture", "0", "0", "1", "1", "1")]
    public void InvalidUsageExitsTwoAndTouchesNothing(params string[] probe)
    {
        Assert.Equal(2, Run(probe));
        Assert.False(Path.Exists(Output));
        Assert.Equal(0, _connects);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    public void APortOutOfRangeIsInvalidUsage(string port)
    {
        Assert.Equal(2, ObserveCheck.Run(["127.0.0.1", port, Pins, Output, "paint", PaintPlanFile], (_, _) => { _connects++; return _game; }));
        Assert.False(Path.Exists(Output));
        Assert.Equal(0, _connects);
    }

    [Fact]
    public void ReportsHashInputsAndNameThemWithoutTheirDirectories()
    {
        Assert.Equal(0, Run("paint", PaintPlanFile));
        var provenance = Result().GetProperty("Provenance");
        Assert.Equal("paint paint.json", provenance.GetProperty("probe").GetString());
        Assert.Equal(FileHash.Sha256(PaintPlanFile), provenance.GetProperty("inputSha256").GetString());
        Assert.Equal(FileHash.Sha256(Pins), provenance.GetProperty("pinsSha256").GetString());
    }

    [Fact]
    public void ARefusedInputOrAnExistingOutputNeverConnects()
    {
        File.WriteAllText(PaintPlanFile, """{"expectedFrom":"x","tolerance":0.01,"samples":[{"x":32.5,"z":0,"r":1,"g":0,"b":0,"a":0}]}""");
        Assert.Equal(1, Run("paint", PaintPlanFile));
        Assert.False(Path.Exists(Output));
        Plan(1);
        Directory.CreateDirectory(Output);
        Assert.Equal(1, Run("paint", PaintPlanFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Output));
        Assert.Equal(0, _connects);
    }
}
