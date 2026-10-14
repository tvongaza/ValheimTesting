using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

// #819: a campaign client started through the hosted path on this machine (a `local` Windows host, as `server-load` places
// its clean client) is local, so the one lease owner, ClientSession, publishes its command lease after the menu pins verify,
// and the join publishes its world pins. A client on another host publishes none. No game: fake hosts and transports.
public sealed partial class HostedServerRunTests
{
    private const string LeaseWorldUid = "4242";

    // A Windows client role on host "pc", local or reached by SSH, whose install the fake host mirrors.
    private (ResolvedEnvironment Profile, ServerRunPlan Server, ClientRunPlan Client, FakeServerHost ClientHost) WindowsClientProfile(FakeServerHost serverHost, bool local)
    {
        var (planPath, profilePath) = Write(serverHost);
        var profile = TestEnvironment.Read(profilePath);
        profile.Hosts["pc"] = new HostProfile
        {
            Kind = local ? "local" : "ssh", Platform = "windows", Shell = "pwsh", Lock = @"C:\vt\lock",
            Destination = local ? null : "tester@pc.example",
        };
        profile.Clients["pc"] = new GameRole { Host = "pc", Install = @"C:\valheim\client", Runtime = @"C:\vt\runs", CliPort = 5578 };
        var clientHost = new FakeServerHost("pc", Path.Combine(_root, "pc-mirror"), tunnelPort: 15578,
            kind: local ? GameHostKind.Local : GameHostKind.Ssh, windows: true);
        string install = clientHost.Local(@"C:\valheim\client");
        FakeInstalls.Client(install);
        File.WriteAllText(Path.Combine(install, "valheim.exe"), "client");
        File.WriteAllText(Path.Combine(install, "winhttp.dll"), "MZ target_assembly");
        File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), DoorstopMixPathsTests.Doorstop4Config);
        var client = new ClientRunPlan
        {
            Mode = "owned", Architecture = "x64", Install = _root, Port = 5578, Join = "127.0.0.1:2456", Character = "Tester",
            InstallPins = InstallPins.Of(install), Pins = new() { [CliCapabilityManifest.PluginGuid] = new string('a', 32), ["example.mod"] = "absent" },
        };
        return (profile, ServerRunPlan.Read<ServerRunPlan>(planPath), client, clientHost);
    }

    // A client at its menu that joins and leaves the server's world, as ValheimCLI's session commands answer.
    private static ScriptedTransport JoiningClient()
    {
        bool joined = false;
        return new ScriptedTransport()
            .ClientAccess(() => joined)
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester-copy, Local)"))
            .Extension("valheim.session", "join", _ => { joined = true; return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "leave", _ => { joined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? LeaseWorldUid : null, worldPresent = joined,
                worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined, saving = false, loadError = false,
                connectionStatus = joined ? "Connected" : "None",
            })
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"));
    }

    [Fact]
    public async Task ALocalCampaignClientPublishesItsLeaseAfterVerificationAndItsWorldPinsWhileJoined()
    {
        var serverHost = NewHost();
        var (profile, server, client, clientHost) = WindowsClientProfile(serverHost, local: true);
        var transport = JoiningClient();
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, StateWaits = false, Host = name => name == "pc" ? clientHost : serverHost, Connect = _ => transport,
            LocalStartIdentity = (_, _) => "777", // This machine's identity rule, distinct from the host probe's "555".
        });
        Directory.CreateDirectory(Output);
        string lease = Path.Combine(Output, OwnedClientCommandLease.FileName);
        string menuPins = Path.Combine(Output, OwnedClientCommandLease.MenuPins);
        string worldPins = Path.Combine(Output, OwnedClientCommandLease.WorldPins);
        var session = new ClientActor("pc", client, Output, hosted.ClientPlacement(new ScenarioReport("pc")), CancellationToken.None).Start();
        try
        {
            // Published by ClientSession once the menu pins verified: the exact process, read by the local identity rule.
            var written = JsonSerializer.Deserialize<OwnedClientCommandLease>(File.ReadAllText(lease))!;
            Assert.Equal((OwnedClientCommandLease.ClientRole, session.ProcessId!.Value, "777", 5578),
                (written.Role, written.Pid, written.StartFileTimeUtc, written.Port));
            Assert.Contains("example.mod=absent", File.ReadAllText(menuPins));
            Assert.Contains(transport.Commands, command => command.StartsWith("cli_expect --strict ", StringComparison.Ordinal));
            Assert.False(File.Exists(worldPins)); // No world is verified before the join.

            // The hold names the very folder this session published its lease in.
            Assert.Equal(Path.GetFullPath(Output), Path.GetFullPath(session.Output!));
            Assert.Equal("Pinned client commands: valheim-test cli --evidence " + session.Output + " --role client --phase world --command cli_screenshot",
                ForegroundHold.ClientCommandHint(true, session.Output!));

            new SessionControl(session.Actor).JoinWorld(client, LeaseWorldUid, protectPlayer: false);
            string pins = File.ReadAllText(worldPins);
            Assert.Contains("worlduid=" + LeaseWorldUid, pins);
            Assert.Contains(CliCapabilityManifest.PluginGuid + "=" + new string('a', 32), pins);

            new SessionControl(session.Actor).Leave();
            Assert.False(File.Exists(worldPins)); // A command after the leave finds no world to pin to.
            Assert.True(File.Exists(lease));
        }
        finally { session.Dispose(); }
        Assert.False(File.Exists(lease));
        Assert.False(File.Exists(menuPins));
        Assert.Null(ForegroundHold.ClientCommandHint(true, Output)); // No lease, no hint.
        Assert.Empty(await hosted.TeardownAsync(new ScenarioReport("teardown"), Output, launched: false, serverStopped: true));
    }

    // A client on this machine listens on its role's CLI port, and its lease records the plan's: a plan naming another port is
    // refused before anything launches, so the hold never prints a command that cannot reach the client.
    [Fact]
    public void ALocalCampaignClientWhosePlanPortDiffersFromItsRoleIsRefusedBeforeLaunch()
    {
        var serverHost = NewHost();
        var (profile, server, client, clientHost) = WindowsClientProfile(serverHost, local: true);
        client.Port = 5577; // The role listens on 5578.
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, StateWaits = false, Host = name => name == "pc" ? clientHost : serverHost, Connect = _ => JoiningClient(),
        });
        Directory.CreateDirectory(Output);
        var error = Assert.ThrowsAny<Exception>(() =>
            new ClientActor("pc", client, Output, hosted.ClientPlacement(new ScenarioReport("pc")), CancellationToken.None).Start());
        Assert.Contains("must use its role's CLI port 5578; its plan names 5577", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Output, OwnedClientCommandLease.FileName)));
        Assert.Empty(clientHost.Tunnels); // Refused before the CLI tunnel, so before any launch.
    }

    // A client on another host runs its process there: no lease is published here, and the hold prints no client command. A local
    // client whose local identity cannot be read publishes none either, with a warning, and still starts: the lease is optional.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARemoteOrUnidentifiedCampaignClientPublishesNoLease(bool local)
    {
        var serverHost = NewHost();
        var (profile, server, client, clientHost) = WindowsClientProfile(serverHost, local);
        bool remoteAsked = false; // A remote client's process is never identified by this machine's rule.
        var hosted = HostedServerRun.Create(profile, server, "test", new FakeRunHooks
        {
            RunId = RunId, StateWaits = false, Host = name => name == "pc" ? clientHost : serverHost, Connect = _ => JoiningClient(),
            LocalStartIdentity = (pid, _) =>
            {
                if (local) throw new InvalidOperationException($"Process {pid} has exited.");
                remoteAsked = true;
                return "777";
            },
        });
        Directory.CreateDirectory(Output);
        using (var session = new ClientActor("pc", client, Output, hosted.ClientPlacement(new ScenarioReport("pc")), CancellationToken.None).Start())
        {
            Assert.NotNull(session.ProcessId);
            Assert.False(File.Exists(Path.Combine(Output, OwnedClientCommandLease.FileName)));
            Assert.False(File.Exists(Path.Combine(Output, OwnedClientCommandLease.MenuPins)));
            Assert.Null(ForegroundHold.ClientCommandHint(true, session.Output!));
            Assert.False(remoteAsked);
        }
        Assert.Empty(await hosted.TeardownAsync(new ScenarioReport("teardown"), Output, launched: false, serverStopped: true));
    }
}
