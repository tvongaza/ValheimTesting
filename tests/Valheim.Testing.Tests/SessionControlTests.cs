using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

public class SessionControlTests
{
    private sealed class Fake : IGameTransport
    {
        public List<string> Commands = [];
        public string World = "7";
        public bool Ready = true, FailTransition, AdvanceSave = true, Devcommands;
        public string? DevcommandsReply;
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            Commands.Add(command);
            if (command.StartsWith("cli_expect ")) return new() { Ok = true, Output = ["OK: EXPECT"] };
            if (command == "devcommands") { Devcommands = !Devcommands; return new() { Ok = true, Output = [DevcommandsReply ?? "Dev commands: " + Devcommands] }; }
            // ValheimCLI's access observation for a client at its menu (TestAccess).
            if (command == "cli_access") return new() { Ok = true, Output = ["ACCESS " + JsonSerializer.Serialize(new { schemaVersion = 1, complete = true, devcommands = Devcommands, cheatsAcknowledged = false, allowOnServerClients = true, server = false, dedicated = false, joinedClient = false, localPlayer = false, profileAvailable = false })] };
            // ValheimCLI refuses the join (a mutating extension command) until devcommands is on.
            if (!Devcommands && command.StartsWith("cli_extension valheim.session/join"))
                return new() { Ok = true, Output = ["EXTENSION_RESULT " + JsonSerializer.Serialize(new { schemaVersion = 1, ok = false, extension = "valheim.session", instance = "a", code = "extension_precondition", message = "Enable devcommands for mutating extension commands.", data = new { } })] };
            if (command == "cli_extensions") return new() { Ok = true, Output = ["EXTENSIONS " + JsonSerializer.Serialize(new { apiVersion = 1, extensions = new[] { new { id = "valheim.session", instance = "a", closing = false, commands = new[] { "state", "join", "leave", "save" }.Select(name => new { name, resultVersion = 1, readOnly = name == "state" }) } } })] };
            string action = command.Split(' ')[1].Split('/')[1];
            if (FailTransition && action is "join" or "leave") return new() { Ok = false, ErrorCode = "lost", Message = "reply lost" };
            object data = action switch {
                "state" => new { source = "session-state", complete = true, phase = Ready ? "world-present" : "loading", worldUid = World, worldPresent = true, worldReady = Ready, server = true, dedicated = true, localPlayer = false, playerReady = false, saving = false, loadError = false, connectionStatus = "None" },
                "save" => new { source = "session-save", complete = true, worldUid = World, saved = true, before = 8u, after = AdvanceSave ? 9u : 8u, milliseconds = 10 },
                _ => new { source = "session-" + action, complete = true, action }
            };
            return new() { Ok = true, Output = ["EXTENSION_RESULT " + JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, extension = "valheim.session", instance = "a", data })] };
        }
        public void Dispose() { }
        public GameActor Actor() { var actor = new GameActor("test", this); actor.VerifyEnvironment("cli_expect worlduid=7"); return actor; }
    }
    [Fact] public void SaveIsIssuedOnceAndRequiresAnAdvancedCounter()
    {
        var fake = new Fake(); using var actor = fake.Actor(); var session = new SessionControl(actor);
        Assert.Equal(9u, session.Save("7", TimeSpan.FromSeconds(10)));
        Assert.Single(fake.Commands.Where(x => x.StartsWith("cli_extension valheim.session/save")));
        fake.AdvanceSave = false; Assert.Throws<InvalidOperationException>(() => session.Save("7", TimeSpan.FromSeconds(10)));
    }
    [Fact] public void WrongWorldRefusesBeforeMutation()
    {
        var fake = new Fake { World = "8" }; using var actor = fake.Actor(); var session = new SessionControl(actor);
        Assert.Throws<InvalidOperationException>(() => session.Save("7", TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("cli_extension valheim.session/save"));
        Assert.Throws<InvalidOperationException>(() => session.WaitForWorld("7", TimeSpan.FromSeconds(1)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void TransitionInvalidatesPinsEvenWhenReplyIsLost(bool lost)
    {
        var fake = new Fake { FailTransition = lost }; using var actor = fake.Actor(); var session = new SessionControl(actor);
        if (lost) Assert.Throws<InvalidOperationException>(() => session.Join("localhost:2456", "Tester", "TEST_PASSWORD"));
        else session.Join("localhost:2456", "Tester", "TEST_PASSWORD");
        Assert.Single(fake.Commands.Where(x => x.StartsWith("cli_extension valheim.session/join")));
        Assert.Throws<InvalidOperationException>(() => session.Read());
        actor.VerifyEnvironment("cli_expect worlduid=7"); Assert.True(session.Read().WorldReady);
    }
    [Fact] public void ReadinessWaitOnlyPollsAndDoesNotResubmitMutations()
    {
        var fake = new Fake { Ready = false }; using var actor = fake.Actor();
        Assert.Throws<TimeoutException>(() => new SessionControl(actor).WaitForWorld("7", TimeSpan.FromMilliseconds(20)));
        Assert.All(fake.Commands.Where(x => x.StartsWith("cli_extension ")), x => Assert.Equal("cli_extension valheim.session/state", x));
    }
    // TestAccess reads the state first, so devcommands already on are left alone (the English reply used to need a second toggle).
    [Theory] [InlineData(false, 1)] [InlineData(true, 0)]
    public void JoinTurnsDevcommandsOnFirstWhateverTheirState(bool alreadyOn, int toggles)
    {
        var fake = new Fake { Devcommands = alreadyOn }; using var actor = fake.Actor();
        new SessionControl(actor).Join("localhost:2456", "Tester");
        Assert.True(fake.Devcommands);
        Assert.Equal(toggles, fake.Commands.Count(x => x == "devcommands"));
        Assert.True(fake.Commands.LastIndexOf("cli_access") < fake.Commands.FindIndex(x => x.StartsWith("cli_extension valheim.session/join")));
    }
    [Fact] public void AnUnrecognisedDevcommandsReplyStopsBeforeTheJoin()
    {
        var fake = new Fake { DevcommandsReply = "Unknown command devcommands" }; using var actor = fake.Actor();
        Assert.Equal("devcommands was refused: Unknown command devcommands", Assert.Throws<InvalidOperationException>(() => new SessionControl(actor).Join("localhost:2456", "Tester")).Message);
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("cli_extension valheim.session/join"));
    }
    [Fact] public void AJoinLeftToTheOperatorsDevcommandsFailsWhenTheyAreOff()
    {
        var fake = new Fake(); using var actor = fake.Actor();
        Assert.Throws<InvalidOperationException>(() => new SessionControl(actor).Join("localhost:2456", "Tester", enableDevcommands: false));
        Assert.DoesNotContain("devcommands", fake.Commands);
        Assert.False(fake.Devcommands);
    }
    // A client at its menu that joins world 7: not ready for the first readings, then ready with its player. Protection
    // is answered only when a reply is given, so an unexpected protection command fails the test.
    private const string Protected = "OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True";
    private static ScriptedTransport JoiningClient(string? safetyReply, int notReadyReadings = 0)
    {
        bool joined = false; int readings = 0;
        var transport = new ScriptedTransport()
            .ClientAccess(() => joined)
            .Extension("valheim.session", "join", _ => { joined = true; return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ =>
            {
                bool ready = joined && ++readings > notReadyReadings;
                return new
                {
                    source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? "7" : null, worldPresent = joined,
                    worldReady = ready, server = false, dedicated = false, localPlayer = ready, playerReady = ready,
                    saving = false, loadError = false, connectionStatus = ready ? "Connected" : "Connecting",
                };
            });
        if (safetyReply != null) transport.On("cli_set_player_safety true", _ => ScriptedTransport.Ok(safetyReply));
        return transport;
    }
    private static SessionState JoinAndWait(ScriptedTransport transport, bool? protectPlayer = null)
    {
        using var actor = transport.Actor("client", "cli_expect worlduid=7");
        var session = new SessionControl(actor);
        session.Join("localhost:2456", "Tester");
        actor.VerifyEnvironment("cli_expect worlduid=7");
        return protectPlayer is bool protect
            ? session.WaitForWorld("7", TimeSpan.FromSeconds(10), protectPlayer: protect)
            : session.WaitForWorld("7", TimeSpan.FromSeconds(10));
    }
    [Fact] public void AJoinWithoutOptionsProtectsThePlayerOnceTheWorldIsReadyAndReadsItBack()
    {
        var transport = JoiningClient(Protected, notReadyReadings: 2);
        Assert.True(JoinAndWait(transport).WorldReady);
        var commands = transport.Commands.ToList();
        Assert.Equal(1, transport.Count("cli_set_player_safety")); // Set, not toggled: once is enough.
        // Only after the reading that showed the world ready, never while it was still loading.
        Assert.Equal(3, transport.Count("cli_extension valheim.session/state"));
        Assert.True(commands.IndexOf("cli_set_player_safety true") > commands.FindLastIndex(c => c == "cli_extension valheim.session/state"));
        Assert.Equal(0, transport.Count("cli_fly")); // Fly stays off by default.
    }
    [Fact] public void ProtectionCanBeLeftOff()
    {
        var transport = JoiningClient(safetyReply: null); // Unscripted: a protection command would throw.
        Assert.True(JoinAndWait(transport, protectPlayer: false).WorldReady);
        Assert.Equal(0, transport.Count("cli_set_player_safety"));
    }
    [Theory]
    [InlineData("ERROR: code=safety_not_applied playerSafety enabled=True god=True ghost=False debugMode=True cheats=True")]
    [InlineData("ERROR: code=safety_not_applied playerSafety enabled=True god=True ghost=True debugMode=True cheats=False")]
    [InlineData("ERROR: No local player found")]
    [InlineData("Usage: cli_set_player_safety <true|false>")]
    public void AProtectionThatIsNotReadBackFailsTheWaitAndIsNotRetried(string reply)
    {
        var transport = JoiningClient(reply);
        var error = Assert.Throws<InvalidOperationException>(() => JoinAndWait(transport));
        // A refusal fails as one (#269); a reply that is neither refused nor confirmed fails the protection check.
        Assert.StartsWith(reply.StartsWith("ERROR:", StringComparison.Ordinal) ? "cli_set_player_safety true was refused: " + reply : "Player protection was not confirmed", error.Message);
        Assert.Equal(1, transport.Count("cli_set_player_safety"));
    }
    [Fact] public void ADedicatedServerHasNoPlayerToProtect()
    {
        // Unscripted protection: a protection command would throw.
        var transport = new ScriptedTransport().Extension("valheim.session", "state", _ => new
        {
            source = "session-state", complete = true, phase = "world-present", worldUid = "7", worldPresent = true, worldReady = true, server = true, dedicated = true,
            localPlayer = false, playerReady = false, saving = false, loadError = false, connectionStatus = "None",
        });
        using var actor = transport.Actor("server", "cli_expect worlduid=7");
        Assert.False(new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(10)).LocalPlayer);
        Assert.Equal(0, transport.Count("cli_set_player_safety"));
    }
    // A game hosting world 7 (a listen server): the world is ready before its own player spawns.
    private static ScriptedTransport Host(int readingsWithoutPlayer, string? safetyReply)
    {
        int readings = 0;
        var transport = new ScriptedTransport().Extension("valheim.session", "state", _ =>
        {
            bool player = ++readings > readingsWithoutPlayer;
            return new
            {
                source = "session-state", complete = true, phase = "world-present", worldUid = "7", worldPresent = true, worldReady = true, server = true, dedicated = false,
                localPlayer = player, playerReady = player, saving = false, loadError = false, connectionStatus = "None",
            };
        });
        if (safetyReply != null) transport.On("cli_set_player_safety true", _ => ScriptedTransport.Ok(safetyReply));
        return transport;
    }
    [Fact] public void AHostingGameIsProtectedOnceItsPlayerSpawns()
    {
        var transport = Host(readingsWithoutPlayer: 2, Protected);
        using var actor = transport.Actor("host", "cli_expect worlduid=7");
        Assert.True(new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(10)).LocalPlayer);
        var commands = transport.Commands.ToList();
        Assert.Equal(3, transport.Count("cli_extension valheim.session/state"));
        Assert.Equal(1, transport.Count("cli_set_player_safety"));
        Assert.True(commands.IndexOf("cli_set_player_safety true") > commands.FindLastIndex(c => c == "cli_extension valheim.session/state"));
    }
    [Fact] public void AHostWhosePlayerNeverSpawnsTimesOutUnprotectedWithAClearReason()
    {
        var transport = Host(readingsWithoutPlayer: int.MaxValue, safetyReply: null); // Unscripted: a protection command would throw.
        using var actor = transport.Actor("host", "cli_expect worlduid=7");
        var error = Assert.Throws<TimeoutException>(() => new SessionControl(actor).WaitForWorld("7", TimeSpan.FromMilliseconds(300)));
        Assert.Contains("local player did not spawn", error.Message);
        Assert.Equal(0, transport.Count("cli_set_player_safety"));
    }
    [Fact] public void AHostThatOptsOutReturnsAsSoonAsTheWorldIsReady()
    {
        var transport = Host(readingsWithoutPlayer: int.MaxValue, safetyReply: null);
        using var actor = transport.Actor("host", "cli_expect worlduid=7");
        Assert.False(new SessionControl(actor).WaitForWorld("7", TimeSpan.FromSeconds(10), protectPlayer: false).LocalPlayer);
        Assert.Equal(1, transport.Count("cli_extension valheim.session/state"));
    }
    [Fact] public void LeaveIsExplicitAndRequiresNewPinsAfterwards()
    {
        var fake = new Fake(); using var actor = fake.Actor(); new SessionControl(actor).Leave();
        Assert.Single(fake.Commands.Where(x => x == "cli_extension valheim.session/leave"));
        Assert.Throws<InvalidOperationException>(() => actor.Execute("anything"));
    }
}
