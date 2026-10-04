using System.Diagnostics;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>Parses ValheimCLI's <c>VALUE</c> replies (for example from <c>cli_call</c>).</summary>
public static class CliReply
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
    /// generated zone, so this re-asks every <paramref name="poll"/> (250 ms by default) until <paramref name="perZone"/>
    /// expires. Records one step per zone. Requires devcommands (<c>cli_call</c>).
    /// </summary>
    public static async Task EnsureGeneratedAsync(GameActor server, IEnumerable<(int X, int Z)> zones, ScenarioReport report,
        TimeSpan perZone, TimeSpan? poll = null, CancellationToken cancellation = default)
    {
        if (perZone <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perZone));
        foreach (var (x, z) in zones)
            await report.StepAsync(FormattableString.Invariant($"prepared generated zone {x},{z}"), async () =>
            {
                // Not an ObservedWait: each round repeats the ghost-zone request (a mutation) until the game reports the zone
                // generated, and ObservedWait only re-reads. Fixture authoring, never an acceptance result.
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (CliReply.Bool(server.Execute(FormattableString.Invariant($"cli_call ZoneSystem.instance.IsZoneGenerated {x},{z}"))))
                        return;
                    if (clock.Elapsed >= perZone) throw new WaitTimeoutException(FormattableString.Invariant($"zone {x},{z} generated"), clock.Elapsed, "not generated");
                    server.Execute(FormattableString.Invariant($"cli_call ZoneSystem.instance.CreateGhostZones {x * 64},0,{z * 64}"));
                    await Task.Delay(poll ?? TimeSpan.FromMilliseconds(250), cancellation).ConfigureAwait(false);
                }
            }, "Zone preparation failed.").ConfigureAwait(false);
    }
}
