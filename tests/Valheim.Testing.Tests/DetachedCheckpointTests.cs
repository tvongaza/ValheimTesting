using System.Text.Json;
using System.Diagnostics;
using Valheim.Testing.GameSessions;
using Valheim.Testing.Game;
using Xunit;

public sealed class DetachedCheckpointTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-checkpoints-").FullName;
    private readonly List<string> _effects = [];
    private readonly List<JournalEntry> _journal = [];
    private readonly List<string> _archives = [];
    private DetachedCommandQueue.Route Route()
    {
        string control = Path.Combine(_root, "control"), evidence = Path.Combine(_root, "evidence");
        Directory.CreateDirectory(control); Directory.CreateDirectory(evidence);
        string started = OwnedClientCommandLease.LocalStartFileTimeUtc(Environment.ProcessId);
        var route = new DetachedCommandQueue.Route("run-checkpoint", control, evidence, Environment.ProcessId, started,
            new(100, "first-boot", 5577, "owned-profile") { Role = "server" });
        DetachedCommandQueue.WriteNew(Path.Combine(evidence, DetachedCommandQueue.RouteFile), route);
        return route;
    }
    private DetachedCheckpoints Lifecycle(DetachedCommandQueue.Route route, string? failure = null,
        CancellationToken cancellation = default, Action<string>? onEffect = null, Action? monitor = null)
    {
        void Effect(string name)
        {
            _effects.Add(name);
            onEffect?.Invoke(name);
            if (failure == name) throw new IOException("lost " + name);
            if (failure == "cancel-" + name) throw new OperationCanceledException();
        }
        return new(route.Evidence, new(
            () => Effect("verify"), () => { Effect("save"); return 10; }, () => Effect("stop"), (archive, _) =>
            {
                Effect("archive"); _archives.Add(archive);
                foreach (string kind in new[] { "world", "logs" })
                {
                    string folder = Path.Combine(archive, kind); Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "file"), kind == "world" ? "changed world" : "stopped boot log");
                    File.WriteAllText(Path.Combine(archive, kind + ".sha256.json"), JsonSerializer.Serialize(WorldFixture.Manifest(folder)));
                }
            }, (_, _) => Effect("restore"), _ => { Effect("start"); return route.Server with { Pid = 101, StartFileTimeUtc = "next-boot" }; }, monitor ?? (() => Effect("monitor"))), cancellation);
    }
    private static DetachedCommandQueue.Request Request(DetachedCommandQueue.Route route, string operation) =>
        new(Guid.NewGuid().ToString("N"), route, DateTime.UtcNow.AddSeconds(60), operation, "pins", 1) { Operation = operation };
    private static DetachedCommandQueue.Reply Read(DetachedCommandQueue.Route route, int slot) =>
        DetachedCommandQueue.Read<DetachedCommandQueue.Reply>(DetachedCommandQueue.Slot(route, slot, "reply"));
    private DetachedCommandQueue Queue(DetachedCommandQueue.Route route, DetachedCheckpoints lifecycle) =>
        new(route, _journal.Add, (request, admit) =>
        {
            admit(); _effects.Add("command"); return new(request.Id, "completed", [], "", "");
        }, lifecycle, () => _effects.Add("proof"));

    [Fact]
    public void StopArchivesBeforeRestartAndOneQueueRejectsEveryOldBootRequest()
    {
        var route = Route(); var lifecycle = Lifecycle(route);
        using var queue = Queue(route, lifecycle);
        int stop = DetachedCommandQueue.Publish(route, Request(route, "stop"));
        queue.ProcessPending();
        Assert.Equal("completed", Read(route, stop).Outcome);
        Assert.Equal("stopped", lifecycle.State);
        Assert.Equal(["verify", "proof", "save", "verify", "proof", "stop", "archive", "verify"], _effects);
        int whileStopped = DetachedCommandQueue.Publish(route, Request(route, "command"));
        queue.ProcessPending();
        Assert.Equal("refused", Read(route, whileStopped).Outcome);
        var restart = Request(route, "restart");
        int start = DetachedCommandQueue.Publish(route, restart);
        int stale = DetachedCommandQueue.Publish(route, Request(route, "command"));
        queue.ProcessPending();
        Assert.Equal("completed", Read(route, start).Outcome);
        Assert.Equal("refused", Read(route, stale).Outcome);
        var next = DetachedCommandQueue.CurrentRoute(route.Evidence);
        Assert.Equal(2, next.Boot); Assert.Equal(101, next.Server.Pid);
        Assert.Equal(route, DetachedCommandQueue.Read<DetachedCommandQueue.Route>(Path.Combine(route.Evidence, DetachedCommandQueue.RouteFile)));
        Assert.True(File.Exists(DetachedCommandQueue.Slot(route, stop, "request")));
        int current = DetachedCommandQueue.Publish(next, Request(next, "command"));
        int duplicate = DetachedCommandQueue.Publish(next, restart with { Route = next });
        queue.ProcessPending(); queue.ProcessPending();
        Assert.Equal("completed", Read(next, current).Outcome);
        Assert.Equal("refused", Read(next, duplicate).Outcome);
        Assert.Single(_effects.Where(effect => effect == "start"));
        Assert.Single(_effects.Where(effect => effect == "command"));
        Assert.Equal(["saving", "stopping", "stopped", "starting", "running"],
            _journal.Where(entry => entry.Kind == "detached-checkpoint-state").Select(entry => entry.Fields["state"]));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("stop")]
    [InlineData("archive")]
    [InlineData("cancel-save")]
    [InlineData("cancel-stop")]
    [InlineData("cancel-archive")]
    public void UncertainStopNeverRestartsOrReplaysAndBlocksLaterMutations(string failure)
    {
        var route = Route(); var lifecycle = Lifecycle(route, failure);
        using var queue = Queue(route, lifecycle);
        int stop = DetachedCommandQueue.Publish(route, Request(route, "stop"));
        int pending = DetachedCommandQueue.Publish(route, Request(route, "save"));
        queue.ProcessPending(); queue.ProcessPending();
        Assert.Equal("unknown", Read(route, stop).Outcome);
        Assert.Equal("refused", Read(route, pending).Outcome);
        Assert.Equal("unknown", lifecycle.State);
        Assert.Single(_effects.Where(effect => effect == "save"));
        Assert.DoesNotContain("restore", _effects); Assert.DoesNotContain("start", _effects);
        Assert.Equal("unknown", DetachedCommandQueue.Read<DetachedCommandQueue.Status>(Path.Combine(route.Evidence, DetachedCommandQueue.StateFile)).State);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("start")]
    [InlineData("cancel-restore")]
    [InlineData("cancel-start")]
    public void FailedRestartNeverPublishesANewBootOrReopensAdmission(string failure)
    {
        var route = Route(); var lifecycle = Lifecycle(route, failure);
        using var queue = Queue(route, lifecycle);
        DetachedCommandQueue.Publish(route, Request(route, "stop")); queue.ProcessPending();
        int start = DetachedCommandQueue.Publish(route, Request(route, "restart"));
        int pending = DetachedCommandQueue.Publish(route, Request(route, "command"));
        queue.ProcessPending();
        Assert.Equal("unknown", Read(route, start).Outcome);
        Assert.Equal("refused", Read(route, pending).Outcome);
        Assert.Equal(1, DetachedCommandQueue.CurrentRoute(route.Evidence).Boot);
        Assert.DoesNotContain("command", _effects);
    }

    [Fact]
    public void ChangedArchiveRefusesBeforeRestoringTheWorld()
    {
        var route = Route(); var lifecycle = Lifecycle(route);
        using var queue = Queue(route, lifecycle);
        DetachedCommandQueue.Publish(route, Request(route, "stop")); queue.ProcessPending();
        File.WriteAllText(Path.Combine(Assert.Single(_archives), "world", "file"), "different world");
        int start = DetachedCommandQueue.Publish(route, Request(route, "restart")); queue.ProcessPending();
        Assert.Equal("refused", Read(route, start).Outcome); Assert.Equal("stopped", lifecycle.State);
        Assert.DoesNotContain("restore", _effects);
    }

    [Fact]
    public void SourceDriftRefusesBeforeSaving()
    {
        var route = Route(); var lifecycle = Lifecycle(route, "verify");
        using var queue = Queue(route, lifecycle);
        int save = DetachedCommandQueue.Publish(route, Request(route, "save")); queue.ProcessPending();
        Assert.Equal("refused", Read(route, save).Outcome);
        Assert.Equal(["verify"], _effects);
        Assert.Equal("running", lifecycle.State);
    }

    [Theory]
    [InlineData("first-verify", false)]
    [InlineData("first-proof", false)]
    [InlineData("saving", false)]
    [InlineData("second-verify", true)]
    [InlineData("second-proof", true)]
    [InlineData("stopping", true)]
    public void CancellationDuringChecksOrPublicationNeverDispatchesTheNextMutation(string point, bool saved)
    {
        var route = Route();
        using var cancellation = new CancellationTokenSource();
        int verifies = 0, proofs = 0;
        var lifecycle = Lifecycle(route, cancellation: cancellation.Token, onEffect: effect =>
        {
            if (effect == "verify" && ++verifies == (point == "first-verify" ? 1 : point == "second-verify" ? 2 : -1))
                cancellation.Cancel();
        });
        lifecycle.Changed = state => { if (state == point) cancellation.Cancel(); };
        var reply = lifecycle.Execute(Request(route, "stop"), () => { }, () =>
        {
            if (++proofs == (point == "first-proof" ? 1 : point == "second-proof" ? 2 : -1)) cancellation.Cancel();
        }, _ => throw new InvalidOperationException("Unexpected boot"));
        Assert.Equal(point is "first-verify" or "first-proof" ? "refused" : "unknown", reply.Outcome);
        Assert.Equal(saved ? 1 : 0, _effects.Count(effect => effect == "save"));
        Assert.DoesNotContain("stop", _effects); Assert.DoesNotContain("archive", _effects);
    }

    [Fact]
    public async Task IdleQueueDetectsRealHostExitAndASettledWatcherCanObserveTheNextBoot()
    {
        if (OperatingSystem.IsWindows()) return; // Windows task startup is covered by the existing host integration lane.
        var host = new LocalGameHost("checkpoint-exit", HostShell.Bash);
        var launch = OperatingSystem.IsMacOS()
            ? (await MacServerStandIn.PrepareAsync(host, _root, TimeSpan.FromSeconds(30))).Launch
            : LauncherTimeoutStandIn.Unix(_root);
        using var watcher = new DetachedCheckpoints.BootExitWatch();
        for (int boot = 1; boot <= 2; boot++)
        {
            string bootRoot = Path.Combine(_root, "boot-" + boot);
            await StandInServer.RunAsync(host, launch, bootRoot, TimeSpan.FromSeconds(30),
                nameof(IdleQueueDetectsRealHostExitAndASettledWatcherCanObserveTheNextBoot), async process =>
            {
                watcher.Attach(process);
                Assert.False(process.HasExited);
                string control = Directory.CreateDirectory(Path.Combine(_root, "queue-" + boot)).FullName;
                var route = new DetachedCommandQueue.Route("run-watch-" + boot, control, control, Environment.ProcessId,
                    OwnedClientCommandLease.LocalStartFileTimeUtc(Environment.ProcessId), new(process.Id, process.StartIdentity, 5577, "owned") { Role = "server" });
                using var queue = Queue(route, Lifecycle(route, monitor: watcher.Check));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Task held = queue.WaitAsync(deadline.Token);
                using (var exact = Process.GetProcessById(process.Id)) { exact.Kill(); await exact.WaitForExitAsync(deadline.Token); }
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => held);
                Assert.Contains("exited outside a checkpoint", failure.Message);
                Assert.True(File.Exists(Path.Combine(control, "commands-closed.json")));
                Assert.Equal("unknown", DetachedCommandQueue.Read<DetachedCommandQueue.Status>(Path.Combine(control, DetachedCommandQueue.StateFile)).State);
                watcher.Settle();
            }, logs: []);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
