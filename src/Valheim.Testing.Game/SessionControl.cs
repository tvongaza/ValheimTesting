using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

public sealed record SessionState(string Phase, string? WorldUid, bool WorldPresent, bool WorldReady,
    bool Server, bool Dedicated, bool LocalPlayer, bool PlayerReady, bool Saving, bool LoadError, string ConnectionStatus)
{
    public static SessionState FromObservation(Observation observation)
    {
        observation.RequireComplete("session-state");
        var d = observation.Data;
        var state = new SessionState(d.GetProperty("phase").GetString()!, d.GetProperty("worldUid").GetString(),
            d.GetProperty("worldPresent").GetBoolean(), d.GetProperty("worldReady").GetBoolean(),
            d.GetProperty("server").GetBoolean(), d.GetProperty("dedicated").GetBoolean(),
            d.GetProperty("localPlayer").GetBoolean(), d.GetProperty("playerReady").GetBoolean(),
            d.GetProperty("saving").GetBoolean(), d.GetProperty("loadError").GetBoolean(), d.GetProperty("connectionStatus").GetString()!);
        if (state.Phase is not ("menu" or "loading" or "world-present" or "leaving" or "failed") || string.IsNullOrWhiteSpace(state.ConnectionStatus) ||
            state.WorldPresent != (state.WorldUid != null) || state.WorldUid != null && !long.TryParse(state.WorldUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            state.PlayerReady && !state.LocalPlayer || state.WorldReady && (!state.WorldPresent || state.LoadError || state.Phase != "world-present" || !state.Server && (!state.PlayerReady || state.ConnectionStatus != "Connected")))
            throw new InvalidOperationException("Inconsistent session observation.");
        return state;
    }
}

/// <summary>Actions affect an attached game, never launch or terminate its process. Mod readiness is separate.</summary>
public sealed class SessionControl(GameActor actor)
{
    public SessionState Read() => Read(actor.RequireCapability("valheim.session/state"));
    private SessionState Read(Capability capability) => SessionState.FromObservation(actor.Observe(capability));

    /// <summary>
    /// Waits until <paramref name="worldUid"/> is loaded and ready, polling the read-only session state. Then, unless
    /// <paramref name="protectPlayer"/> is false, protects the game's local player (<see cref="PlayerPlacement.Protect"/>:
    /// god, ghost and debug modes, read back), so a fall, the water or a mob between the join and the check cannot cost
    /// the character. A protection the game does not confirm fails the wait; it is issued once and never toggled. Debug
    /// fly stays off. A dedicated server has no local player and is left alone. A game hosting its own world reports
    /// ready before its player spawns, so the wait goes on, within the same timeout, until that player exists.
    /// <para>
    /// Protection changes gameplay, as the game does in 1.0.16: monsters neither notice nor target a player in ghost
    /// mode, an egg hatches only near a player who is not, and a creature hit by a player in god or ghost mode is marked
    /// cheated, which marks the items it drops. Pass false for combat, aggro, taming, hatching or loot checks, when a
    /// check needs a vulnerable player, or when an operator manages these modes; call <see cref="PlayerPlacement.Protect"/>
    /// yourself for the steps that should be protected.
    /// </para>
    /// </summary>
    public SessionState WaitForWorld(string worldUid, TimeSpan timeout, CancellationToken cancellation = default, bool protectPlayer = true)
    {
        ValidateWorldUid(worldUid);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var capability = actor.RequireCapability("valheim.session/state");
        var timer = Stopwatch.StartNew();
        bool lastReady = false;
        while (timer.Elapsed < timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            var state = Read(capability);
            lastReady = state.WorldReady;
            if (state.LoadError) throw new InvalidOperationException("The game reports a world load error.");
            if (state.WorldPresent && state.WorldUid != worldUid) throw new InvalidOperationException($"A different world is loaded: UID {state.WorldUid}, expected {worldUid}. The world loaded, so this is the wrong world (another fixture or a fresh one), not a load failure.");
            if (state.WorldReady)
            {
                if (!protectPlayer || state.Dedicated) return state;
                if (state.LocalPlayer) { PlayerPlacement.Protect(actor); return state; }
                // A hosting game: its world is ready before its own player spawns. Wait for the player.
            }
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(0, (timeout - timer.Elapsed).TotalMilliseconds))));
        }
        if (lastReady)
            throw new TimeoutException("The world is ready but its local player did not spawn, so it was not protected; no action was retried. Pass protectPlayer: false to wait for the world alone.");
        throw new TimeoutException("World readiness was not established; no action was retried.");
    }

    /// <summary>
    /// Turns the attached game's devcommands on and requires the game's reply to say so. The console command toggles and
    /// reports the resulting state, so a reply of off is toggled once more. ValheimCLI refuses mutating extension
    /// commands, the session join among them, until devcommands is on, and a fresh game starts with it off. It is the
    /// game's local flag: on a client joined to a server, cheats also need that server's admin list.
    /// </summary>
    public void EnableDevcommands()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string reply = string.Join(" ", actor.Execute("devcommands").Output);
            if (Regex.IsMatch(reply, @"Dev ?commands:\s*True", RegexOptions.IgnoreCase)) return;
            if (!Regex.IsMatch(reply, @"Dev ?commands:\s*False", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Unrecognised devcommands reply: " + reply);
        }
        throw new InvalidOperationException("Devcommands stayed off after two toggles.");
    }

    /// <summary>
    /// Joins a server. Turns devcommands on first unless <paramref name="enableDevcommands"/> is false, because the
    /// join is refused without it; pass false when an operator manages that flag and the join should fail instead.
    /// Credentials stay in the game host's environment, never in command text or transcripts. The join invalidates the
    /// actor's pins; verify the destination world, then <see cref="WaitForWorld"/>, which protects the joined player.
    /// </summary>
    public void Join(string address, string character, string? passwordEnvironmentVariable = null, bool enableDevcommands = true)
    {
        if (enableDevcommands) EnableDevcommands();
        var args = passwordEnvironmentVariable == null ? new[] { address, character } : new[] { address, character, passwordEnvironmentVariable };
        Transition("join", args);
    }

    /// <summary>Before a prepared-save join, require ValheimCLI to select this exact local filename, never a cloud
    /// character or a display-name collision. Run with devcommands enabled while still at the menu.</summary>
    public void RequireLocalCharacter(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Any(char.IsWhiteSpace))
            throw new ArgumentException("Give a single character filename.", nameof(filename));
        var selected = actor.Execute("cli_select_character " + filename, requireSuccess: false);
        RequireSelectedLocal(selected, filename);
    }

    private static void RequireSelectedLocal(CommandResult selected, string filename)
    {
        if (!selected.Output.Any(line => line.StartsWith("OK: Selected character '", StringComparison.Ordinal) &&
            line.EndsWith(" (" + filename + ", Local)", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The prepared character was not selected as the exact local file " + filename + ": " + string.Join(" | ", selected.Output));
    }

    /// <summary>
    /// Joins a server that must refuse this client with <paramref name="expected"/>, for example
    /// <see cref="GameConnectionStatus.ErrorVersion"/> (3) from a mod's version check. Issues the session join exactly
    /// once (devcommands first, as <see cref="Join"/>), requires ValheimCLI to report that the game refused it
    /// (<c>join_failed</c>: the new connection ended in an error status), re-pins the client with
    /// <paramref name="menuExpectations"/> (plugins only, such as <see cref="ClientRunPlan.MenuExpectations"/>), waits within
    /// <paramref name="timeout"/> until the client is back at its main menu, then reads <c>cli_connection_status</c>, which
    /// the game keeps until the next join, and requires the expected status. Fails when the join succeeds (the client is
    /// then in the server's world: leave it before another join), when it ends any other way (another ValheimCLI code, a
    /// timeout or a lost reply: nothing is retried) and when the refusal has another status. On success the client is at
    /// its menu, pinned, and a normal <see cref="Join"/> may follow.
    /// </summary>
    public JoinRefusal JoinExpectingRefusal(string address, string character, GameConnectionStatus expected, string menuExpectations, TimeSpan timeout,
        string? passwordEnvironmentVariable = null, bool enableDevcommands = true, CancellationToken cancellation = default)
    {
        if (!ConnectionStatusReading.IsRefusal(expected))
            throw new ArgumentException($"Expect one of the game's refusal statuses (ErrorVersion, ErrorPassword, ErrorBanned, ...), not {expected}.", nameof(expected));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentException.ThrowIfNullOrEmpty(menuExpectations);
        if (enableDevcommands) EnableDevcommands();
        var capability = actor.RequireCapability("valheim.session/join");
        if (capability.ReadOnly) throw new InvalidOperationException("Session action was incorrectly advertised as read-only.");
        var arguments = passwordEnvironmentVariable == null ? new[] { address, character } : new[] { address, character, passwordEnvironmentVariable };
        if (arguments.Any(x => x.Length == 0 || x.Any(char.IsWhiteSpace))) throw new ArgumentException("Extension arguments must be single tokens in preview 1.");
        bool ok; string? code;
        var previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = TimeSpan.FromSeconds(130);
            // Exactly once, and a refusal is the expected outcome, so the reply is read here rather than required to succeed.
            var reply = actor.Execute("cli_extension " + capability.Path + " " + string.Join(" ", arguments), requireSuccess: false);
            using var document = GameActor.ParseLine(reply, "EXTENSION_RESULT ");
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != capability.SchemaVersion || root.GetProperty("instance").GetString() != capability.Instance ||
                root.GetProperty("extension").GetString() != "valheim.session") throw new InvalidOperationException("Extension changed; explicitly rediscover capabilities after reload.");
            ok = root.GetProperty("ok").GetBoolean();
            code = ok ? null : root.TryGetProperty("code", out var value) ? value.GetString() : null;
        }
        finally
        {
            actor.CommandTimeout = previous;
            actor.InvalidateEnvironment(); // Even a failed/ambiguous transition can have changed the world.
        }
        if (ok) throw new InvalidOperationException($"The join succeeded, but a refusal with {expected} ({(int)expected}) was expected. The client is in the server's world: leave it before joining again.");
        if (code != "join_failed")
            throw new InvalidOperationException($"The join ended with ValheimCLI code {code ?? "none"}, not a refusal by the server; {expected} ({(int)expected}) was expected. Nothing was retried.");
        actor.VerifyEnvironment(menuExpectations);
        var clock = Stopwatch.StartNew();
        var state = actor.RequireCapability("valheim.session/state");
        SessionState reading;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            reading = Read(state);
            if (reading.Phase == "menu" && !reading.WorldPresent) break;
            if (clock.Elapsed >= timeout)
                throw new WaitTimeoutException("the refused client back at its main menu", clock.Elapsed, $"phase {reading.Phase}, connection {reading.ConnectionStatus}");
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(0, (timeout - clock.Elapsed).TotalMilliseconds))));
        }
        var status = ConnectionStatusReading.Read(actor);
        if (status.Status != expected)
            throw new InvalidOperationException($"The join was refused with {status}, not the expected {expected} ({(int)expected}).");
        return new JoinRefusal(status.Status, status.Server, clock.Elapsed);
    }

    /// <summary>
    /// Joins a crossplay (PlayFab) server's lobby with the given existing character: <c>cli_select_character</c>, then
    /// <c>cli_connect_playfab_user &lt;remotePlayerId&gt;</c> exactly once (devcommands first, as <see cref="Join"/>), each
    /// reply read back. <paramref name="remotePlayerId"/> comes from the server (<see cref="CrossplayServer.WaitForLobby"/>).
    /// No password is passed: the command would carry it as text, so a crossplay fixture server runs private without one.
    /// The command only starts the join, so this then re-pins the client with <paramref name="menuExpectations"/> (plugins
    /// only) and waits, reading the session state, until the client is connected in <paramref name="worldUid"/> with its
    /// player ready, as the session join does. Fails when the game reports a load error or another world, or returns to
    /// the menu with an error status after the join started. Then verify the world pins and <see cref="WaitForWorld"/>.
    /// A strict client refuses its menu pins as soon as the join loads the world between two reads ("loaded but not
    /// listed"); with <paramref name="worldExpectations"/> (the plugins and the server's world, as
    /// <see cref="ClientRunPlan.WorldExpectations"/> gives them) the client is then re-pinned to that world, once, and the wait
    /// goes on; another world fails that pin check. The same holds when the join loads the world before the menu pins are
    /// first checked. Found in the native crossplay run: the join completes within a read.
    /// </summary>
    public SessionState JoinCrossplay(string remotePlayerId, string character, string worldUid, string menuExpectations, TimeSpan timeout,
        bool enableDevcommands = true, CancellationToken cancellation = default, string? worldExpectations = null, string? requiredLocalFilename = null)
    {
        ValidateWorldUid(worldUid);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentException.ThrowIfNullOrEmpty(menuExpectations);
        foreach (string token in new[] { remotePlayerId, character })
            if (string.IsNullOrEmpty(token) || token.Any(char.IsWhiteSpace)) throw new ArgumentException("The remote player id and character must be single tokens.");
        var capability = actor.RequireCapability("valheim.session/state");
        var before = Read(capability);
        if (before.Phase != "menu" || before.WorldPresent) throw new InvalidOperationException("A crossplay join starts from the client's idle main menu.");
        if (enableDevcommands) EnableDevcommands();
        var selected = actor.Execute("cli_select_character " + character, requireSuccess: false);
        if (!selected.Output.Any(line => line.StartsWith("OK: Selected character '", StringComparison.Ordinal)))
            throw new InvalidOperationException("The character was not selected: " + string.Join(" | ", selected.Output));
        if (requiredLocalFilename != null) RequireSelectedLocal(selected, requiredLocalFilename);
        try
        {
            var started = actor.Execute("cli_connect_playfab_user " + remotePlayerId, requireSuccess: false); // Exactly once.
            if (!started.Output.Any(line => line.StartsWith($"OK: PlayFab user join started for {remotePlayerId} using ", StringComparison.Ordinal)))
                throw new InvalidOperationException("The crossplay join did not start: " + string.Join(" | ", started.Output));
        }
        finally { actor.InvalidateEnvironment(); } // A join that may have started can change the world.
        bool worldPinned = false;
        try
        {
            actor.VerifyEnvironment(menuExpectations);
            capability = actor.RequireCapability("valheim.session/state");
        }
        catch (Exception error) when (worldExpectations != null && LoadedButNotListed(error))
        {
            // The join loaded the world before the menu pins were checked.
            actor.VerifyEnvironment(worldExpectations); // Throws for another world than the server's.
            worldPinned = true;
            capability = actor.RequireCapability("valheim.session/state");
        }
        var clock = Stopwatch.StartNew();
        bool left = false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            SessionState state;
            try { state = Read(capability); }
            catch (Exception error) when (!worldPinned && worldExpectations != null && LoadedButNotListed(error))
            {
                actor.VerifyEnvironment(worldExpectations); // Throws for another world than the server's.
                worldPinned = true;
                continue;
            }
            if (state.LoadError) throw new InvalidOperationException("The game reports a world load error.");
            if (state.WorldPresent && state.WorldUid != worldUid) throw new InvalidOperationException("A different world is loaded.");
            if (state.WorldPresent && state.PlayerReady && state.ConnectionStatus == "Connected") return state;
            // A status left over from an earlier join stays until this one connects, so an error counts only once the menu was left.
            left |= state.Phase != "menu" || state.ConnectionStatus == "Connecting";
            if (left && state.Phase == "menu" && state.ConnectionStatus.StartsWith("Error", StringComparison.Ordinal))
                throw new InvalidOperationException($"The crossplay join failed: the client is back at its menu with {state.ConnectionStatus}. Nothing was retried.");
            if (clock.Elapsed >= timeout)
                throw new WaitTimeoutException("the crossplay join", clock.Elapsed, $"phase {state.Phase}, connection {state.ConnectionStatus}");
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(0, (timeout - clock.Elapsed).TotalMilliseconds))));
        }
    }

    // ValheimCLI's strict cli_expect refusal once a world is loaded that the pins do not list.
    internal static bool LoadedButNotListed(Exception error) => error.Message.Contains("is loaded but not listed (strict)", StringComparison.Ordinal);

    public void Leave() => Transition("leave", []);
    private void Transition(string action, string[] arguments)
    {
        var capability = actor.RequireCapability("valheim.session/" + action);
        if (capability.ReadOnly) throw new InvalidOperationException("Session action was incorrectly advertised as read-only.");
        var previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = TimeSpan.FromSeconds(130);
            var result = actor.Invoke(capability, arguments); // Exactly once. A lost reply is an unknown outcome.
            RequireResult(result, "session-" + action);
            if (result.GetProperty("action").GetString() != action) throw new InvalidOperationException("Wrong session action result.");
        }
        finally
        {
            actor.CommandTimeout = previous;
            actor.InvalidateEnvironment(); // Even a failed/ambiguous transition can have changed the world.
        }
    }
    public uint Save(string worldUid, TimeSpan timeout)
    {
        ValidateWorldUid(worldUid);
        if (timeout.TotalSeconds < 1 || timeout.TotalSeconds > 600) throw new ArgumentOutOfRangeException(nameof(timeout));
        var state = Read();
        if (!state.Server || !state.WorldReady || state.WorldUid != worldUid) throw new InvalidOperationException("The requested server world is not ready to save.");
        var capability = actor.RequireCapability("valheim.session/save");
        if (capability.ReadOnly) throw new InvalidOperationException("Save was incorrectly advertised as read-only.");
        var previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = timeout + TimeSpan.FromSeconds(10);
            var data = actor.Invoke(capability, timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            RequireResult(data, "session-save");
            uint before = data.GetProperty("before").GetUInt32(), after = data.GetProperty("after").GetUInt32();
            if (data.GetProperty("worldUid").GetString() != worldUid || !data.GetProperty("saved").GetBoolean() || before == after)
                throw new InvalidOperationException("The game did not confirm a completed save for this world.");
            return after;
        }
        finally { actor.CommandTimeout = previous; }
    }
    private static void ValidateWorldUid(string value)
    { if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw new ArgumentException("An exact world UID is required."); }
    private static void RequireResult(JsonElement data, string source)
    { if (data.GetProperty("source").GetString() != source || !data.GetProperty("complete").GetBoolean()) throw new InvalidOperationException("Session result is incomplete or has the wrong source."); }
}
