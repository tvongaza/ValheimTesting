using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>One read-only ValheimCLI observation retained with the actor's role and its complete reply.</summary>
public sealed record ObservedCommand(string Role, string Command, DateTimeOffset AtUtc, IReadOnlyList<string> Reply);

/// <summary>
/// What the terrain and object snapshots share: the world and role check, a recorded command, one coordinate-checked
/// reply line, a recorded capability observation, the area-readiness wait and the loaded ground height.
/// </summary>
internal static class SiteObservation
{
    private static readonly Regex Area = new(@"^OK: AREA_READY (?<x>-?\d+\.\d),(?<z>-?\d+\.\d) ready=(?<ready>True|False) zone=-?\d+,-?\d+ loaded=(?<loaded>True|False) objects=\d+ without_instance=(?<missing>\d+)$", RegexOptions.CultureInvariant);

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

    /// <summary>Exactly one line, matching <paramref name="pattern"/>, naming the point <paramref name="x"/>,<paramref name="z"/>.</summary>
    internal static Match One(IReadOnlyList<string> lines, Regex pattern, int x, int z)
    {
        if (lines.Count != 1) throw new InvalidOperationException("Expected exactly one complete reply line.");
        var match = pattern.Match(lines[0]);
        if (!match.Success || Number(match.Groups["x"].Value) != x || Number(match.Groups["z"].Value) != z)
            throw new InvalidOperationException($"Incomplete reply or wrong coordinates at {x},{z}: {lines[0]}");
        return match;
    }

    /// <summary>Polls <c>cli_area_ready</c> until the point's area is ready and loaded with every saved object instantiated.</summary>
    internal static void WaitAreaReady(GameActor actor, string role, int x, int z, TimeSpan timeout, Stopwatch clock,
        List<ObservedCommand> commands, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var reply = Lines(actor, role, $"cli_area_ready {x} {z} 0", commands);
            var match = One(reply, Area, x, z);
            if (match.Groups["ready"].Value == "True" && match.Groups["loaded"].Value == "True" && match.Groups["missing"].Value == "0") return;
            if (clock.Elapsed >= timeout)
                throw new TimeoutException($"{role} area at {x},{z} did not become ready within {timeout}. Last state: {string.Join(" | ", reply)}");
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(0, (timeout - clock.Elapsed).TotalMilliseconds))));
        }
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

    internal static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new InvalidOperationException("Reply contains a non-finite number.");
        return value;
    }
}
