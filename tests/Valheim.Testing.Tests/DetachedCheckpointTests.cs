using System.Text.Json;
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
    private DetachedCheckpoints Lifecycle(DetachedCommandQueue.Route route, string? failure = null)
    {
        void Effect(string name)
        {
            _effects.Add(name);
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
            }, (_, _) => Effect("restore"), _ => { Effect("start"); return route.Server with { Pid = 101, StartFileTimeUtc = "next-boot" }; }, () => Effect("monitor")));
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

    public void Dispose() => Directory.Delete(_root, true);
}
