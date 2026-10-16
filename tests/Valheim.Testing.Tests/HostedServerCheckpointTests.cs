using System.Text.Json;
using System.Text.Json.Nodes;
using valheim_cli.Testing;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed partial class HostedServerRunTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("source")]
    [InlineData("stopped-world")]
    public async Task CheckpointRetainsOnePlacementAndRestoresOnlyThePinnedOriginal(string drift)
    {
        var server = new FakeOwnedServer("test.mod", @"C:\vt\runs\run-test\world");
        var host = new FakeServerHost("linux-box", Mirror, server, windows: true);
        var (plan, profileFile) = Write(host, hostPlatform: "windows", hostShell: "pwsh");
        var profile = TestEnvironment.Read(profileFile);
        profile.Hosts["linux-box"].Kind = "local";
        using var machine = EnvironmentInventory.UseMachine(new FakeMachine("windows"));
        bool checkpoint = false;
        int code = await PinnedServerRun.MainAsync(profile, ["run", plan, Output], Options(host, server, run =>
        {
            var actor = run.GameSession.Server!;
            var (original, hashes) = actor.OriginalFixture!.Value;
            string worldFile = Path.Combine(host.Local(actor.WorldDirectory), "worlds_local", "Test.db");
            File.WriteAllText(worldFile, "round changed world");
            actor.StopCheckpoint();
            Assert.Null(actor.CurrentProcess);
            // HostServer reports a proven clean exit without an exit code; it is still a clean checkpoint.
            Assert.Equal(StopOutcome.Clean, Assert.Single(actor.Stops).Outcome);
            string archive = Path.Combine(Output, "round-one"); Directory.CreateDirectory(archive);
            actor.ArchiveCheckpoint(archive, CancellationToken.None);
            DetachedCheckpoints.VerifyArchive(archive);
            Assert.Equal("round changed world", File.ReadAllText(Path.Combine(archive, "world", "worlds_local", "Test.db")));
            Assert.Single(host.Claims); Assert.Empty(host.Releases);
            Assert.True(Directory.Exists(host.Local(actor.RuntimeDirectory)));
            if (drift != "none")
            {
                string changedFile = drift == "source" ? Path.Combine(original, "worlds_local", "Test.db") : worldFile;
                File.WriteAllText(changedFile, "source drift");
                Assert.Throws<InvalidOperationException>(() => actor.RestoreCheckpoint(original, hashes, archive, CancellationToken.None));
                Assert.Equal(drift == "source" ? "round changed world" : "source drift", File.ReadAllText(worldFile));
                Assert.Single(actor.StartedProcesses);
            }
            else
            {
                actor.RestoreCheckpoint(original, hashes, archive, CancellationToken.None);
                Assert.Equal("world", File.ReadAllText(worldFile));
                WorldFixture.Verify(original, hashes);
                actor.StartCheckpoint(CancellationToken.None);
                Assert.Equal(2, actor.StartedProcesses.Count);
                Assert.NotEqual(server.Tokens[0], server.Tokens[1]);
                Assert.True(TestAccess.Read(actor.Game).CheatsAcknowledged);
                Assert.Single(host.Claims); Assert.Empty(host.Releases);
            }
            checkpoint = true;
            return Task.CompletedTask;
        }, checkpointFixture: true, connect: () => new CheckpointTransport(server.Connect())));
        Assert.True(checkpoint, JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement.GetProperty("Steps").GetRawText());
        Assert.Equal(0, code);
        Assert.Single(host.Releases);
    }

    // FakeOwnedServer already owns launch/stop/pins; expose its ready session through the normal capability listing too.
    private sealed class CheckpointTransport(IGameTransport inner) : IGameTransport
    {
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            if (command == "cli_extensions") return new() { Ok = true, Output = ["EXTENSIONS " + JsonSerializer.Serialize(new
            {
                apiVersion = 1, extensions = new[] { new { id = "test.mod", instance = "boot", closing = false,
                    commands = new[] { new { name = "session", resultVersion = 1, readOnly = true } } } },
            })] };
            var reply = inner.Execute(command, timeout);
            if (command != "cli_extension test.mod/session") return reply;
            var result = JsonNode.Parse(reply.Output[0]["EXTENSION_RESULT ".Length..])!;
            result["instance"] = "boot"; result["data"]!["acceptingConnections"] = true;
            return new() { Ok = true, Output = ["EXTENSION_RESULT " + result.ToJsonString()] };
        }
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task ForcedCheckpointStopNeverRestoresOrStartsAnotherBoot()
    {
        var fixture = LocalFixture();
        using var machine = LocalServerMachine.Use();
        bool refused = false;
        int code = await PinnedServerRun.MainAsync(fixture.Profile, ["run", fixture.Plan, Output], Options(fixture.Host, fixture.Server, run =>
        {
            var actor = run.GameSession.Server!;
            fixture.Host.IgnoreQuit = true;
            Assert.Throws<InvalidOperationException>(actor.StopCheckpoint);
            Assert.Equal(StopOutcome.Killed, Assert.Single(actor.Stops).Outcome);
            Assert.Throws<InvalidOperationException>(() => actor.StartCheckpoint(CancellationToken.None));
            Assert.Single(actor.StartedProcesses);
            refused = true;
            return Task.CompletedTask;
        }));
        Assert.True(refused);
        Assert.Equal(0, code); // This scenario deliberately caught the refusal; the queue tests require UNKNOWN at its boundary.
    }
}
