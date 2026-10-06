using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public sealed partial class HostedServerRunTests
{
    // #258 step 2: a client the scenario left open (it failed before closing it) is its ClientActor's to close at teardown,
    // before its server stops, and its logs are still scanned. Before, a campaign client without a Steam lease kept running.
    [Fact] public async Task AClientTheScenarioLeftOpenIsClosedAtTeardownBeforeItsServerStops()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, GameLaunch.ClientLinuxExecutable), "client");
        FakeInstalls.LinuxLoader(clientInstall);
        var (plan, profile) = Write(host, withClient: true);
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, run =>
        {
            run.OpenClient(client); // Never closed: the scenario fails first.
            throw new InvalidOperationException("the scenario failed with its client open");
        }, clientHost, new ScriptedTransport()));
        Assert.Equal(1, code);
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops); // Only that client, once.
        var steps = StepNames().ToList();
        int clientStop = steps.IndexOf("stop only the owned client player"), serverStop = steps.IndexOf("stop only owned server");
        Assert.True(clientStop >= 0 && clientStop < serverStop, string.Join(" | ", steps));
        Assert.Contains(Result().GetProperty("Logs").EnumerateArray(), log => log.GetProperty("Role").GetString() == "client-1 BepInEx log");
    }
}
