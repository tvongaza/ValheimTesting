using System.Diagnostics;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using valheim_cli.Testing;

// One supervisor consumes fixed, write-once slots in its already journalled control directory. The lifetime
// DetachedControlLock belongs to adoption/recovery; request clients never take it and never append the run journal.
internal sealed class DetachedCommandQueue : IDisposable
{
    internal const string RouteFile = "detached-command-route.json";
    internal const int Capacity = 64, MaxBytes = 65536;
    internal sealed record Route(string Run, string Control, string Evidence, int Pid, string Started, OwnedClientCommandLease Server)
    { public int Boot { get; init; } = 1; public int CompletionSeconds { get; init; } = 1050; }
    internal sealed record Status(Route Route, string State);
    internal const string StateFile = "detached-state.json";
    internal sealed record Request(string Id, Route Route, DateTime ExpiresUtc, string Command, string Pins, int TimeoutSeconds)
    { public string Operation { get; init; } = "command"; }
    internal sealed record Reply(string Id, string Outcome, string[] Output, string Message, string Evidence);
    private Route _route;
    private readonly DetachedCheckpoints? _checkpoints;
    private readonly Action _checkpointProof;
    private bool _unknown;
    private readonly Action<JournalEntry> _journal;
    private readonly Func<Request, Action, Reply> _execute;
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<int> _settled = [];
    private bool _closed;
    private string FinishFile => Path.Combine(_route.Control, _route.Run + ".finish");
    private string ClosedFile => Path.Combine(_route.Control, "commands-closed.json");

    internal DetachedCommandQueue(Route route, Action<JournalEntry> journal, Func<Request, Action, Reply>? execute = null,
        DetachedCheckpoints? checkpoints = null, Action? checkpointProof = null)
    {
        _route = route; _journal = journal; _execute = execute ?? Execute; _checkpoints = checkpoints; _checkpointProof = checkpointProof ?? ProveServer;
        if (checkpoints != null) checkpoints.Changed = state =>
        {
            _journal(JournalEntry.Of("detached-checkpoint-state", ("state", state), ("boot", _route.Boot.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            PublishState();
        };
    }

    private string State => _unknown ? "unknown" : _checkpoints?.State ?? "running";
    private void PublishState() => Replace(Path.Combine(_route.Evidence, StateFile), new Status(_route, State));
    internal static Route CurrentRoute(string evidence)
    {
        var initial = Read<Route>(Path.Combine(evidence, RouteFile));
        if (!File.Exists(Path.Combine(evidence, StateFile))) return initial;
        var current = Read<Status>(Path.Combine(evidence, StateFile));
        if (current.Route with { Server = initial.Server, Boot = initial.Boot } != initial || current.Route.Boot < initial.Boot)
            throw new InvalidDataException("The detached state belongs to another supervisor.");
        return current.Route;
    }

    internal static DetachedCommandQueue Open(string run, string evidence, IOwnedProcess server, DetachedCheckpoints? checkpoints = null)
    {
        string control = DetachedSession.ConfiguredControlDirectory ?? throw new InvalidOperationException("Missing detached control directory.");
        var lease = ServerLease(evidence, server);
        using var self = Process.GetCurrentProcess();
        var route = new Route(run, control, Path.GetFullPath(evidence), self.Id, OwnedClientCommandLease.LocalStartFileTimeUtc(self), lease) { CompletionSeconds = checkpoints?.CompletionSeconds ?? 1050 };
        DetachedSession.RequireCommandOwnerAsync(route).GetAwaiter().GetResult();
        var queue = new DetachedCommandQueue(route, entry => new RunJournal(run).AppendLocal("detached", entry), checkpoints: checkpoints);
        WriteNew(Path.Combine(evidence, RouteFile), route);
        queue.PublishState();
        return queue;
    }

    internal static OwnedClientCommandLease ServerLease(string evidence, IOwnedProcess server)
    {
        var lease = Read<OwnedClientCommandLease>(Path.Combine(evidence, OwnedClientCommandLease.ServerFileName));
        string started = server switch
        {
            HostServerProcess hosted => hosted.StartIdentity,
            ILocalClientProcessIdentity local => local.StartFileTimeUtc,
            _ => throw new InvalidOperationException("Detached commands require the exact local boot identity."),
        };
        if (lease.Role != "server" || lease.Pid != server.Id || lease.StartFileTimeUtc != started || server.HasExited)
            throw new InvalidOperationException("The detached server lease does not name the owned boot.");
        return lease;
    }

    internal static int SubmitCheckpoint(string evidence, string operation, TextWriter output, TextWriter error)
    {
        evidence = Path.GetFullPath(evidence);
        var route = CurrentRoute(evidence);
        if (route.Evidence != evidence) throw new InvalidDataException("Checkpoint evidence names another run.");
        DetachedSession.RequireCommandOwnerAsync(route).GetAwaiter().GetResult();
        var request = new Request(Guid.NewGuid().ToString("N"), route, DateTime.UtcNow.AddSeconds(60), operation,
            "checkpoint", 5) { Operation = operation };
        Validate(request);
        int slot = Publish(route, request);
        return WaitForReply(route, request, slot, output, error);
    }

    internal static int Submit(string evidence, Dictionary<string, string> values, TextWriter output, TextWriter error)
    {
        var route = CurrentRoute(evidence);
        if (route.Evidence != evidence || !Path.IsPathFullyQualified(route.Control) || !RunJournal.SafeName(route.Run) ||
            values.GetValueOrDefault("--role", "server") != "server")
            throw new InvalidDataException("Detached commands require this run's server evidence.");
        DetachedSession.RequireCommandOwnerAsync(route).GetAwaiter().GetResult();
        string pins = values.TryGetValue("--expect-strict", out var supplied) ? supplied :
            Path.Combine(evidence, OwnedClientCommandLease.PhasePins("server", values["--phase"]));
        var request = new Request(Guid.NewGuid().ToString("N"), route, DateTime.UtcNow.AddSeconds(60), values["--command"],
            StrictExpectations.Load(pins), values.TryGetValue("--timeout-seconds", out var seconds) ? int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture) : 5);
        Validate(request);
        int slot = Publish(route, request);
        return WaitForReply(route, request, slot, output, error);
    }

    // Publication is the uncertainty boundary: every subsequent read failure may conceal an executed mutation.
    internal static int WaitForReply(Route route, Request request, int slot, TextWriter output, TextWriter error,
        Func<string, Reply>? readReply = null)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            output.WriteLine("Detached operation: " + request.Id);
            while (elapsed.Elapsed < TimeSpan.FromSeconds(request.Operation == "command" ? 65 : route.CompletionSeconds))
            {
                string result = Slot(route, slot, "reply");
                if (File.Exists(result))
                {
                    var reply = readReply != null ? readReply(result) : Read<Reply>(result);
                    if (reply.Id != request.Id || reply.Outcome is not ("completed" or "refused" or "unknown") ||
                        reply.Output == null || reply.Message == null || reply.Evidence == null)
                        throw new InvalidDataException("The detached reply does not name a valid outcome for this operation.");
                    foreach (string line in reply.Output) output.WriteLine(line);
                    output.WriteLine("Private command evidence: " + reply.Evidence);
                    if (reply.Outcome == "completed") return 0;
                    error.WriteLine(reply.Outcome.ToUpperInvariant() + ": " + reply.Message +
                        (reply.Outcome == "unknown" ? "; do not retry the command. Inspect its evidence and use read-only observations." : ""));
                    return reply.Outcome == "unknown" ? 3 : 1;
                }
                if (!ForegroundHold.ExactOwner(route.Pid, route.Started)) break;
                Thread.Sleep(50);
            }
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        { error.WriteLine("The published operation's reply could not be read: " + failure.Message); }
        error.WriteLine("UNKNOWN: no readable durable reply for " + request.Id + "; do not retry the command. Inspect its private evidence and use read-only observations after recovery.");
        return 3;
    }

    internal static int Publish(Route route, Request request)
    {
        if (File.Exists(Path.Combine(route.Control, route.Run + ".finish")) || File.Exists(Path.Combine(route.Control, "commands-closed.json")))
            throw new InvalidOperationException("Detached command admission is closed; no command was sent.");
        // Slot files are never removed or reused. Even a lost reply cannot lead to a replay in this supervisor or a later one.
        for (int i = 0; i < Capacity; i++)
        {
            string path = Slot(route, i, "request");
            try { WriteNew(path, request); return i; }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new InvalidOperationException("This detached run has used its 64 command slots; finish it before starting another run.");
    }

    internal async Task WaitAsync(CancellationToken cancellation)
    {
        try
        {
            while (!File.Exists(FinishFile))
            {
                cancellation.ThrowIfCancellationRequested();
                _checkpoints?.CheckRunning();
                ProcessPending(cancellation);
                await Task.Delay(50, cancellation).ConfigureAwait(false);
            }
            // Finish is not a successful hold if this boot already exited before teardown began.
            // Intentionally stopped checkpoints have no running boot to observe.
            _checkpoints?.CheckRunning();
        }
        finally { Close(); }
        if (_unknown) throw new InvalidOperationException("The detached run has an unknown operation; inspect its checkpoint evidence.");
    }

    internal void ProcessPending(CancellationToken cancellation = default)
    {
        for (int slot = 0; slot < Capacity; slot++)
        {
            if (_settled.Contains(slot) || !File.Exists(Slot(_route, slot, "request"))) continue;
            var request = Read<Request>(Slot(_route, slot, "request"));
            _settled.Add(slot); // Failure after intent/dispatch must never re-enter this slot, including during Close.
            Reply reply;
            string? refusal = null;
            try
            {
                Validate(request);
                if (!_ids.Add(request.Id)) throw new InvalidOperationException("Duplicate operation ID; the original operation is never replayed.");
                RequireAdmission(request, cancellation);
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            { refusal = failure.Message; }
            // Only this supervisor writes operation entries, preserving LastNamedRunner. Failure to journal intent aborts
            // admission and the hold before any transport dispatch. Outcome journal failure likewise stops subsequent work.
            _journal(Entry("detached-command-intended", request, refusal ?? request.Command));
            reply = refusal != null ? new(request.Id, "refused", [], refusal, "") : request.Operation == "command"
                ? _execute(request, () => RequireAdmission(request, cancellation))
                : _checkpoints!.Execute(request, () => RequireAdmission(request, cancellation), _checkpointProof, lease =>
                {
                    if (lease == _route.Server) throw new InvalidOperationException("Restart returned the old boot identity.");
                    _route = _route with { Server = lease, Boot = checked(_route.Boot + 1) };
                });
            if (reply.Outcome == "unknown")
            {
                _unknown = true;
                if (_checkpoints != null && _checkpoints.State != "unknown") _checkpoints.Unknown();
                PublishState();
            }
            _journal(Entry("detached-command-" + reply.Outcome, request, reply.Message[..Math.Min(reply.Message.Length, 4096)]));
            string evidence = Path.Combine(_route.Evidence, "detached-command-" + slot.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".json");
            WriteNew(evidence, reply, bounded: false); // Complete private result; large output must not end the held server.
            WriteNew(Slot(_route, slot, "reply"), BoundReply(reply with { Evidence = evidence }));
        }
    }

    private static JournalEntry Entry(string kind, Request request, string detail) => JournalEntry.Of(kind,
        ("operation", request.Id), ("pid", request.Route.Server.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("startIdentity", request.Route.Server.StartFileTimeUtc), ("detail", detail));

    private void RequireAdmission(Request request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_closed || File.Exists(FinishFile) || File.Exists(ClosedFile)) throw new InvalidOperationException("Detached command admission is closed.");
        if (_unknown) throw new InvalidOperationException("An earlier operation has an unknown outcome; finish or recover before another mutation.");
        if (request.Operation != "command" && _checkpoints == null) throw new InvalidOperationException("This hold has no checkpoint lifecycle.");
        if (request.Operation == "command" && State != "running") throw new InvalidOperationException("Server commands require the running state.");
        if (request.Route != _route) throw new InvalidOperationException("The request does not name this supervisor and server boot.");
        if (request.ExpiresUtc <= DateTime.UtcNow) throw new InvalidOperationException("The operation expired before dispatch.");
        if (!ForegroundHold.ExactOwner(_route.Pid, _route.Started)) throw new InvalidOperationException("The exact supervisor is gone.");
    }

    internal static void Validate(Request request)
    {
        if (request.Route.CompletionSeconds is < 1 or > 18000 || request.Operation is not ("command" or "save" or "stop" or "restart") ||
            !Guid.TryParseExact(request.Id, "N", out _) || request.Command.Length is < 1 or > 4096 ||
            request.Command.IndexOfAny(['\r', '\n', '\0']) >= 0 || request.Pins.Length is < 1 or > 32768 ||
            request.TimeoutSeconds is < 1 or > 15 || request.ExpiresUtc.Kind != DateTimeKind.Utc || request.ExpiresUtc > DateTime.UtcNow.AddSeconds(61))
            throw new InvalidDataException("Invalid or unbounded detached command request.");
        // Screenshots belong to the client workflow; this slice is a single server command, never an asynchronous follow-up.
        if (OwnedCliCommand.ScreenshotSupersize(request.Command) != null)
            throw new InvalidOperationException("Detached server commands do not capture client screenshots.");
    }

    internal Reply Execute(Request request, Action admission)
    {
        bool dispatched = false;
        string log = Path.Combine(_route.Evidence, "owned-cli-command-" + request.Id + ".jsonl");
        try
        {
            void Proof()
            {
                admission();
                ProveServer();
            }
            Proof();
            using var recording = new RecordingTransport(new CliTransport("127.0.0.1", _route.Server.Port), log);
            var guarded = new GuardedTransport(recording, Proof, () => dispatched = true);
            using var actor = new GameActor("detached server", guarded)
            { CommandTimeout = TimeSpan.FromSeconds(request.TimeoutSeconds) };
            actor.VerifyEnvironment(request.Pins);
            // GuardedTransport marks only the final call: both strict checks are read-only and cannot be confused with dispatch.
            guarded.Armed = true;
            var result = actor.Execute(request.Command, requireAccepted: false);
            return new(request.Id, !result.Ok ? "unknown" : result.Accepted ? "completed" : "refused", result.Output.ToArray(),
                result.Refusal ?? result.ErrorCode ?? result.Message ?? "", log);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
            TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
        { return new(request.Id, dispatched ? "unknown" : "refused", [], failure.Message, log); }
    }

    private void ProveServer()
    {
        var current = Read<OwnedClientCommandLease>(Path.Combine(_route.Evidence, OwnedClientCommandLease.ServerFileName));
        if (current != _route.Server) throw new InvalidOperationException("The owned server boot lease changed.");
        using var process = Process.GetProcessById(current.Pid);
        if (!OwnedCliCommand.IsExactProcess(process, current)) throw new InvalidOperationException("The exact owned server is gone or its PID was reused.");
        OwnedCliCommand.RequirePortOwner(current.Port, current.Pid, "server");
    }

    private sealed class GuardedTransport(IGameTransport inner, Action proof, Action dispatched) : IGameTransport
    {
        internal bool Armed;
        private int _armedCalls;
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            proof();
            if (Armed && ++_armedCalls == 2) dispatched(); // GameActor.Execute first rechecks strict pins, then sends once.
            return inner.Execute(command, timeout);
        }
        public void Dispose() { } // The enclosing recording owns the connection.
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        WriteNew(ClosedFile, new { _route.Run, Utc = DateTime.UtcNow });
        ProcessPending(); // All unstarted requests receive durable refusals; current execution already settled.
    }
    public void Dispose() => Close();
    internal static Reply BoundReply(Reply reply)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(reply).Length <= MaxBytes) return reply;
        var bounded = reply with { Output = ["Output exceeds the control reply limit; see private command evidence."], Message = reply.Message[..Math.Min(reply.Message.Length, 1024)] +
            " Output omitted from the control reply; complete output is in private command evidence." };
        if (JsonSerializer.SerializeToUtf8Bytes(bounded).Length > MaxBytes)
            bounded = bounded with { Evidence = Path.GetFileName(bounded.Evidence) };
        return bounded;
    }
    internal static string Slot(Route route, int slot, string suffix) => Path.Combine(route.Control,
        "command-" + slot.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "." + suffix + ".json");
    internal static T Read<T>(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException("Detached control record exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(file) ?? throw new InvalidDataException("Empty detached control record.");
    }
    private static void Replace<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static void WriteNew<T>(string path, T value, bool bounded = true)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bounded && bytes.Length > MaxBytes) throw new InvalidDataException("Detached control record exceeds its size limit.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path); // Publish complete JSON atomically; an existing slot is never overwritten.
        }
        finally { File.Delete(temporary); }
    }
}
