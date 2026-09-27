using System.Globalization;
using System.Text.Json;
using Valheim.Testing;
using Valheim.Testing.Game;
using valheim_cli.Testing;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: GameObserve <host> <port> <expectations-file> <x> <z>\nAttaches read-only; never launches/stops the game or changes terrain.");
    return 2;
}
try
{
    if (!PlanExpectations.TryLoad(args[2], true, out string pins, out string error)) throw new InvalidOperationException(error);
    float x = float.Parse(args[3], CultureInfo.InvariantCulture), z = float.Parse(args[4], CultureInfo.InvariantCulture);
    using var server = new GameActor("observer", new CliTransport(args[0], int.Parse(args[1], CultureInfo.InvariantCulture)));
    server.VerifyEnvironment(pins);
    var capability = server.RequireCapability("valheim.world/terrain");
    var sample = server.Observe(capability, x.ToString("R", CultureInfo.InvariantCulture), z.ToString("R", CultureInfo.InvariantCulture), "generator");
    sample.RequireComplete("generator");
    Console.WriteLine(JsonSerializer.Serialize(sample));
    // Demonstrate captured-input replay, without pretending this calibrates client ground.
    var replay = new ReplayTerrain("CLI observation; retain the command's expectation file and loaded manifest alongside it", sample.Source,
        [new TerrainSample(x, z, sample.Data.GetProperty("height").GetSingle(), TerrainBiome.Unknown)]);
    Check.Near(replay.GetHeight(x, z), sample.Data.GetProperty("height").GetSingle(), .001);
    Console.WriteLine("Captured sample can be replayed. This is an input round-trip, not a terrain-conversion or physics test.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
