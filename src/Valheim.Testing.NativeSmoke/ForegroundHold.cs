using System.Diagnostics;
using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>A finish request signals the live runner; it never takes ownership of its game or cleanup.</summary>
internal sealed class ForegroundHold : IDisposable
{
    internal const string FinishUsage = "valheim-test finish --run ID";
    internal static string FinishUsageForRun(string run) =>
        (DetachedSession.ConfiguredRun == run ? "valheim-test detach finish --run ID" : FinishUsage).Replace("ID", run, StringComparison.Ordinal);
    private sealed record Marker(string Run, string Evidence, string? ServerEvidence, int Pid, string Started);

    private readonly string _marker;
    private readonly string _request;
    private readonly Marker _identity;
    private bool _disposed;
    private DetachedCommandQueue? _commands;

    private ForegroundHold(string marker, string request, Marker identity)
    { _marker = marker; _request = request; _identity = identity; }

    internal static string DirectoryForThisMachine => Path.Combine(CliBundle.DataRoot, "runs", "holds");

    internal static ForegroundHold Open(string run, string evidence, string? directory = null, string? serverEvidence = null)
    {
        if (!RunJournal.SafeName(run) || run == "-") throw new ArgumentException("Invalid run ID.", nameof(run));
        evidence = Path.GetFullPath(evidence);
        if (!Directory.Exists(evidence)) throw new DirectoryNotFoundException("Hold evidence does not exist: " + evidence);
        if (serverEvidence != null)
        {
            serverEvidence = Path.GetFullPath(serverEvidence);
            if (!Directory.Exists(serverEvidence)) throw new DirectoryNotFoundException("Server hold evidence does not exist: " + serverEvidence);
        }
        directory ??= DetachedSession.ConfiguredControlDirectory ?? DirectoryForThisMachine;
        Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        var identity = new Marker(run, evidence, serverEvidence, process.Id, OwnedClientCommandLease.LocalStartFileTimeUtc(process));
        string marker = Path.Combine(directory, run + ".json"), request = Path.Combine(directory, run + ".finish");
        if (File.Exists(request)) throw new IOException("A finish request already exists for run " + run + "; inspect the old run before reusing its ID.");
        // A stale marker must be recovered deliberately; a second owner must never overwrite it.
        using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(file, identity);
        var hold = new ForegroundHold(marker, request, identity);
        try { DetachedSession.SignalHeld(run, evidence, identity.Pid, identity.Started); }
        catch { hold.Dispose(); throw; }
        return hold;
    }

    internal static async Task HoldAsync(string run, string evidence, CancellationToken cancellation,
        IReadOnlyList<(string Name, IOwnedProcess Process)> processes, bool client = true, string? serverEvidence = null, DetachedCheckpoints? checkpoints = null)
    {
        if (processes.Count == 0) throw new ArgumentException("A hold needs at least one owned process to watch.", nameof(processes));
        // Publish the route before signalling readiness. Only this loop executes detached commands; finish and cancellation
        // close admission and await its current dispatch before the caller can tear down the server.
        using var checkpointLifetime = checkpoints;
        using var commands = DetachedSession.ConfiguredRun == run
            ? DetachedCommandQueue.Open(run, serverEvidence ?? evidence, processes.Single(process => process.Name == "server").Process, checkpoints)
            : null;
        using var hold = Open(run, evidence, serverEvidence: serverEvidence);
        hold._commands = commands;
        Console.WriteLine("HELD run " + run + "; finish from another shell with: " + FinishUsageForRun(run));
        if (ClientCommandHint(client, evidence) is { } clientHint) Console.WriteLine(clientHint);
        if (ServerCommandHint(processes.Any(process => process.Name == "server"), evidence, serverEvidence) is { } serverHint)
            Console.WriteLine(serverHint);
        Console.Out.Flush();
        try
        {
            if (checkpoints != null) await commands!.WaitAsync(cancellation).ConfigureAwait(false);
            else await hold.WaitAsync(cancellation, processes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && checkpoints == null) { }
    }

    // Printed only where the held client's session published its lease: a client on another machine has none, so no command reaches it.
    internal static string? ClientCommandHint(bool hasClient, string evidence) =>
        hasClient && File.Exists(Path.Combine(evidence, OwnedClientCommandLease.FileName))
            ? "Pinned client commands: valheim-test cli --evidence " + evidence + " --role client --phase world --command cli_screenshot"
            : null;

    internal static string? ServerCommandHint(bool hasServer, string evidence, string? serverEvidence)
    {
        string commandEvidence = serverEvidence ?? evidence;
        // An SSH-hosted server is watched by this runner too, but its remote process cannot publish a lease on this machine.
        if (!hasServer || !File.Exists(Path.Combine(commandEvidence, OwnedClientCommandLease.ServerFileName))) return null;
        return "Pinned server commands: valheim-test cli --evidence " + commandEvidence + " --role server --phase world --command cli_manifest";
    }

    internal static int FinishCommand(string[] args, TextWriter? output = null, TextWriter? error = null, string? directory = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args is not ["--run", { } run]) { error.WriteLine("Usage: " + FinishUsage); return 2; }
        try
        {
            Request(run, directory);
            output.WriteLine("Finish requested for held run " + run + "; its original runner will leave, stop and verify cleanup.");
            return 0;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
    }

    internal Task WaitAsync(CancellationToken cancellation) => WaitForFinishAsync(cancellation);

    internal async Task WaitAsync(CancellationToken cancellation, IReadOnlyList<(string Name, IOwnedProcess Process)> processes)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var elapsed = Stopwatch.StartNew();
        Task finish = WaitForFinishAsync(waiting.Token);
        Task<int>[] exits = processes.Select(actor => actor.Process.WaitForExitAsync(waiting.Token)).ToArray();
        try
        {
            Task completed = await Task.WhenAny([finish, .. exits]).ConfigureAwait(false);
            // A client that exited at the same time as finish is still an unexpected exit: the
            // holding runner has not started its teardown yet.
            for (int i = 0; i < exits.Length; i++)
                if (exits[i].IsCompleted && (completed == exits[i] || !cancellation.IsCancellationRequested))
                {
                    int code = await exits[i].ConfigureAwait(false);
                    throw new InvalidOperationException($"Held {processes[i].Name} exited during the hold with code {code} after {elapsed.Elapsed.TotalSeconds:F1} s.");
                }
            await finish.ConfigureAwait(false);
        }
        finally
        {
            waiting.Cancel();
            try { await finish.ConfigureAwait(false); } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
            foreach (Task<int> exit in exits)
                try { await exit.ConfigureAwait(false); } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
        }
    }

    private async Task WaitForFinishAsync(CancellationToken cancellation)
    {
        if (_commands != null)
        {
            await _commands.WaitAsync(cancellation).ConfigureAwait(false);
            return;
        }
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(_request)!)
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FileSystemEventHandler onCreated = (_, change) => { if (change.FullPath == _request) requested.TrySetResult(); };
        RenamedEventHandler onRenamed = (_, change) => { if (change.FullPath == _request) requested.TrySetResult(); };
        watcher.Created += onCreated;
        watcher.Changed += onCreated;
        watcher.Renamed += onRenamed;
        try
        {
            // Check after subscribing so a request cannot slip between the check and the watcher.
            if (File.Exists(_request)) return;
            // Some macOS filesystems coalesce the create notification for a new empty file. The
            // watcher is the normal wakeup; a bounded fallback check prevents a stranded hold.
            while (!File.Exists(_request))
            {
                cancellation.ThrowIfCancellationRequested();
                Task eventTask = requested.Task;
                await Task.WhenAny(eventTask, Task.Delay(TimeSpan.FromSeconds(2), cancellation)).ConfigureAwait(false);
                if (eventTask.IsCompleted && !File.Exists(_request))
                    requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        finally { watcher.Created -= onCreated; watcher.Changed -= onCreated; watcher.Renamed -= onRenamed; }
    }

    internal static void Request(string run, string? directory = null)
    {
        directory ??= DirectoryForThisMachine;
        _ = LiveMarker(run, directory);
        using var request = new FileStream(Path.Combine(directory, run + ".finish"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    /// <summary>
    /// The evidence folder of run <paramref name="run"/>, held on this machine by its exact live runner, with no finish requested:
    /// where its owned game's command lease and pins are. Refuses a run that is not held here, has ended or is finishing.
    /// </summary>
    internal static string HeldEvidence(string run, string? directory = null)
    {
        directory ??= DirectoryForThisMachine;
        var identity = LiveMarker(run, directory);
        if (File.Exists(Path.Combine(directory, run + ".finish")))
            throw new InvalidOperationException("Run " + run + " is finishing (a finish was requested); its world is being moved out.");
        return identity.Evidence;
    }

    // The hold marker of a run whose exact runner is still alive.
    private static Marker LiveMarker(string run, string directory)
    {
        if (!RunJournal.SafeName(run) || run == "-") throw new ArgumentException("Invalid run ID.", nameof(run));
        string marker = Path.Combine(directory, run + ".json");
        if (!File.Exists(marker))
            throw new FileNotFoundException("Run " + run + " is not held on this machine (no hold marker " + marker + "); valheim-test env status lists the runs here.", marker);
        var identity = JsonSerializer.Deserialize<Marker>(File.ReadAllText(marker))
            ?? throw new InvalidDataException("The hold marker is empty.");
        if (identity.Run != run || identity.Pid <= 0 || identity.Started.Length == 0)
            throw new InvalidDataException("The hold marker has an invalid owner.");
        if (!ExactOwner(identity.Pid, identity.Started))
            throw new InvalidOperationException("The holding runner is gone; use valheim-test env status and env recover for its run.");
        return identity;
    }

    // Whether the hold's recorded runner is still that exact live process. finish runs in another shell, so the marker and
    // this check use the one cross-process identity rule (OwnedClientCommandLease.LocalStartFileTimeUtc); a detached run's
    // readiness record carries the same marker identity.
    internal static bool ExactOwner(int pid, string started)
    {
        try { return OwnedClientCommandLease.LocalStartFileTimeUtc(pid) == started; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { return false; } // No such process, it exited, or (Linux) its /proc entry vanished during the read.
    }

    // Recovery already proved the run has no live owner or owned game. Retire only the marker
    // naming that run, and never remove a marker whose exact owner is still alive.
    internal static void RetireRecovered(string run, string? directory = null)
    {
        if (!RunJournal.SafeName(run) || run == "-") throw new ArgumentException("Invalid run ID.", nameof(run));
        directory ??= DirectoryForThisMachine;
        string marker = Path.Combine(directory, run + ".json");
        if (!File.Exists(marker)) return;
        string source = File.ReadAllText(marker);
        var identity = JsonSerializer.Deserialize<Marker>(source)
            ?? throw new InvalidDataException("The hold marker is empty.");
        if (identity.Run != run || identity.Pid <= 0 || identity.Started.Length == 0)
            throw new InvalidDataException("The hold marker has an invalid owner.");
        if (ExactOwner(identity.Pid, identity.Started)) throw new InvalidOperationException("The holding runner is still alive; finish it instead of recovering it.");
        if (File.ReadAllText(marker) != source) throw new IOException("The hold marker changed during recovery.");
        // env recover has already proved the owned game is gone. The hold's world/menu pins and
        // process pointer are live-session capabilities, not lasting evidence; command records stay.
        OwnedClientCommandLease.Retire(identity.Evidence);
        if (identity.ServerEvidence != null) OwnedClientCommandLease.RetireServer(identity.ServerEvidence);
        File.Delete(Path.Combine(directory, run + ".finish"));
        File.Delete(marker);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Do not remove another owner's marker if the folder was altered during the hold.
        try
        {
            if (JsonSerializer.Deserialize<Marker>(File.ReadAllText(_marker)) == _identity)
            {
                File.Delete(_request);
                File.Delete(_marker);
            }
        }
        catch (FileNotFoundException) { }
    }
}
