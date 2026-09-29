using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>
/// Puts a joined client's player where a check needs it and proves it stands there: protection, arrival arranged by the
/// server, and stationary support read on the client. The server moves the player with the game's own teleport
/// (<c>cli_teleport_peer</c>), which runs on the player's client and needs nothing installed there, so this works on a
/// client that is not an admin and has cheats off. Arrival is established only by the client's own observations; a
/// teleport reply is not arrival.
/// </summary>
public static class PlayerPlacement
{
    /// <summary>
    /// Turns on god, ghost and debug modes for the local player (<c>cli_set_player_safety true</c>) and requires the game
    /// to read all three back. Debug flying stays off, so support observations remain meaningful.
    /// </summary>
    public static void Protect(GameActor client)
    {
        var reply = client.Execute("cli_set_player_safety true");
        string line = reply.Output.LastOrDefault(l => l.Contains("playerSafety", StringComparison.Ordinal)) ?? "";
        if (!line.StartsWith("OK: playerSafety enabled=True god=True ghost=True debugMode=True", StringComparison.Ordinal))
            throw new InvalidOperationException("Player protection was not confirmed: " + (line.Length > 0 ? line : string.Join(" | ", reply.Output)));
    }

    /// <summary>
    /// Ends a new character's first-spawn intro (the Valkyrie ride) as the menu's Skip button does, or stops it before it
    /// starts, and waits until the player has respawned on the ground (<c>cli_skip_intro</c>, not a cheat command).
    /// Returns whether an intro was running; a character that has spawned before returns false and nothing changes.
    /// The game records the skipped intro in the character, as it does after a normal first spawn.
    /// </summary>
    public static bool SkipIntro(GameActor client, TimeSpan timeout)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var previous = client.CommandTimeout;
        CommandResult reply;
        try
        {
            client.CommandTimeout = timeout + TimeSpan.FromSeconds(10);
            reply = client.Execute("cli_skip_intro " + timeout.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));
        }
        finally { client.CommandTimeout = previous; }
        string line = reply.Output.LastOrDefault(l => l.StartsWith("OK: skipped=", StringComparison.Ordinal) || l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? "";
        if (line.StartsWith("OK: skipped=True", StringComparison.Ordinal)) return true;
        if (line.StartsWith("OK: skipped=False", StringComparison.Ordinal)) return false;
        throw new InvalidOperationException("The intro was not confirmed skipped: " + (line.Length > 0 ? line : string.Join(" | ", reply.Output)));
    }

    /// <summary>
    /// The server's only connected peer that has a character, as <c>cli_peers</c> numbers it. Refuses none or several:
    /// with more than one player the test cannot tell which one it would move.
    /// </summary>
    public static int OnlyPeer(GameActor server)
    {
        var reply = server.Execute("cli_peers");
        var characters = reply.Output.Where(l => l.StartsWith("PEER ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 2 && w[2] == "character").ToArray();
        if (characters.Length != 1)
            throw new InvalidOperationException($"Expected exactly one connected player with a character; the server lists {characters.Length}.");
        return int.Parse(characters[0][1], NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Unless <paramref name="skipIntro"/> is false, first ends a first-join intro (<see cref="SkipIntro"/>), which is a
    /// no-op for a character that has spawned before. Then waits until the client's player has stood still somewhere for
    /// <paramref name="settleFor"/> (default 3 s): a first join rides in on the Valkyrie, and the game refuses a teleport
    /// within 2 s of a spawn or of the previous teleport, silently in both cases. Then has the server teleport the only connected player to <paramref name="point"/> (a little
    /// above it, so the character settles onto the ground) exactly once, and waits on the client until its player stands
    /// settled there (<see cref="SurfaceProbe.Supported"/>). Returns the observation that established arrival. The one
    /// timeout covers the intro and both waits. Times out without retrying the teleport: a lost reply is an unknown outcome, not a
    /// failure to act.
    /// </summary>
    public static JsonElement Arrive(GameActor server, GameActor client, HeightExpectation point, TimeSpan timeout, CancellationToken cancellation = default, TimeSpan? settleFor = null, bool skipIntro = true)
    {
        var still = settleFor ?? TimeSpan.FromSeconds(3);
        if (still < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(settleFor));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        TerrainProbe.Validate("loaded-ground", "arrival point", [point], .3f);
        var support = client.RequireCapability("valheim.world/player-support");
        var clock = Stopwatch.StartNew();
        // The intro gets at most a minute of the budget, and never less than the command's one-second minimum.
        if (skipIntro) SkipIntro(client, TimeSpan.FromSeconds(Math.Clamp(Math.Floor(timeout.TotalSeconds), 1, 60)));
        Observation? last = null;
        // Settled anywhere, and for long enough: the game drops a teleport while the player is attached (the first-join
        // Valkyrie), loading or already teleporting, and within 2 s of a spawn (its teleport cooldown).
        TimeSpan? settledSince = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            last = client.Observe(support);
            if (!Settled(last)) settledSince = null;
            else if ((settledSince ??= clock.Elapsed) + still <= clock.Elapsed) break;
            if (clock.Elapsed >= timeout)
                throw new TimeoutException($"The player never stood still before the teleport within {timeout.TotalSeconds:F0} s; last reading: {last.Data.GetRawText()}. Nothing was teleported.");
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
        int peer = OnlyPeer(server);
        string at = string.Join(" ", new[] { point.X, point.Height + .5f, point.Z }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        var reply = server.Execute($"cli_teleport_peer {peer} {at}");
        if (!reply.Output.Any(l => l.StartsWith("OK: asked peer", StringComparison.Ordinal)))
            throw new InvalidOperationException("The server did not accept the teleport: " + string.Join(" | ", reply.Output));
        while (clock.Elapsed < timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            last = client.Observe(support);
            if (last.Complete && SurfaceProbe.Supported(last, point)) return last.Data.Clone();
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
        throw new TimeoutException($"The player did not settle at ({point.X}, {point.Height}, {point.Z}) within {timeout.TotalSeconds:F0} s; last reading: " +
            (last == null ? "none" : last.Data.GetRawText()) + ". The teleport was not repeated.");
    }

    private static bool Settled(Observation observation)
    {
        if (!observation.Complete) return false;
        var d = observation.Data;
        return d.GetProperty("grounded").GetBoolean() && !d.GetProperty("attached").GetBoolean() && !d.GetProperty("teleporting").GetBoolean() &&
            !d.GetProperty("dead").GetBoolean() && d.GetProperty("speed").GetSingle() <= .15f;
    }

    /// <summary>
    /// <paramref name="readings"/> client observations <paramref name="interval"/> apart (default three, half a second),
    /// each of which must show the player settled on <paramref name="point"/>: stationary support, not walking usability.
    /// </summary>
    public static IReadOnlyList<JsonElement> RequireSupported(GameActor client, HeightExpectation point, int readings = 3, TimeSpan? interval = null)
    {
        if (readings < 1) throw new ArgumentOutOfRangeException(nameof(readings));
        var wait = interval ?? TimeSpan.FromMilliseconds(500);
        var support = client.RequireCapability("valheim.world/player-support");
        var states = new List<JsonElement>();
        for (int i = 0; i < readings; i++)
        {
            if (i > 0) Thread.Sleep(wait);
            var state = client.Observe(support);
            states.Add(state.Data.Clone());
            if (!SurfaceProbe.Supported(state, point))
                throw new SupportException($"Reading {i + 1} of {readings} does not show the player settled on the declared ground: {state.Data.GetRawText()}", states);
        }
        return states;
    }
}

/// <summary>A support check failed; <see cref="Readings"/> holds every observation taken, the failing one last.</summary>
public sealed class SupportException(string message, IReadOnlyList<JsonElement> readings) : InvalidOperationException(message)
{
    public IReadOnlyList<JsonElement> Readings { get; } = readings;
}
