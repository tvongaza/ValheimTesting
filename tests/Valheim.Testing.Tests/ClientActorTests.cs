using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// ClientActor is the one client open path (#258 step 2): its one open goes through its placement, and that open's kept logs,
// a failed startup's included, are the actor's for the teardown scan. Here the placement launches fake processes (no game).
public sealed class ClientActorTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("client-actor-").FullName;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static ClientRunPlan Plan() => new()
    {
        Mode = "owned", Install = Path.GetFullPath("client-install"), Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
        InstallPins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) },
    };

    // Each open starts a new fake process; a failing open fails after its process started, as a client that never reached its menu.
    private sealed class FakePlacement : IClientPlacement
    {
        public List<FakeOwnedProcess> Processes { get; } = [];
        public bool FailStartup { get; set; }
        public Action? BeforeReady { get; set; }
        public ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation)
        {
            var process = new FakeOwnedProcess(100 + Processes.Count);
            Processes.Add(process);
            int n = Processes.Count;
            return ClientSession.Launch(plan, output, () => process, () => new ScriptedTransport(),
                (_, _) => { BeforeReady?.Invoke(); return FailStartup ? throw new InvalidOperationException("no menu") : Task.CompletedTask; }, cancellation, null,
                [new RunLog($"{name}-{n} BepInEx log", Path.Combine(output, $"{name}-{n}.game-0.log"), Required: true)]);
        }
    }

    [Fact] public void AnActorOpensItsClientOnceAndItsLogsAreTheActors()
    {
        var placement = new FakePlacement();
        using var actor = new ClientActor("client-a", Plan(), _output, placement, CancellationToken.None);
        Assert.Null(actor.Session);
        Assert.Throws<InvalidOperationException>(() => actor.Game);

        var session = actor.Start();
        Assert.Same(session, actor.Session);
        Assert.Same(session.Actor, actor.Game);
        Assert.Equal(new[] { "client-a-1 BepInEx log" }, actor.Logs.Select(log => log.Role));
        // Once: a second open would write over the first one's evidence.
        Assert.Throws<InvalidOperationException>(() => actor.Start());
        Assert.Single(placement.Processes);

        session.Dispose(); // The scenario closes it; the actor sees it closed.
        Assert.Null(actor.Session);
        Assert.Throws<InvalidOperationException>(() => actor.Start());
        Assert.Equal(1, placement.Processes[0].Stops);
    }

    [Fact] public void DisposingTheActorClosesItsOpenClientAndRefusesALaterOpen()
    {
        var placement = new FakePlacement();
        var actor = new ClientActor("client", Plan(), _output, placement, CancellationToken.None);
        var session = actor.Start();
        actor.Dispose(); // Stops only the process it started.
        Assert.True(session.Closed);
        Assert.Equal(1, placement.Processes[0].Stops);
        Assert.Throws<InvalidOperationException>(() => actor.Game);
        Assert.Throws<ObjectDisposedException>(() => actor.Start());
    }

    [Fact] public void AClientClosedWhileItOpensIsClosedWhenItsOpenReturns()
    {
        using var opening = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var placement = new FakePlacement { BeforeReady = () => { opening.Set(); release.Wait(TimeSpan.FromSeconds(30)); } };
        var actor = new ClientActor("client", Plan(), _output, placement, CancellationToken.None);
        var start = Task.Run(actor.Start);
        Assert.True(opening.Wait(TimeSpan.FromSeconds(30)));
        actor.Dispose(); // Teardown while the client is still on its way to its menu.
        release.Set();
        Assert.ThrowsAny<ObjectDisposedException>(() => start.GetAwaiter().GetResult());
        Assert.Equal(1, placement.Processes[0].Stops); // Nobody else holds it, so the actor closed it.
    }

    [Fact] public void AStartupThatFailsAfterItsProcessStartedKeepsItsLogsForTheScan()
    {
        var placement = new FakePlacement { FailStartup = true };
        using var actor = new ClientActor("client", Plan(), _output, placement, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => actor.Start());
        Assert.Null(actor.Session);
        Assert.Equal(1, placement.Processes[0].Stops); // The failed startup stopped its own process.
        Assert.Equal(new[] { "client-1 BepInEx log" }, actor.Logs.Select(log => log.Role));
    }
}
