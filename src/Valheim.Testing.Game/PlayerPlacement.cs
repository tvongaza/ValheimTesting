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
    /// <summary>A single teleport's game-reported phase times and the client's supported landing.</summary>
    public sealed record TeleportArrival(JsonElement Support, string Trace, TeleportTrace Timing);

    /// <summary>
    /// Arrives using one bounded in-game wait at each transition. The runner does not poll the remote player:
    /// ValheimCLI observes readiness and support on game frames and returns once each condition holds.
    /// The server requests the teleport exactly once. Test timing is an explicit opt-in and still uses the game's
    /// own area and floor checks; it is switched off again after the hop, so later movements in the session use
    /// the game's ordinary timing. <paramref name="timeout"/> is the overall deadline; each CLI wait is capped
    /// at 120 seconds, so a phase that takes longer fails without issuing another request or teleport.
    /// </summary>
    public static TeleportArrival ArriveOnSignals(GameActor server, GameActor client, HeightExpectation point,
        TimeSpan timeout, bool fastTestTiming = false, CancellationToken cancellation = default, bool skipIntro = true)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(600)) throw new ArgumentOutOfRangeException(nameof(timeout));
        TerrainProbe.Validate("loaded-ground", "arrival point", [point], .3f);
        var support = client.RequireCapability("valheim.world/player-support-wait");
        var clock = Stopwatch.StartNew();
        if (skipIntro) SkipIntro(client, TimeSpan.FromSeconds(Math.Clamp(Math.Floor(timeout.TotalSeconds), 1, 60)));
        if (!fastTestTiming) return Hop(server, client, point, support, clock, timeout, cancellation, skipIntro);
        bool onAttempted = false, offAttempted = false;
        try
        {
            onAttempted = true; // The reply can be lost after the game applied the mode.
            client.Execute("cli_teleport_test_mode on").RequireLine("OK: testFastTeleport enabled=True", "Test teleport timing was not enabled");
            var arrival = Hop(server, client, point, support, clock, timeout, cancellation, skipIntro);
            offAttempted = true; // Never repeat an uncertain off request.
            try
            {
                client.Execute("cli_teleport_test_mode off").RequireLine("OK: testFastTeleport enabled=False",
                    "Test teleport timing was not switched off after a supported landing");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Test teleport timing was not switched off after a supported landing (trace: " + arrival.Trace + ")", error);
            }
            return arrival;
        }
        catch (Exception error)
        {
            if (onAttempted && !offAttempted && error is not OperationCanceledException)
                TryTestTimingOff(client); // A cancelled run is torn down at once.
            throw;
        }
    }

    private static TeleportArrival Hop(GameActor server, GameActor client, HeightExpectation point, Capability support,
        Stopwatch clock, TimeSpan timeout, CancellationToken cancellation, bool skipIntro)
    {
        cancellation.ThrowIfCancellationRequested();
        string ready = SecondsLeft(clock, timeout);
        WithTimeout(client, timeout - clock.Elapsed, () =>
            client.Execute($"cli_wait_teleportable {ready} 0 {!skipIntro}").RequireLine("OK: TELEPORTABLE ",
                "The client never became ready for a teleport"));
        cancellation.ThrowIfCancellationRequested();
        string armed = client.Execute("cli_teleport_trace_arm").RequireLine("OK: TELEPORT_TRACE_ARM id=", "The client did not arm a teleport trace");
        if (!int.TryParse(armed["OK: TELEPORT_TRACE_ARM id=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 1)
            throw new InvalidOperationException("The client returned an invalid teleport trace id: " + armed);
        int peer = OnlyPeer(server);
        string at = string.Join(" ", new[] { point.X, point.Height + .5f, point.Z }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        server.Execute($"cli_teleport_peer {peer} {at}").RequireLine("OK: asked peer", "The server did not accept the teleport");
        cancellation.ThrowIfCancellationRequested();
        string trace = "";
        WithTimeout(client, timeout - clock.Elapsed, () =>
            trace = client.Execute($"cli_teleport_trace_wait {id} {SecondsLeft(clock, timeout)}").RequireLine(
                "OK: TELEPORT_TRACE ", "The client did not complete its teleport"));
        var timing = TeleportTrace.Parse(trace, id);
        if (!timing.FloorAtDone)
            throw new InvalidOperationException("The game ended its teleport without a ready floor: " + trace);
        cancellation.ThrowIfCancellationRequested();
        Observation landed = null!;
        WithTimeout(client, timeout - clock.Elapsed, () => landed = client.Observe(support,
            point.X.ToString("R", CultureInfo.InvariantCulture), point.Height.ToString("R", CultureInfo.InvariantCulture),
            point.Z.ToString("R", CultureInfo.InvariantCulture), SecondsLeft(clock, timeout)));
        if (!SurfaceProbe.Supported(landed, point))
            throw new InvalidOperationException("The client wait ended without supported arrival: " + landed.Data.GetRawText());
        return new TeleportArrival(landed.Data.Clone(), trace, timing);
    }

    // The hop's own failure is the one reported. A client that stopped answering would hold the full command
    // timeout again, so each call (the strict pin check, then the switch) gets 10 s; a client that is gone or
    // leaves the world takes the mode with it.
    private static void TryTestTimingOff(GameActor client)
    {
        var previous = client.CommandTimeout;
        try
        {
            client.CommandTimeout = TimeSpan.FromSeconds(10);
            client.Execute("cli_teleport_test_mode off", requireAccepted: false); // best effort; the hop's failure is reported
        }
        catch (Exception) { }
        finally { client.CommandTimeout = previous; }
    }

    private static string SecondsLeft(Stopwatch clock, TimeSpan timeout)
    {
        double left = (timeout - clock.Elapsed).TotalSeconds;
        if (left <= 0) throw new TimeoutException("The one-hop arrival deadline expired; the teleport was not repeated.");
        return Math.Min(120, left).ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WithTimeout(GameActor actor, TimeSpan remaining, Action action)
    {
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("The one-hop arrival deadline expired; the teleport was not repeated.");
        var previous = actor.CommandTimeout;
        try { actor.CommandTimeout = remaining + TimeSpan.FromSeconds(10); action(); }
        finally { actor.CommandTimeout = previous; }
    }

    /// <summary>
    /// Verifies that a character staged at a world's logout point actually arrived at <paramref name="point"/>.
    /// Reads only the client's support capability; never teleports or falls back to a teleport on failure.
    /// Call after the join and world-ready/protection checks. Returns the reading that proved arrival.
    /// </summary>
    public static JsonElement ObserveArrival(GameActor client, HeightExpectation point, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        TerrainProbe.Validate("loaded-ground", "arrival point", [point], .3f);
        var support = client.RequireCapability("valheim.world/player-support");
        var clock = Stopwatch.StartNew();
        Observation? last = null;
        TimeSpan? wrongPointSince = null;
        do
        {
            cancellation.ThrowIfCancellationRequested();
            last = client.Observe(support);
            RefuseFlying(last);
            if (last.Complete && SurfaceProbe.Supported(last, point)) return last.Data.Clone();
            if (last.Complete && SettledElsewhere(last, point))
            {
                wrongPointSince ??= clock.Elapsed;
                if (clock.Elapsed - wrongPointSince >= TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException($"The staged character is settled away from ({point.X}, {point.Height}, {point.Z}): " +
                        last.Data.GetRawText() + ". No teleport was sent.");
            }
            else wrongPointSince = null;
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        } while (clock.Elapsed < timeout);
        throw new TimeoutException($"The staged character did not settle at ({point.X}, {point.Height}, {point.Z}) within {timeout.TotalSeconds:F0} s; last reading: " +
            (last == null ? "none" : last.Data.GetRawText()) + ". No teleport was sent.");
    }

    private static bool SettledElsewhere(Observation observation, HeightExpectation point)
    {
        var d = observation.Data;
        float x = d.GetProperty("x").GetSingle(), z = d.GetProperty("z").GetSingle(), speed = d.GetProperty("speed").GetSingle();
        return float.IsFinite(x) && float.IsFinite(z) && float.IsFinite(speed) && speed <= .15f &&
            d.GetProperty("grounded").GetBoolean() && !d.GetProperty("attached").GetBoolean() &&
            !d.GetProperty("dead").GetBoolean() && !d.GetProperty("teleporting").GetBoolean() &&
            Math.Sqrt(Math.Pow(x - point.X, 2) + Math.Pow(z - point.Z, 2)) > 2;
    }

    /// <summary>
    /// Turns on god, ghost and debug modes for the local player (<c>cli_set_player_safety true</c>) and requires the game
    /// to read all three back. The command sets the modes rather than toggling them, so running it again changes nothing;
    /// it is issued once and an unconfirmed reply fails. Debug flying stays off, so support observations remain
    /// meaningful. <see cref="SessionControl.WaitForWorld"/> calls this by default once the world is ready.
    /// </summary>
    public static void Protect(GameActor client)
    {
        client.Execute("cli_set_player_safety true").RequireLine("OK: playerSafety enabled=True god=True ghost=True debugMode=True", "Player protection was not confirmed");
    }

    /// <summary>
    /// Turns the local player's debug fly on or off (<c>cli_fly on|off</c>, which sets rather than toggles) and requires
    /// the game to read the requested state back. Fly is for review and visual steps only, such as looking at a site from
    /// above: a flying player is never supported, so <see cref="Arrive"/> and <see cref="RequireSupported"/> refuse while
    /// it is on. Turn it off before measuring again.
    /// </summary>
    public static void SetFly(GameActor client, bool on)
    {
        client.Execute(on ? "cli_fly on" : "cli_fly off").RequireLine(on ? "OK: fly=True " : "OK: fly=False ", $"Fly {(on ? "on" : "off")} was not confirmed");
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
        GameReply reply;
        try
        {
            client.CommandTimeout = timeout + TimeSpan.FromSeconds(10);
            reply = client.Execute("cli_skip_intro " + timeout.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));
        }
        finally { client.CommandTimeout = previous; }
        if (reply.Line("OK: skipped=True") != null) return true;
        if (reply.Line("OK: skipped=False") != null) return false;
        throw new InvalidOperationException("The intro was not confirmed skipped. Reply: " + reply.Describe());
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
    /// no-op for a character that has spawned before. Then waits until the client's player has held still somewhere for
    /// <paramref name="settleFor"/> (default 3 s): the game refuses a teleport within 2 s of a spawn or of the previous
    /// teleport, silently, and the Valkyrie of a first-join intro overwrites the player's position every frame. Still, not
    /// grounded: a character that logged out where this world copy has water spawns swimming, and the game teleports a
    /// swimming player as readily as a standing one. With <paramref name="skipIntro"/> false the intro may still be running
    /// (the ride is not reported as attached, and its speed can read low), so the player must also be grounded. Then has the server teleport the only connected player to <paramref name="point"/> (a little
    /// above it, so the character settles onto the ground) exactly once, and waits on the client until its player stands
    /// settled there (<see cref="SurfaceProbe.Supported"/>). Returns the observation that established arrival. Refuses at
    /// once, before or after the teleport, if a reading shows the player flying (see <see cref="SetFly"/>). The one
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
        // Still anywhere, and for long enough: the game drops a teleport while the player is already teleporting and within
        // 2 s of a spawn or teleport (its cooldown); an attached player (a chair, a ship) or an unfinished observation waits too.
        TimeSpan? stillSince = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            last = client.Observe(support);
            RefuseFlying(last);
            if (!ReadyToTeleport(last, requireGrounded: !skipIntro)) stillSince = null;
            else if ((stillSince ??= clock.Elapsed) + still <= clock.Elapsed) break;
            if (clock.Elapsed >= timeout)
                throw new TimeoutException($"The player never stood still before the teleport within {timeout.TotalSeconds:F0} s; last reading: {last.Data.GetRawText()}. Nothing was teleported.");
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
        int peer = OnlyPeer(server);
        string at = string.Join(" ", new[] { point.X, point.Height + .5f, point.Z }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        server.Execute($"cli_teleport_peer {peer} {at}").RequireLine("OK: asked peer", "The server did not accept the teleport");
        while (clock.Elapsed < timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            last = client.Observe(support);
            RefuseFlying(last);
            if (last.Complete && SurfaceProbe.Supported(last, point)) return last.Data.Clone();
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
        throw new TimeoutException($"The player did not settle at ({point.X}, {point.Height}, {point.Z}) within {timeout.TotalSeconds:F0} s; last reading: " +
            (last == null ? "none" : last.Data.GetRawText()) + ". The teleport was not repeated.");
    }

    /// <summary>A flying player is never supported: measuring support or grounding while fly is on is refused, not failed.</summary>
    private static void RefuseFlying(Observation observation)
    {
        if (observation.Complete && observation.Data.TryGetProperty("flying", out var flying) && flying.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("The player is flying, so support cannot be measured. Fly is for review and visual steps only; turn it off first (SetFly(client, false)).");
    }

    // Grounded is not required when the intro was skipped: the game's teleport refuses only a player that is already
    // teleporting or within its cooldown, and a swimming player (grounded false, bobbing at a few centimetres a second) is
    // teleported like a standing one. With the intro possibly running, grounded is what shows the ride is over.
    private static bool ReadyToTeleport(Observation observation, bool requireGrounded)
    {
        if (!observation.Complete) return false;
        var d = observation.Data;
        return (!requireGrounded || d.GetProperty("grounded").GetBoolean()) && !d.GetProperty("attached").GetBoolean() && !d.GetProperty("teleporting").GetBoolean() &&
            !d.GetProperty("dead").GetBoolean() && d.GetProperty("speed").GetSingle() <= .15f;
    }

    /// <summary>
    /// <paramref name="readings"/> client observations <paramref name="interval"/> apart (default three, half a second),
    /// each of which must show the player settled on <paramref name="point"/>: stationary support, not walking usability.
    /// A reading that shows the player flying refuses the check (an <see cref="InvalidOperationException"/>, not a
    /// <see cref="SupportException"/>): support cannot be measured while fly is on.
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
            RefuseFlying(state);
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
