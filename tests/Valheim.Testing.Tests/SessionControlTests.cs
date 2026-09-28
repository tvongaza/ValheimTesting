using System.Text.Json;
using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public class SessionControlTests
{
    private sealed class Fake : IGameTransport
    {
        public List<string> Commands = [];
        public string World = "7";
        public bool Ready = true, FailTransition, AdvanceSave = true;
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            Commands.Add(command);
            if (command.StartsWith("cli_expect ")) return new() { Ok = true, Output = ["OK: EXPECT"] };
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
    [Fact] public void LeaveIsExplicitAndRequiresNewPinsAfterwards()
    {
        var fake = new Fake(); using var actor = fake.Actor(); new SessionControl(actor).Leave();
        Assert.Single(fake.Commands.Where(x => x == "cli_extension valheim.session/leave"));
        Assert.Throws<InvalidOperationException>(() => actor.Execute("anything"));
    }
}
