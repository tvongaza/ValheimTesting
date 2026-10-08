using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>A finish request signals the live runner; it never takes ownership of its game or cleanup.</summary>
internal sealed class ForegroundHold : IDisposable
{
    internal const string FinishUsage = "valheim-test finish --run ID";
    private sealed record Marker(string Run, string Evidence, int Pid, string Started);

    private readonly string _marker;
    private readonly string _request;
    private readonly Marker _identity;
    private bool _disposed;

    private ForegroundHold(string marker, string request, Marker identity)
    { _marker = marker; _request = request; _identity = identity; }

    internal static string DirectoryForThisMachine => Path.Combine(CliBundle.DataRoot, "runs", "holds");

    internal static ForegroundHold Open(string run, string evidence, string? directory = null)
    {
        if (!RunJournal.SafeName(run) || run == "-") throw new ArgumentException("Invalid run ID.", nameof(run));
        evidence = Path.GetFullPath(evidence);
        if (!Directory.Exists(evidence)) throw new DirectoryNotFoundException("Hold evidence does not exist: " + evidence);
        directory ??= DirectoryForThisMachine;
        Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        var identity = new Marker(run, evidence, process.Id, process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));
        string marker = Path.Combine(directory, run + ".json"), request = Path.Combine(directory, run + ".finish");
        if (File.Exists(request)) throw new IOException("A finish request already exists for run " + run + "; inspect the old run before reusing its ID.");
        // A stale marker must be recovered deliberately; a second owner must never overwrite it.
        using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(file, identity);
        return new ForegroundHold(marker, request, identity);
    }

    internal static async Task HoldAsync(string run, string evidence, CancellationToken cancellation, bool client = true)
    {
        using var hold = Open(run, evidence);
        Console.WriteLine("HELD run " + run + "; finish from another shell with: " + FinishUsage.Replace("ID", run, StringComparison.Ordinal));
        if (client) Console.WriteLine("Pinned client commands: valheim-test cli --evidence " + evidence + " --phase world --command cli_screenshot");
        Console.Out.Flush();
        try { await hold.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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

    internal async Task WaitAsync(CancellationToken cancellation)
    {
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
        if (!RunJournal.SafeName(run) || run == "-") throw new ArgumentException("Invalid run ID.", nameof(run));
        directory ??= DirectoryForThisMachine;
        string marker = Path.Combine(directory, run + ".json");
        var identity = JsonSerializer.Deserialize<Marker>(File.ReadAllText(marker))
            ?? throw new InvalidDataException("The hold marker is empty.");
        if (identity.Run != run || identity.Pid <= 0 || identity.Started.Length == 0)
            throw new InvalidDataException("The hold marker has an invalid owner.");
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited || process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) != identity.Started)
                throw new InvalidOperationException("The holding runner is gone; use valheim-test env status and env recover for its run.");
        }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("The holding runner is gone; use valheim-test env status and env recover for its run.");
        }
        using var request = new FileStream(Path.Combine(directory, run + ".finish"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
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
