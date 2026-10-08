using System.Globalization;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>One read-only ValheimCLI observation retained with the actor's role and its complete reply.</summary>
[ResultShape]
public sealed record ObservedCommand(string Role, string Command, DateTimeOffset AtUtc, IReadOnlyList<string> Reply);

/// <summary>
/// What the terrain and object snapshots share: the world and role check, a recorded command, one coordinate-checked
/// reply line, a recorded capability observation, the area-readiness wait and the loaded ground height.
/// </summary>
internal static class SiteObservation
{

    /// <summary>The actor is ready in <paramref name="worldUid"/>; a client has a ready player, a server hosts the world.</summary>
    internal static void CheckWorld(GameActor actor, string worldUid, string role)
    {
        var state = new SessionControl(actor).Read();
        if (!state.WorldReady || state.WorldUid != worldUid)
            throw new InvalidOperationException($"{role} is not ready in the expected world {worldUid}; observed UID {state.WorldUid ?? "none"}.");
        if (role == "client" && (!state.LocalPlayer || !state.PlayerReady))
            throw new InvalidOperationException("Loaded terrain, objects and paint require a ready client player at the site.");
        if (role == "server" && !state.Server)
            throw new InvalidOperationException("The supplied server actor is not hosting the world.");
    }

    /// <summary>Runs <paramref name="command"/>, records it before judging (so a refusal is evidence too) and requires a reply.</summary>
    internal static IReadOnlyList<string> Lines(GameActor actor, string role, string command, List<ObservedCommand> commands)
    {
        var reply = actor.Execute(command, requireAccepted: false);
        string[] lines = reply.Output.ToArray();
        commands.Add(new(role, command, DateTimeOffset.UtcNow, lines));
        if (!reply.Accepted || lines.Length == 0)
            throw new InvalidOperationException($"{role} returned an incomplete or error reply for {command}: {string.Join(" | ", lines)}");
        return lines;
    }

    /// <summary>
    /// Waits until each area, in order, is ready and loaded with every saved object instantiated (<c>valheim.observe/zones</c>),
    /// within one deadline for them all. The game raises no event for a loaded area, so the pending area is re-read every
    /// 100 ms (<see cref="ObservedWait"/>); a ready area is not read again.
    /// </summary>
    internal static void WaitAreasReady(IReadOnlyList<(GameActor Actor, string Role, int X, int Z)> areas, TimeSpan timeout,
        List<ObservedCommand> commands, CancellationToken cancellation)
    {
        var capabilities = areas.Select(area => area.Actor.RequireCapability("valheim.observe/zones")).ToArray();
        int next = 0;
        string last = "no area reply";
        ObservedWait.Until(areas.Count == 1 ? $"the {areas[0].Role}'s area at {areas[0].X},{areas[0].Z} ready" : "every site point's area ready", () =>
            {
                for (; next < areas.Count; next++)
                {
                    var (actor, role, x, z) = areas[next];
                    ZoneId zone = ZoneId.Of(x, z);
                    var observation = Observe(actor, role, capabilities[next], commands, zone.ToString());
                    var state = ZoneReading.Parse(observation.Data, [zone]).Zones[0];
                    last = $"{role} area at {x},{z}: {state}";
                    if (!state.AreaReady || !state.Loaded) break;
                }
                return next;
            }, ready => ready == areas.Count, timeout, TimeSpan.FromMilliseconds(100), cancellation, describe: _ => last);
    }

    /// <summary>
    /// Observes a read-only capability and records the command with its complete reply lines before judging them, so a
    /// refusal is evidence too. A command refused before any reply (a pin drift, say) is recorded with the error. The
    /// caller reads the observation with the probe that owns its source.
    /// </summary>
    internal static Observation Observe(GameActor actor, string role, Capability capability, List<ObservedCommand> commands, params string[] arguments)
    {
        string command = "cli_extension " + capability.Path + (arguments.Length == 0 ? "" : " " + string.Join(" ", arguments));
        bool recorded = false;
        try
        {
            return actor.Observe(capability, lines => { commands.Add(new(role, command, DateTimeOffset.UtcNow, lines)); recorded = true; }, arguments);
        }
        catch (Exception error) when (!recorded)
        {
            commands.Add(new(role, command, DateTimeOffset.UtcNow, ["ERROR: " + error.Message]));
            throw;
        }
    }

    /// <summary>The loaded ground height at an integer point, read as <see cref="TerrainProbe.Height"/> reads it.</summary>
    internal static float GroundHeight(GameActor actor, string role, int x, int z, List<ObservedCommand> commands) =>
        TerrainProbe.Height(Observe(actor, role, actor.RequireCapability("valheim.world/terrain"), commands,
            x.ToString(CultureInfo.InvariantCulture), z.ToString(CultureInfo.InvariantCulture), "loaded-ground"), x, z, "loaded-ground");

}
