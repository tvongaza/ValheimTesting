using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

// The supervisor's operations over the existing ServerActor. This owns no process, lock or fixture copy of its own.
internal sealed class DetachedCheckpoints
{
    internal sealed record Operations(Action VerifyInputs, Func<uint> Save, Action Stop, Action<string, CancellationToken> Archive,
        Action<string, CancellationToken> Restore, Func<CancellationToken, OwnedClientCommandLease> Start, Action CheckRunning);
    private readonly Operations _operations;
    private readonly string _evidence;
    private string? _archive;
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

    internal static DetachedCheckpoints For(GameSession session, string worldUid)
    {
        var server = session.Server ?? throw new InvalidOperationException("Detached checkpoints require the owned server.");
        var (original, hashes) = server.OriginalFixture ?? throw new InvalidOperationException("No original fixture was pinned before startup.");
        WorldFixture.Verify(original, hashes);
        if (hashes.Count == 0) throw new InvalidDataException("The original detached fixture is empty.");
        DetachedCommandQueue.WriteNew(Path.Combine(session.Output, "detached-original-fixture.json"), new { original, hashes }, bounded: false);
        return new(session.Output, new(
            () => { session.Cancellation.ThrowIfCancellationRequested(); DetachedSession.VerifyStagedInputs(); WorldFixture.Verify(original, hashes); },
            () => new SessionControl(server.Game).Save(worldUid, TimeSpan.FromSeconds(90)),
            server.StopCheckpoint, server.ArchiveCheckpoint,
            (archive, token) => server.RestoreCheckpoint(original, hashes, archive, token),
            token =>
            {
                server.StartCheckpoint(token);
                return DetachedCommandQueue.ServerLease(session.Output, server.CurrentProcess ?? throw new InvalidOperationException("Restart has no owned process."));
            },
            () =>
            {
                if (server.CurrentProcess is not { HasExited: false })
                    throw new InvalidOperationException("Held server exited outside a checkpoint; finish or recover the run.");
            }), session.Cancellation, deadlineSeconds: Math.Max(900, 2 * (server.Plan?.StartupSeconds ?? 300) + 300),
            settleSeconds: Math.Max(120, (server.Plan?.QuitSeconds ?? 20) + 375));
    }

    internal void CheckRunning() { if (State == "running") _operations.CheckRunning(); }
    internal void Unknown() => Transition("unknown");
    private void Transition(string state) { State = state; Changed?.Invoke(state); }

    internal DetachedCommandQueue.Reply Execute(DetachedCommandQueue.Request request, Action admission, Action proof,
        Action<OwnedClientCommandLease> newBoot)
    {
        bool changed = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        deadline.CancelAfter(_deadline);
        void Verify() { deadline.Token.ThrowIfCancellationRequested(); _operations.VerifyInputs(); }
        try
        {
            admission();
            if (request.Operation == "restart" ? State != "stopped" : State != "running")
                throw new InvalidOperationException("Checkpoint " + request.Operation + " is not allowed while " + State + ".");
            Verify();
            if (request.Operation == "restart")
            {
                if (_archive == null) throw new InvalidOperationException("No verified clean-stop archive exists.");
                VerifyArchive(_archive);
                changed = true;
                Transition("starting");
                _operations.Restore(_archive, deadline.Token);
                Verify();
                var lease = _operations.Start(deadline.Token); // New strict pins and world readiness, never the previous actor/capability.
                Verify();
                newBoot(lease);
                Transition("running");
                return new(request.Id, "completed", ["RESTARTED original fixture"], "", _archive);
            }
            proof(); // exact PID/start, port owner and boot lease before the first game mutation
            changed = true;
            Transition("saving");
            uint save = _operations.Save(); // SessionControl requires complete, advancing save in the pinned world.
            Verify();
            if (request.Operation == "save")
            {
                Transition("running");
                return new(request.Id, "completed", ["SAVED " + save.ToString(CultureInfo.InvariantCulture)], "", "");
            }
            proof();
            Transition("stopping");
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
            HostOperationException or System.ComponentModel.Win32Exception)
        {
            if (changed) Unknown();
            return new(request.Id, changed ? "unknown" : "refused", [], failure.Message, _archive ?? "");
        }
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
