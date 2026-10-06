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
        // A hosting game's world is ready before its own player spawns, so a protected wait goes on until that player exists.
        var state = ObservedWait.Until(protectPlayer ? $"world {worldUid} ready with its local player (to protect it)" : $"world {worldUid} ready",
            () => Read(capability), s => s.WorldReady && (!protectPlayer || s.Dedicated || s.LocalPlayer), timeout, ReadInterval, cancellation,
            fails: s => s.LoadError ? "the game reports a world load error" : s.WorldPresent && s.WorldUid != worldUid
                ? $"a different world is loaded: UID {s.WorldUid}, expected {worldUid}. The world loaded, so this is the wrong world (another fixture or a fresh one), not a load failure" : null,
            describe: s => $"phase {s.Phase}, world {s.WorldUid ?? "none"}, ready {s.WorldReady}, local player {s.LocalPlayer}, connection {s.ConnectionStatus}" +
                (s.WorldReady && s.WorldUid == worldUid ? ": the world is ready but its local player did not spawn, so it was not protected. Pass protectPlayer: false to wait for the world alone" : "") +
                "; no action was retried");
        if (protectPlayer && !state.Dedicated) PlayerPlacement.Protect(actor);
        return state;
    }
    // Session state has no push the runner can wait on (ValheimCLI's state is a scene fact, not the session's readiness).
    private static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Turns the client's devcommands on at its menu: <see cref="TestAccess.Ensure"/> as <see cref="TestActorRole.ClientMenu"/>,
    /// which reads ValheimCLI's <c>cli_access</c>, toggles once only if needed and verifies the result. ValheimCLI refuses
    /// mutating extension commands, the session join among them, until devcommands is on, and a fresh game starts with it
    /// off. Cheats are acknowledged after joining (<see cref="ClientRounds"/> does it for an owned client). On a client joined
    /// to a server, Valheim 1.0 also needs ValheimCLI's <c>AllowOnServerClients = true</c> for mutating test commands.
    /// </summary>
    public void EnableDevcommands() => TestAccess.Ensure(actor, TestActorRole.ClientMenu);

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

    /// <summary>
    /// Joins a client plan's disposable character to the owned dedicated server whose world is <paramref name="worldUid"/>:
    /// the one join that <see cref="ClientRounds"/>, <see cref="LogoutCycle"/> and a scenario that joins several clients
    /// itself all use. Devcommands first and the join exactly once, by the plan's address (<see cref="Join"/>) or, for a
    /// <see cref="ClientRunPlan.Crossplay"/> plan, into the server's <paramref name="lobby"/> (<see cref="JoinCrossplay"/>);
    /// then the client's world pins (<see cref="ClientRunPlan.WorldExpectations"/>) are verified and the world awaited within
    /// the plan's join seconds. An owned client then gets test access on its disposable character
    /// (<see cref="TestAccess.Ensure"/> as <see cref="TestActorRole.ClientInWorld"/>, requiring ValheimCLI's
    /// <c>AllowOnServerClients</c> when the player is protected, since protection is a mutating command there); an operator's
    /// attached client keeps devcommands only. Last, unless <paramref name="protectPlayer"/> is false, the player is protected
    /// (<see cref="PlayerPlacement.Protect"/>; see <see cref="WaitForWorld"/> for what protection changes).
    /// </summary>
    public SessionState JoinWorld(ClientRunPlan plan, string worldUid, CrossplayLobby? lobby = null, bool protectPlayer = true, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateWorldUid(worldUid);
        if (plan.HostWorld != null) throw new ArgumentException("This client hosts its own world (hostWorld); it joins no server.", nameof(plan));
        if (plan.JoinsHost) throw new ArgumentException("This client is a host's peer (joinsHost): it joins the hosting client with JoinHost.", nameof(plan));
        if (plan.Crossplay != (lobby != null))
            throw new ArgumentException(plan.Crossplay ? "A crossplay client joins the server's lobby: pass it (CrossplayServer.WaitForLobby)." : "This client joins by address; a lobby is for a crossplay plan.", nameof(lobby));
        var timeout = TimeSpan.FromSeconds(plan.JoinSeconds);
        if (lobby != null)
            JoinCrossplay(lobby.RemotePlayerId, plan.Character, worldUid, plan.MenuExpectations, timeout, cancellation: cancellation,
                worldExpectations: plan.WorldExpectations(worldUid));
        else Join(plan.Join, plan.Character, plan.PasswordVariable); // Exactly once.
        return Joined(plan, worldUid, timeout, protectPlayer, cancellation);
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
            var reply = actor.Execute("cli_extension " + capability.Path + " " + string.Join(" ", arguments), requireAccepted: false);
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
        var state = actor.RequireCapability("valheim.session/state");
        var back = ObservedWait.RunBlocking("the refused client back at its main menu", _ => Read(state), reading => reading.Phase == "menu" && !reading.WorldPresent,
            timeout, ReadInterval, cancellation, describe: reading => $"phase {reading.Phase}, connection {reading.ConnectionStatus}");
        var status = ConnectionStatusReading.Read(actor);
        if (status.Status != expected)
            throw new InvalidOperationException($"The join was refused with {status}, not the expected {expected} ({(int)expected}).");
        return new JoinRefusal(status.Status, status.Server, back.Elapsed);
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
        bool enableDevcommands = true, CancellationToken cancellation = default, string? worldExpectations = null)
    {
        foreach (string token in new[] { remotePlayerId, character })
            if (string.IsNullOrEmpty(token) || token.Any(char.IsWhiteSpace)) throw new ArgumentException("The remote player id and character must be single tokens.");
        return JoinUser("cli_connect_playfab_user " + remotePlayerId, $"OK: PlayFab user join started for {remotePlayerId} using ", "crossplay join", character, worldUid,
            menuExpectations, timeout, enableDevcommands, cancellation, worldExpectations);
    }

    /// <summary>
    /// Joins a client plan's disposable character, a host's peer (<see cref="ClientRunPlan.JoinsHost"/>), to the world a hosting
    /// client hosts (<c>HostingClientActor</c>), whose in-game handle is <paramref name="host"/> and world
    /// <paramref name="worldUid"/>. A listen server on the game's Steam backend is reached through Steam rather than at an address,
    /// so this reads the host's multiplayer identity (<c>cli_multiplayer_identity</c>, read-only: it must be an open server), then
    /// joins the host's Steam user once (<c>cli_connect_steam_user &lt;steamId&gt;</c>, after <c>cli_select_character</c>, with
    /// devcommands first), and waits as a crossplay join does until the client is connected in the host's world with its player.
    /// Then, as <see cref="JoinWorld"/>: the world pins, the world awaited within the plan's join time, test access on an owned
    /// client's character, and protection unless <paramref name="protectPlayer"/> is false. A crossplay (PlayFab) host is refused:
    /// its peer join is not supported yet.
    /// </summary>
    public SessionState JoinHost(ClientRunPlan plan, GameActor host, string worldUid, bool protectPlayer = true, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(host);
        ValidateWorldUid(worldUid);
        if (!plan.JoinsHost) throw new ArgumentException("This client joins a server by its address or lobby (JoinWorld); a host's peer sets joinsHost.", nameof(plan));
        if (string.IsNullOrEmpty(plan.Character) || plan.Character.Any(char.IsWhiteSpace)) throw new ArgumentException("The character must be a single token.", nameof(plan));
        var identity = MultiplayerIdentity.Read(host);
        if (!identity.IsServer || !identity.IsOpenServer)
            throw new InvalidOperationException($"The host is not an open server (isServer={identity.IsServer}, isOpenServer={identity.IsOpenServer}); host its world before a peer joins.");
        if (identity.Backend != "Steamworks")
            throw new InvalidOperationException($"The host runs on the {identity.Backend} backend; a peer joins only a Steam host (hostWorld.crossplay false) so far.");
        // A signed-in Steam user's ID has 17 digits (as SteamAccountHolds reads it); 0 or "unavailable" is a host without Steam.
        if (identity.SteamId.Length != 17 || !identity.SteamId.All(char.IsAsciiDigit))
            throw new InvalidOperationException($"The host's Steam ID is {identity.SteamId}, not a signed-in Steam user's, so no peer can join it; sign the host's Steam in.");
        var timeout = TimeSpan.FromSeconds(plan.JoinSeconds);
        JoinUser("cli_connect_steam_user " + identity.SteamId, $"OK: Steam user join started for {identity.SteamId} using ", "host join", plan.Character, worldUid,
            plan.MenuExpectations, timeout, enableDevcommands: true, cancellation, plan.WorldExpectations(worldUid));
        return Joined(plan, worldUid, timeout, protectPlayer, cancellation);
    }

    // After the one join: the world pins, the world awaited, test access on an owned client's character, then protection.
    private SessionState Joined(ClientRunPlan plan, string worldUid, TimeSpan timeout, bool protectPlayer, CancellationToken cancellation)
    {
        actor.VerifyEnvironment(plan.WorldExpectations(worldUid)); // A transition always needs fresh pins.
        var state = WaitForWorld(worldUid, timeout, cancellation, protectPlayer: false); // A joined client's world is ready with its player.
        // Protection is a mutating test command on a joined client: an owned client's access is established first and must
        // allow it (AllowOnServerClients), so a client staged without it is named here rather than by a refused command.
        if (plan.Owned) TestAccess.Ensure(actor, TestActorRole.ClientInWorld, clientMutations: protectPlayer);
        if (protectPlayer) PlayerPlacement.Protect(actor);
        return state;
    }

    // A join to another player's game: the character selected, the join command issued exactly once and its start confirmed, then
    // the wait until the client is connected in worldUid with its player (crossplay lobby or Steam host alike).
    private SessionState JoinUser(string command, string started, string what, string character, string worldUid, string menuExpectations, TimeSpan timeout,
        bool enableDevcommands, CancellationToken cancellation, string? worldExpectations)
    {
        ValidateWorldUid(worldUid);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentException.ThrowIfNullOrEmpty(menuExpectations);
        var capability = actor.RequireCapability("valheim.session/state");
        var before = Read(capability);
        if (before.Phase != "menu" || before.WorldPresent) throw new InvalidOperationException($"A {what} starts from the client's idle main menu.");
        if (enableDevcommands) EnableDevcommands();
        var selected = actor.Execute("cli_select_character " + character);
        selected.RequireLine("OK: Selected character '", "The character was not selected");
        try
        {
            actor.Execute(command) // Exactly once.
                .RequireLine(started, $"The {what} did not start");
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
        bool left = false;
        return ObservedWait.Until("the " + what, () =>
            {
                SessionState state;
                try { state = Read(capability); }
                catch (Exception error) when (!worldPinned && worldExpectations != null && LoadedButNotListed(error))
                {
                    actor.VerifyEnvironment(worldExpectations); // Throws for another world than the server's.
                    worldPinned = true;
                    state = Read(capability);
                }
                // A status left over from an earlier join stays until this one connects, so an error counts only once the menu was left.
                left |= state.Phase != "menu" || state.ConnectionStatus == "Connecting";
                return state;
            },
            state => state.WorldPresent && state.PlayerReady && state.ConnectionStatus == "Connected", timeout, ReadInterval, cancellation,
            fails: state => state.LoadError ? "the game reports a world load error" : state.WorldPresent && state.WorldUid != worldUid ? "a different world is loaded"
                : left && state.Phase == "menu" && state.ConnectionStatus.StartsWith("Error", StringComparison.Ordinal)
                    ? $"the {what} failed: the client is back at its menu with {state.ConnectionStatus}. Nothing was retried" : null,
            describe: state => $"phase {state.Phase}, connection {state.ConnectionStatus}");
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
