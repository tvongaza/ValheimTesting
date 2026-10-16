using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

// The supervisor's operations over the existing ServerActor. This owns no process, lock or fixture copy of its own.
internal sealed class DetachedCheckpoints : IDisposable
{
    internal sealed record Operations(Action VerifyInputs, Func<uint> Save, Action Stop, Action<string, CancellationToken> Archive,
        Action<string, CancellationToken> Restore, Func<CancellationToken, OwnedClientCommandLease> Start, Action CheckRunning,
        Func<DetachedModSwap.Input, string, Action, CancellationToken, Action>? PrepareSwap = null);
    private readonly Operations _operations;
    private readonly string _evidence;
    private string? _archive;
    private bool _swapped;
    private BootExitWatch? _watch;
    private readonly CancellationToken _cancellation;
    private readonly TimeSpan _deadline;
    internal int CompletionSeconds { get; }
    internal string State { get; private set; } = "running";
    internal Action<string>? Changed { get; set; }
    internal DetachedCheckpoints(string evidence, Operations operations, CancellationToken cancellation = default,
        int deadlineSeconds = 900, int settleSeconds = 120)
    {
        _evidence = evidence; _operations = operations; _cancellation = cancellation;
        _deadline = TimeSpan.FromSeconds(deadlineSeconds); CompletionSeconds = checked(deadlineSeconds + settleSeconds + 30);
    }

    internal static DetachedCheckpoints For(GameSession session, string worldUid, NativeDependencyLock dependencies)
    {
        var server = session.Server ?? throw new InvalidOperationException("Detached checkpoints require the owned server.");
        var (original, hashes) = server.OriginalFixture ?? throw new InvalidOperationException("No original fixture was pinned before startup.");
        WorldFixture.Verify(original, hashes);
        if (hashes.Count == 0) throw new InvalidDataException("The original detached fixture is empty.");
        DetachedCommandQueue.WriteNew(Path.Combine(session.Output, "detached-original-fixture.json"), new { original, hashes }, bounded: false);
        var swap = new DetachedModSwap(server, dependencies, session.Output);
        var watch = new BootExitWatch();
        watch.Attach(server.CurrentProcess ?? throw new InvalidOperationException("No owned server process."));
        return new(session.Output, new(
            () => { session.Cancellation.ThrowIfCancellationRequested(); DetachedSession.VerifyStagedInputs(); WorldFixture.Verify(original, hashes); swap.VerifyActivated(); },
            () => new SessionControl(server.Game).Save(worldUid, TimeSpan.FromSeconds(90)),
            () => { server.StopCheckpoint(); watch.Settle(); }, server.ArchiveCheckpoint,
            (archive, token) => server.RestoreCheckpoint(original, hashes, archive, token),
            token =>
            {
                server.StartCheckpoint(token);
                var process = server.CurrentProcess ?? throw new InvalidOperationException("Restart has no owned process.");
                watch.Attach(process);
                return DetachedCommandQueue.ServerLease(session.Output, process);
            },
            watch.Check, swap.Prepare), session.Cancellation, deadlineSeconds: Math.Max(900, 2 * (server.Plan?.StartupSeconds ?? 300) + 300),
            settleSeconds: Math.Max(120, (server.Plan?.QuitSeconds ?? 20) + 375)) { _watch = watch };
    }

    internal void CheckRunning()
    {
        if (State != "running") return;
        try { _operations.CheckRunning(); }
        catch { Unknown(); throw; }
    }
    internal void Unknown() => Transition("unknown");
    private void Transition(string state) { State = state; Changed?.Invoke(state); }

    internal DetachedCommandQueue.Reply Execute(DetachedCommandQueue.Request request, Action admission, Action proof,
        Action<OwnedClientCommandLease> newBoot)
    {
        bool changed = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        deadline.CancelAfter(_deadline);
        void Verify()
        {
            deadline.Token.ThrowIfCancellationRequested();
            _operations.VerifyInputs();
            deadline.Token.ThrowIfCancellationRequested();
        }
        try
        {
            admission();
            if (request.Operation is "restart" or "swap" ? State != "stopped" : State != "running")
                throw new InvalidOperationException("Checkpoint " + request.Operation + " is not allowed while " + State + ".");
            Verify();
            if (request.Operation == "swap")
            {
                if (_swapped) throw new InvalidOperationException("This detached run has already used its one build swap.");
                if (_operations.PrepareSwap == null || request.Swap == null) throw new InvalidOperationException("No pinned build swap inputs.");
                var activate = _operations.PrepareSwap(request.Swap, request.Id, admission, deadline.Token);
                admission(); deadline.Token.ThrowIfCancellationRequested();
                changed = true; _swapped = true;
                Transition("swapping");
                admission(); deadline.Token.ThrowIfCancellationRequested();
                activate();
                Verify(); Transition("stopped");
                return new(request.Id, "completed", ["SWAPPED pinned build; server remains stopped"], "", _evidence);
            }
            if (request.Operation == "restart")
            {
                if (_archive == null) throw new InvalidOperationException("No verified clean-stop archive exists.");
                VerifyArchive(_archive);
                deadline.Token.ThrowIfCancellationRequested();
                changed = true;
                Transition("starting");
                deadline.Token.ThrowIfCancellationRequested();
                _operations.Restore(_archive, deadline.Token);
                Verify();
                var lease = _operations.Start(deadline.Token); // New strict pins and world readiness, never the previous actor/capability.
                Verify();
                newBoot(lease);
                Transition("running");
                return new(request.Id, "completed", ["RESTARTED original fixture"], "", _archive);
            }
            proof(); // exact PID/start, port owner and boot lease before the first game mutation
            deadline.Token.ThrowIfCancellationRequested();
            changed = true;
            Transition("saving");
            deadline.Token.ThrowIfCancellationRequested();
            uint save = _operations.Save(); // SessionControl requires complete, advancing save in the pinned world.
            Verify();
            if (request.Operation == "save")
            {
                Transition("running");
                return new(request.Id, "completed", ["SAVED " + save.ToString(CultureInfo.InvariantCulture)], "", "");
            }
            proof();
            deadline.Token.ThrowIfCancellationRequested();
            Transition("stopping");
            deadline.Token.ThrowIfCancellationRequested();
            _operations.Stop();
            string archive = Path.Combine(_evidence, "checkpoints", request.Id);
            Directory.CreateDirectory(archive);
            _operations.Archive(archive, deadline.Token);
            VerifyArchive(archive);
            Verify();
            DetachedCommandQueue.WriteNew(Path.Combine(archive, "checkpoint.json"), new { request.Id, request.Route.Boot, Save = save });
            _archive = archive;
            Transition("stopped");
            return new(request.Id, "completed", ["STOPPED cleanly; saved " + save.ToString(CultureInfo.InvariantCulture)], "", archive);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or InvalidOperationException or
            ArgumentException or TimeoutException or UnauthorizedAccessException or JsonException or OperationCanceledException or
            HostOperationException or System.ComponentModel.Win32Exception or BadImageFormatException)
        {
            if (changed) Unknown();
            return new(request.Id, changed ? "unknown" : "refused", [], failure.Message, _archive ?? "");
        }
    }

    public void Dispose() => _watch?.Dispose();

    // One live waiter per boot, using the owned process's existing identity-aware host observation.
    // Intermediate stops settle it; restart installs a fresh waiter without creating another process owner.
    internal sealed class BootExitWatch : IDisposable
    {
        private CancellationTokenSource? _waiting;
        private Task<int>? _exit;
        internal void Attach(IOwnedProcess process)
        {
            if (_exit != null) throw new InvalidOperationException("The previous boot waiter has not settled.");
            _waiting = new CancellationTokenSource();
            _exit = process.WaitForExitAsync(_waiting.Token);
        }
        internal void Check()
        {
            if (_exit == null) throw new InvalidOperationException("No live boot waiter.");
            if (_exit.IsCompleted)
                throw new InvalidOperationException("Held server exited outside a checkpoint with code " + _exit.GetAwaiter().GetResult() + ".");
        }
        internal void Settle()
        {
            if (_waiting == null) return;
            _waiting.Cancel();
            try { _exit!.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (_waiting.IsCancellationRequested) { }
            finally { _waiting.Dispose(); _waiting = null; _exit = null; }
        }
        public void Dispose() => Settle();
    }

    internal static void VerifyArchive(string archive)
    {
        foreach (string kind in new[] { "world", "logs" })
        {
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(archive, kind + ".sha256.json")))
                ?? throw new InvalidDataException("Missing checkpoint manifest.");
            WorldFixture.Verify(Path.Combine(archive, kind), hashes);
        }
    }
}
