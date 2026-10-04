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
    /// <summary>What <see cref="Arrive"/> needs from the client's ValheimCLI: the Standard pack's bounded teleport commands
    /// (<c>cli_wait_teleportable</c>, <c>cli_teleport_trace_arm</c>/<c>cli_teleport_trace_wait</c>), World Tools' support
    /// wait, and its support reading (the flying check). <see cref="ClientRounds"/> checks them for an arriving client.</summary>
    public static readonly IReadOnlyList<string> ArrivalCapabilities = [CliCapabilities.TeleportSignals, "valheim.world/player-support-wait", "valheim.world/player-support"];

    /// <summary>One arrival: the client's supported landing, and the teleport's game-reported phase times and raw trace line.</summary>
    public sealed record TeleportArrival(JsonElement Support, string Trace, TeleportTrace Timing);

    /// <summary>
    /// Has the server teleport the only connected player (<see cref="OnlyPeer"/>) to <paramref name="point"/>, a little
    /// above it so the character settles onto the ground, exactly once, at the game's ordinary teleport timing, and returns
    /// once the client's own observation shows the player supported there (<see cref="SurfaceProbe.Supported"/>). Unless
    /// <paramref name="skipIntro"/> is false it first ends a first-join intro (<see cref="SkipIntro"/>), a no-op for a
    /// character that has spawned before. Each transition is one bounded wait inside the game, not repeated remote reads:
    /// ValheimCLI waits until the player can be teleported (the game silently drops a teleport within 2 s of a spawn or of
    /// the previous teleport), arms a one-hop trace, and after the server's one request waits for the teleport to finish
    /// with a ready floor, then for supported arrival. A player shown flying is refused before the teleport (see
    /// <see cref="SetFly"/>). <paramref name="timeout"/> (at most 600 s) covers the intro and every wait; each CLI wait is
    /// capped at 120 s. Nothing is retried: a lost reply is an unknown outcome, not a failure to act. Needs
    /// <see cref="ArrivalCapabilities"/> on the client.
    /// </summary>
    public static TeleportArrival Arrive(GameActor server, GameActor client, HeightExpectation point,
        TimeSpan timeout, CancellationToken cancellation = default, bool skipIntro = true)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(600)) throw new ArgumentOutOfRangeException(nameof(timeout));
        TerrainProbe.Validate("loaded-ground", "arrival point", [point], .3f);
        // One listing; a missing pack is named before anything is sent.
        var capabilities = client.RequireCapabilities(ArrivalCapabilities);
        Capability support = capabilities[1], reading = capabilities[2];
        var clock = Stopwatch.StartNew();
        if (skipIntro) SkipIntro(client, TimeSpan.FromSeconds(Math.Clamp(Math.Floor(timeout.TotalSeconds), 1, 60)));
        cancellation.ThrowIfCancellationRequested();
        // One read, not a wait: a flying player is never supported, so refuse before anything moves.
        RefuseFlying(client.Observe(reading));
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
            throw new InvalidOperationException("The game ended its teleport without a ready floor: " + trace + ". The teleport was not repeated.");
        cancellation.ThrowIfCancellationRequested();
        Observation landed = null!;
        WithTimeout(client, timeout - clock.Elapsed, () => landed = client.Observe(support,
            point.X.ToString("R", CultureInfo.InvariantCulture), point.Height.ToString("R", CultureInfo.InvariantCulture),
            point.Z.ToString("R", CultureInfo.InvariantCulture), SecondsLeft(clock, timeout)));
        RefuseFlying(landed);
        if (!SurfaceProbe.Supported(landed, point))
            throw new InvalidOperationException($"The player did not settle at ({point.X}, {point.Height}, {point.Z}); the client's wait ended with: {landed.Data.GetRawText()}. The teleport was not repeated.");
        return new TeleportArrival(landed.Data.Clone(), trace, timing);
    }

    private static string SecondsLeft(Stopwatch clock, TimeSpan timeout)
    {
        double left = (timeout - clock.Elapsed).TotalSeconds;
        if (left <= 0) throw new TimeoutException("The arrival deadline expired; the teleport was not repeated.");
        return Math.Min(120, left).ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WithTimeout(GameActor actor, TimeSpan remaining, Action action)
    {
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("The arrival deadline expired; the teleport was not repeated.");
        var previous = actor.CommandTimeout;
        try { actor.CommandTimeout = remaining + TimeSpan.FromSeconds(10); action(); }
        finally { actor.CommandTimeout = previous; }
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

    /// <summary>A flying player is never supported: measuring support or grounding while fly is on is refused, not failed.</summary>
    private static void RefuseFlying(Observation observation)
    {
        if (observation.Complete && observation.Data.TryGetProperty("flying", out var flying) && flying.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("The player is flying, so support cannot be measured. Fly is for review and visual steps only; turn it off first (SetFly(client, false)).");
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
