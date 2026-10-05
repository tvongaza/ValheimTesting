using System.Diagnostics;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>Parses ValheimCLI's <c>VALUE</c> replies (for example from <c>cli_call</c>).</summary>
internal static class CliReply
{
    /// <summary>The reply's one boolean value line (<c>VALUE true</c> or <c>VALUE false</c>); anything else is refused.</summary>
    public static bool Bool(IReadOnlyList<string> lines)
    {
        var values = lines.Where(x => x.StartsWith("VALUE ", StringComparison.Ordinal)).ToArray();
        if (values.Length != 1 || (values[0] != "VALUE true" && values[0] != "VALUE false")) throw new InvalidOperationException("Expected one boolean value.");
        return values[0] == "VALUE true";
    }
    public static bool Bool(GameReply reply) => Bool(reply.Output);
}

/// <summary>Fixture authoring: make zones exist in a world before a scenario relies on them. Never an acceptance result.</summary>
public static class ZonePreparation
{
    /// <summary>
    /// Makes each zone generated: asks <c>ZoneSystem.instance.IsZoneGenerated</c>, and if not, asks the server to create it
    /// as a ghost zone (<c>CreateGhostZones</c> at the zone's centre) and asks again. The game raises no event for a
    /// generated zone, so this re-asks every <paramref name="poll"/> (250 ms by default, and positive) until
    /// <paramref name="perZone"/> expires. Records one step per zone. Requires devcommands (<c>cli_call</c>).
    /// </summary>
    public static async Task EnsureGeneratedAsync(GameActor server, IEnumerable<(int X, int Z)> zones, ScenarioReport report,
        TimeSpan perZone, TimeSpan? poll = null, CancellationToken cancellation = default)
    {
        if (perZone <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perZone));
        TimeSpan interval = poll ?? TimeSpan.FromMilliseconds(250);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(poll), "Give a positive poll interval.");
        foreach (var (x, z) in zones)
            await report.StepAsync(FormattableString.Invariant($"prepared generated zone {x},{z}"), async () =>
            {
                // Not an ObservedWait: one request is not enough (#343, read from the Valheim 1.0.16 decompile), so the request
                // (a mutation) is repeated here in the open rather than hidden in a wait. ZoneSystem.CreateGhostZones(refPoint)
                // generates at most one zone per call and keeps no pending request: its SpawnZone(zone, SpawnMode.Ghost)
                // returns false without generating while the zone's heightmap is not built (HeightmapBuilder.IsTerrainReady
                // queues the build and answers later) or a location prefab is still loading (PokeCanSpawnLocation). Only
                // ZoneSystem.Update retries, and only around the server's and the peers' reference positions, so a zone far
                // from every player is generated only when it is asked for again. A call whose target is not ready ghosts the
                // first ready, ungenerated zone within the simulation distance instead, so neighbours can be generated too;
                // the requests stop once IsZoneGenerated holds for the target. Fixture authoring, never an acceptance result.
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (CliReply.Bool(server.Execute(FormattableString.Invariant($"cli_call ZoneSystem.instance.IsZoneGenerated {x},{z}"))))
                        return;
                    if (clock.Elapsed >= perZone) throw new WaitTimeoutException(FormattableString.Invariant($"zone {x},{z} generated"), clock.Elapsed, "not generated");
                    server.Execute(FormattableString.Invariant($"cli_call ZoneSystem.instance.CreateGhostZones {x * 64},0,{z * 64}"));
                    TimeSpan left = perZone - clock.Elapsed;
                    await Task.Delay(left >= interval ? interval : left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellation).ConfigureAwait(false);
                }
            }, "Zone preparation failed.").ConfigureAwait(false);
    }
}
