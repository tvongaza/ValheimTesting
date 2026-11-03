using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("StandInProcessTestFramework", "Valheim.Testing.Tests")]

// The test assembly's last owner: after every collection has finished, report the test which left a real process alive and
// stop that exact process before the test host exits. A cleanup failure is an xUnit assembly-cleanup failure.
public sealed class StandInProcessTestFramework(IMessageSink diagnosticMessageSink) : XunitTestFramework(diagnosticMessageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new StandInProcessTestFrameworkExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
}

public sealed class StandInProcessTestFrameworkExecutor(
    AssemblyName assemblyName,
    ISourceInformationProvider sourceInformationProvider,
    IMessageSink diagnosticMessageSink)
    : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
{
    protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
    {
        using var runner = new StandInProcessTestAssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink,
            executionMessageSink, executionOptions);
        await runner.RunAsync();
    }
}

public sealed class StandInProcessTestAssemblyRunner(
    ITestAssembly testAssembly,
    IEnumerable<IXunitTestCase> testCases,
    IMessageSink diagnosticMessageSink,
    IMessageSink executionMessageSink,
    ITestFrameworkExecutionOptions executionOptions)
    : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
{
    protected override async Task BeforeTestAssemblyFinishedAsync()
    {
        await base.BeforeTestAssemblyFinishedAsync();
        await Aggregator.RunAsync(StandInProcessGuard.ThrowIfLeakedAsync);
    }
}

/// <summary>One test-owned stand-in server. Disposal stops it and independently proves its exact PID is gone.</summary>
internal sealed class StandInServer
{
    private readonly Guid _registration;
    private int _disposed;

    private StandInServer(Guid registration, HostServerProcess process)
    {
        _registration = registration;
        Process = process;
    }

    public HostServerProcess Process { get; }
    internal Guid Registration => _registration;

    public static async Task<StandInServer> StartAsync(IGameHost host, GameLaunch launch, string bootDirectory,
        TimeSpan timeout, string test, IReadOnlyList<string>? logs = null, string? evidence = null,
        IReadOnlyDictionary<string, string>? logonSeams = null, CancellationToken cancellation = default)
    {
        Guid registration = await StandInProcessGuard.BeginServerAsync(host, launch, bootDirectory, logs ?? [], evidence,
            test, timeout, cancellation);
        try
        {
            var process = await HostServer.StartAsync(host, launch, bootDirectory, timeout, logs, evidence, logonSeams, cancellation);
            StandInProcessGuard.Started(registration, process);
            return new StandInServer(registration, process);
        }
        catch (Exception startError)
        {
            try { await StandInProcessGuard.CleanupFailedStartAsync(registration); }
            catch (Exception cleanupError) { throw new AggregateException(startError, cleanupError); }
            throw;
        }
    }

    public static async Task RunAsync(IGameHost host, GameLaunch launch, string bootDirectory, TimeSpan timeout,
        string test, Func<HostServerProcess, Task> body, IReadOnlyList<string>? logs = null, string? evidence = null,
        IReadOnlyDictionary<string, string>? logonSeams = null, CancellationToken cancellation = default)
    {
        var server = await StartAsync(host, launch, bootDirectory, timeout, test, logs, evidence, logonSeams, cancellation);
        await RunBodyWithCleanupAsync(() => body(server.Process), server.DisposeAsync);
    }

    internal const string CleanupErrorKey = "StandInServer cleanup error";

    internal static async Task RunBodyWithCleanupAsync(Func<Task> body, Func<ValueTask> cleanup)
    {
        Exception? bodyError = null;
        try { await body(); }
        catch (Exception error) { bodyError = error; }

        try { await cleanup(); }
        catch (Exception cleanupError) when (bodyError != null)
        {
            bodyError.Data[CleanupErrorKey] = cleanupError;
        }

        if (bodyError != null) ExceptionDispatchInfo.Capture(bodyError).Throw();
    }

    private async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await StandInProcessGuard.StopAndReleaseAsync(_registration);
    }
}

/// <summary>One test-owned stand-in client. Disposal uses the client's own stop and proves its exact PID is gone.</summary>
internal sealed class StandInClient
{
    private readonly Guid _registration;
    private int _disposed;

    private StandInClient(Guid registration, InteractiveClientProcess process)
    {
        _registration = registration;
        Process = process;
    }

    public InteractiveClientProcess Process { get; }

    public static async Task<StandInClient> StartAsync(IGameHost host, GameLaunch launch, string launchDirectory,
        TimeSpan timeout, string test, Action? beforeLaunch = null, CancellationToken cancellation = default)
    {
        Guid registration = await StandInProcessGuard.BeginClientAsync(host, launch, launchDirectory, test, timeout, cancellation);
        try
        {
            var process = await InteractiveClient.StartAsync(host, launch, launchDirectory, timeout, cancellation, beforeLaunch);
            StandInProcessGuard.Started(registration, process);
            return new StandInClient(registration, process);
        }
        catch (Exception startError)
        {
            try { await StandInProcessGuard.CleanupFailedStartAsync(registration); }
            catch (Exception cleanupError) { throw new AggregateException(startError, cleanupError); }
            throw;
        }
    }

    /// <summary>Starts the stand-in, runs <paramref name="body"/>, then stops it; a body failure stays the failure (as <see cref="StandInServer.RunAsync"/>).</summary>
    public static async Task RunAsync(IGameHost host, GameLaunch launch, string launchDirectory, TimeSpan timeout, string test,
        Func<InteractiveClientProcess, Task> body, Action? beforeLaunch = null, CancellationToken cancellation = default)
    {
        var client = await StartAsync(host, launch, launchDirectory, timeout, test, beforeLaunch, cancellation);
        await StandInServer.RunBodyWithCleanupAsync(() => body(client.Process), client.DisposeAsync);
    }

    private async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await StandInProcessGuard.StopAndReleaseAsync(_registration);
    }
}

/// <summary>A directly launched test helper registered with the assembly's stand-in guard before it starts.</summary>
internal sealed class StandInProcess : IAsyncDisposable
{
    private readonly Guid _registration;
    private int _disposed;

    private StandInProcess(Guid registration, Process process)
    {
        _registration = registration;
        Process = process;
    }

    public Process Process { get; }

    public static StandInProcess Start(ProcessStartInfo start, string test)
    {
        Guid registration = StandInProcessGuard.BeginProcess(test);
        try
        {
            var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not start the stand-in process.");
            StandInProcessGuard.Started(registration, process);
            return new StandInProcess(registration, process);
        }
        catch
        {
            StandInProcessGuard.ReleaseUnstartedProcess(registration);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await StandInProcessGuard.StopAndReleaseProcessAsync(_registration);
    }
}

/// <summary>Every real test launch is registered before its mutation, so even a lost start reply remains visible.</summary>
internal static class StandInProcessGuard
{
    private enum ProcessKind { Server, Client }

    private sealed class Entry(ProcessKind kind, IGameHost host, GameLaunch launch, string directory, bool directoryExisted,
        string test, IReadOnlyList<string>? logs = null, string? evidence = null)
    {
        public ProcessKind Kind { get; } = kind;
        public IGameHost Host { get; } = host;
        public GameLaunch Launch { get; } = launch;
        public string Directory { get; } = directory;
        public bool DirectoryExisted { get; } = directoryExisted;
        public IReadOnlyList<string> Logs { get; } = logs ?? [];
        public string? Evidence { get; } = evidence;
        public string Test { get; } = test;
        public HostServerProcess? Server { get; set; }
        public InteractiveClientProcess? Client { get; set; }
        public int Id => Server?.Id ?? Client?.Id ?? 0;
        public string? StartIdentity => Server?.StartIdentity ?? Client?.StartIdentity;
    }

    private static readonly ConcurrentDictionary<Guid, Entry> Entries = [];
    private sealed class DirectEntry(string test)
    {
        public string Test { get; } = test;
        public Process? Process { get; set; }
        public string? StartIdentity { get; set; }
    }
    private static readonly ConcurrentDictionary<Guid, DirectEntry> DirectEntries = [];
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<Guid> BeginServerAsync(IGameHost host, GameLaunch launch, string bootDirectory,
        IReadOnlyList<string> logs, string? evidence, string test, TimeSpan timeout, CancellationToken cancellation = default)
        => Begin(new Entry(ProcessKind.Server, host, launch, bootDirectory,
            await DirectoryExistsAsync(host, bootDirectory, timeout, cancellation), test, logs, evidence));

    internal static async Task<Guid> BeginClientAsync(IGameHost host, GameLaunch launch, string launchDirectory,
        string test, TimeSpan timeout, CancellationToken cancellation = default)
        => Begin(new Entry(ProcessKind.Client, host, launch, launchDirectory,
            await DirectoryExistsAsync(host, launchDirectory, timeout, cancellation), test));

    private static async Task<bool> DirectoryExistsAsync(IGameHost host, string directory, TimeSpan timeout,
        CancellationToken cancellation)
    {
        string script = host.Shell.Kind == HostShellKind.PowerShell
            ? "if ([IO.Directory]::Exists($dir) -or [IO.File]::Exists($dir)) { 'VT-EXISTS yes' } else { 'VT-EXISTS no' }"
            : "if [ -e \"$dir\" ]; then echo 'VT-EXISTS yes'; else echo 'VT-EXISTS no'; fi";
        var result = (await host.RunAsync(script, new Dictionary<string, string> { ["dir"] = directory }, timeout,
            cancellation)).EnsureSuccess($"Checking the stand-in launch directory on {host.Name}");
        return InteractiveClient.Line(result.Stdout, "VT-EXISTS ") switch
        {
            "yes" => true,
            "no" => false,
            _ => throw new HostOperationException($"Unexpected reply while checking the stand-in launch directory on {host.Name}", result),
        };
    }

    private static Guid Begin(Entry entry)
    {
        var registration = Guid.NewGuid();
        if (!Entries.TryAdd(registration, entry))
            throw new InvalidOperationException("Could not register a stand-in process launch.");
        return registration;
    }

    internal static void Started(Guid registration, HostServerProcess process) =>
        Entries[registration].Server = process;

    internal static void Started(Guid registration, InteractiveClientProcess process) =>
        Entries[registration].Client = process;

    internal static Guid BeginProcess(string test)
    {
        var registration = Guid.NewGuid();
        if (!DirectEntries.TryAdd(registration, new(test)))
            throw new InvalidOperationException("Could not register a stand-in process launch.");
        return registration;
    }

    internal static void Started(Guid registration, Process process)
    {
        var entry = DirectEntries[registration];
        entry.Process = process;
        entry.StartIdentity = process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static void ReleaseUnstartedProcess(Guid registration) => DirectEntries.TryRemove(registration, out _);

    internal static async Task StopAndReleaseProcessAsync(Guid registration)
    {
        if (!DirectEntries.TryGetValue(registration, out var entry)) return;
        var process = entry.Process ?? throw new InvalidOperationException($"The stand-in process from {entry.Test} has no identity to stop.");
        Exception? stopError = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(CleanupTimeout);
            }
        }
        catch (Exception error) { stopError = error; }

        if (DirectProcessGone(process.Id, entry.StartIdentity!))
        {
            DirectEntries.TryRemove(registration, out _);
            process.Dispose();
        }
        else
        {
            var stillRunning = new InvalidOperationException($"Stand-in process {process.Id} from {entry.Test} still has its launch identity after cleanup.");
            stopError = stopError == null ? stillRunning : new AggregateException(stopError, stillRunning);
        }
        if (stopError != null) throw stopError;
    }

    internal static async Task CleanupFailedStartAsync(Guid registration)
    {
        if (!Entries.TryGetValue(registration, out var entry)) return;
        await ResolveAsync(entry);
        if (entry.Id != 0) await StopAndReleaseAsync(registration);
        else
        {
            // A launch which returned no owner and no pid file is settled only by the independent runtime process scan. It is
            // scoped to this launch's game directory: a client-session scan would also see a parallel test's client.
            await HostedRuntimeStage.RequireStoppedAsync(entry.Host, CleanupTimeout,
                runtimes: [entry.Launch.GameDirectory], clientSession: false);
            Entries.TryRemove(registration, out _);
        }
    }

    internal static async Task StopAndReleaseAsync(Guid registration)
    {
        if (!Entries.TryGetValue(registration, out var entry)) return;
        Exception? stopError = null;
        try
        {
            if (entry.Server != null) await entry.Server.DisposeAsync();
            else if (entry.Client != null) await entry.Client.DisposeAsync();
            else throw new InvalidOperationException($"The stand-in process from {entry.Test} has no identity to stop.");
        }
        catch (Exception error) { stopError = error; }

        var state = await ProbeAsync(entry);
        if (state is ProbedState.Gone or ProbedState.Reused)
            Entries.TryRemove(registration, out _);
        else
        {
            var stillRunning = new InvalidOperationException(
                $"Stand-in process {entry.Id} from {entry.Test} " +
                $"was {state.ToString().ToLowerInvariant()} after its fixture stopped it.");
            stopError = stopError == null ? stillRunning : new AggregateException(stopError, stillRunning);
        }

        if (stopError != null) throw stopError;
    }

    internal static async Task ThrowIfLeakedAsync()
    {
        var failures = new List<string>();
        foreach (var pair in Entries.ToArray())
        {
            try
            {
                string? leak = await InspectAndCleanupAsync(pair.Key, pair.Value);
                if (leak != null) failures.Add(leak);
            }
            catch (Exception error)
            {
                failures.Add($"Stand-in process started by {pair.Value.Test} could not be checked or cleaned: {error.Message}");
            }
        }
        foreach (var pair in DirectEntries.ToArray())
        {
            try
            {
                var process = pair.Value.Process;
                if (process == null)
                {
                    DirectEntries.TryRemove(pair.Key, out _);
                    continue;
                }
                if (DirectProcessGone(process.Id, pair.Value.StartIdentity!))
                {
                    DirectEntries.TryRemove(pair.Key, out _);
                    process.Dispose();
                    continue;
                }
                string leak = $"Stand-in process {process.Id} started by {pair.Value.Test} was still running at assembly teardown.";
                try { await StopAndReleaseProcessAsync(pair.Key); }
                catch (Exception error) { leak += " Cleanup also failed: " + error.Message; }
                failures.Add(leak);
            }
            catch (Exception error)
            {
                failures.Add($"Stand-in process started by {pair.Value.Test} could not be checked or cleaned: {error.Message}");
            }
        }
        if (failures.Count != 0)
            throw new InvalidOperationException("Stand-in process leak guard failed:\n" + string.Join("\n", failures));
    }

    private static bool DirectProcessGone(int pid, string startIdentity)
    {
        try
        {
            using var current = Process.GetProcessById(pid);
            if (current.HasExited) return true;
            return current.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture) != startIdentity;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static async Task<string?> InspectAndCleanupAsync(Guid registration, Entry entry)
    {
        await ResolveAsync(entry);
        if (entry.Id == 0)
        {
            try
            {
                await HostedRuntimeStage.RequireStoppedAsync(entry.Host, CleanupTimeout,
                    runtimes: [entry.Launch.GameDirectory], clientSession: false);
                Entries.TryRemove(registration, out _);
                return null;
            }
            catch (Exception error)
            {
                return $"Stand-in process launch from {entry.Test} returned no process identity and its runtime is not proven idle: {error.Message}";
            }
        }

        var state = await ProbeAsync(entry);
        if (state is ProbedState.Gone or ProbedState.Reused)
        {
            Entries.TryRemove(registration, out _);
            return null;
        }

        string leak = $"Stand-in process {entry.Id} started by {entry.Test} was still {state.ToString().ToLowerInvariant()} at assembly teardown.";
        try { await StopAndReleaseAsync(registration); }
        catch (Exception error) { leak += " Cleanup also failed: " + error.Message; }
        return leak;
    }

    private static async Task ResolveAsync(Entry entry)
    {
        if (entry.Id != 0 || entry.DirectoryExisted) return;
        var files = await RunJournalStatus.ReadPidFilesAsync(entry.Host, [entry.Directory], CleanupTimeout, default);
        var file = files[0];
        if (file.State == RunJournalStatus.PidFileState.Missing) return;
        if (file.State != RunJournalStatus.PidFileState.Found || file.StartIdentity == null)
            throw new InvalidOperationException($"The pid file in {entry.Directory} is {file.State.ToString().ToLowerInvariant()}: {file.Detail}");
        if (entry.Kind == ProcessKind.Server)
            entry.Server = new HostServerProcess(entry.Host, file.Pid, file.StartIdentity, entry.Directory,
                entry.Launch.GameDirectory, entry.Logs, entry.Evidence, entry.Launch.WorkingDirectory, entry.Launch.Platform);
        else
            entry.Client = new InteractiveClientProcess(entry.Host, entry.Launch.Platform, file.Pid, file.StartIdentity,
                entry.Directory, taskName: null);
    }

    private static async Task<ProbedState> ProbeAsync(Entry entry) =>
        (await HostProcessProbe.ProbeAsync(entry.Host,
            [(entry.Id, entry.StartIdentity!)], CleanupTimeout))[(entry.Id, entry.StartIdentity!)].State;
}

internal abstract class DelegatingGameHost(IGameHost inner) : IGameHost
{
    protected IGameHost Inner { get; } = inner;
    public string Name => Inner.Name;
    public GameHostKind Kind => Inner.Kind;
    public HostShell Shell => Inner.Shell;
    public virtual Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
        CancellationToken cancellation = default) => Inner.RunAsync(script, variables, timeout, cancellation);
    public Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => Inner.AcquireLockAsync(lockPath, owner, timeout, cancellation);
    public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => Inner.CheckLockAsync(lockPath, owner, timeout, cancellation);
    public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => Inner.ReleaseLockAsync(lockPath, owner, timeout, cancellation);
    public Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => Inner.ShipRevisionAsync(repository, revision, hostDirectory, timeout, cancellation);
    public Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => Inner.ShipFilesAsync(localDirectory, hostDirectory, timeout, cancellation);
    public Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default) => Inner.LogOffsetAsync(logPath, timeout, cancellation);
    public Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default) => Inner.WaitForLogAsync(logPath, fromOffset, success, failures, timeout, cancellation);
    public Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default) => Inner.FetchDirectoryAsync(hostDirectory, localDirectory, timeout, cancellation);
    public Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default) => Inner.OpenCliTunnelAsync(hostPort, readyTimeout, localPort, cancellation);
}

internal sealed class LinuxHandoffIgnoringHost(IGameHost inner) : DelegatingGameHost(inner)
{
    public override Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (ReferenceEquals(script, HostServerScripts.Start))
        {
            const string handoff = "; printf \"%s\\n\" \"$g\" >&4;";
            string replaced = script.Replace(handoff, "; :;", StringComparison.Ordinal);
            if (ReferenceEquals(replaced, script) || replaced.Contains(handoff, StringComparison.Ordinal))
                throw new TestSeamMismatchException("The Linux timeout seam no longer matches the start script.");
            script = replaced;
        }
        return Inner.RunAsync(script, variables, timeout, cancellation);
    }
}

/// <summary>
/// Pauses the real launcher (Linux, macOS or Windows) after it recorded the game in its ownership file and before it publishes the
/// pid file: it writes <c>OWNER.controller-gap</c>, then waits for <c>OWNER.release</c>.
/// </summary>
internal sealed class BeforePidPausedHost(IGameHost inner) : DelegatingGameHost(inner)
{
    public override Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        static string Pause(string text, string anchor, string pause, string what) => text.Split(anchor).Length == 2
            ? text.Replace(anchor, pause + anchor, StringComparison.Ordinal)
            : throw new TestSeamMismatchException($"The {what} before-pid seam no longer matches its script.");
        if (ReferenceEquals(script, HostServerScripts.Start))
            script = Pause(script, "printf \"%s %s\\n\" \"$g\" \"$gameStart\" > \"$p.tmp\"",
                "printf x > \"$o.controller-gap\"; while [ ! -e \"$o.release\" ]; do sleep 0.1; done; ", "Linux");
        else if (ReferenceEquals(script, HostServerScripts.MacStart))
            script = Pause(script, "open my $pid, \">\", \"$directory/pid.tmp\" or die \"pid: $!\";",
                "open my $gap, \">\", \"$directory/$owner.controller-gap\" or die \"gap: $!\"; print $gap \"x\"; close $gap; " +
                "while (!-e \"$directory/$owner.release\") { select undef, undef, undef, 0.1; } ", "macOS");
        else if (ReferenceEquals(script, HostServerScripts.WindowsStart))
        {
            var changed = new Dictionary<string, string>(variables ?? throw new InvalidOperationException("The Windows start had no variables."));
            changed["launcher"] = Pause(changed["launcher"], "$temporary = Join-Path $dir 'pid.tmp'",
                "[IO.File]::WriteAllText((Join-Path $dir ($ownerName + '.controller-gap')), 'x'); " +
                "while (-not [IO.File]::Exists((Join-Path $dir ($ownerName + '.release')))) { [Threading.Thread]::Sleep(100) }; ", "Windows");
            variables = changed;
        }
        return Inner.RunAsync(script, variables, timeout, cancellation);
    }
}

/// <summary>A local host launcher whose one marked controller can be killed without recursively killing its detached session.</summary>
internal sealed class KillableControllerLauncher : IProcessLauncher
{
    private readonly object _sync = new();
    private Process? _controller;

    public IStartedProcess Start(string executable, IReadOnlyList<string> arguments) =>
        SystemProcessLauncher.Instance.Start(executable, arguments);

    public async Task<ProcessExit> RunAsync(ProcessCall call, CancellationToken cancellation)
    {
        string payload = System.Text.Encoding.ASCII.GetString(call.Input);
        string first = payload.Split('\n', 2)[0];
        string composed;
        try { composed = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(first)); }
        catch (FormatException) { return await SystemProcessLauncher.Instance.RunAsync(call, cancellation); }
        if (!composed.Contains("controller-gap", StringComparison.Ordinal))
            return await SystemProcessLauncher.Instance.RunAsync(call, cancellation);

        var start = new ProcessStartInfo(call.Executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in call.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the test controller.");
        lock (_sync) _controller = process;
        await process.StandardInput.BaseStream.WriteAsync(call.Input, cancellation);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellation);
        lock (_sync) _controller = null;
        // The detached launcher and its short-lived Perl parent deliberately retain the controller's pipes. A killed controller
        // has no reply by definition; do not wait for those descendants to close inherited descriptors.
        return new(ProcessEnd.Exited, process.ExitCode, "", "test controller was killed without its detached process tree", TimeSpan.Zero);
    }

    internal void KillController()
    {
        lock (_sync)
        {
            if (_controller == null || _controller.HasExited) throw new InvalidOperationException("The marked controller is not running.");
            _controller.Kill(); // Deliberately not the tree: the detached launcher is what recovery must own.
        }
    }
}

// The Windows launcher with its pid publication replaced: it records the game's identity for the test only, then either
// keeps running (a launcher the deadline overtakes) or exits at once (a task whose action completed without a handoff).
// beforeOwnerRecord: the launcher stops instead right after it spawned the game, before its ownership record names it (#814).
internal sealed class WindowsHandoffIgnoringHost(IGameHost inner, bool launcherExits = false, bool beforeOwnerRecord = false) : DelegatingGameHost(inner)
{
    internal const string CaptureFile = "test-start-identity";

    public override Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (ReferenceEquals(script, HostServerScripts.WindowsStart))
        {
            var changed = new Dictionary<string, string>(variables ?? throw new InvalidOperationException("The Windows start had no variables."));
            string handoff = beforeOwnerRecord ? "Write-VtOwner $game" : "[IO.File]::Move($temporary, (Join-Path $dir 'pid'))";
            string replacement = $"[IO.File]::WriteAllText((Join-Path $dir '{CaptureFile}'), [string]$game.Id + ' ' + $game.StartTime.ToFileTimeUtc(), $utf8); " +
                (launcherExits ? "exit 0" : "[Threading.Thread]::Sleep([Threading.Timeout]::Infinite)");
            string launcher = changed["launcher"];
            if (launcher.Split(handoff).Length != 2)
                throw new TestSeamMismatchException("The Windows handoff seam no longer matches the launcher script.");
            changed["launcher"] = launcher.Replace(handoff, replacement, StringComparison.Ordinal);
            variables = changed;
        }
        return Inner.RunAsync(script, variables, timeout, cancellation);
    }
}

/// <summary>
/// Pauses the real Windows controller (the start script) once it has read the launcher's pid file and before it acknowledges it:
/// it writes <c>OWNER.controller-gap</c> into the boot directory and waits there until the test kills it (#814).
/// </summary>
internal sealed class AfterPidControllerPausedHost(IGameHost inner) : DelegatingGameHost(inner)
{
    public override Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (ReferenceEquals(script, HostServerScripts.WindowsStart))
        {
            const string anchor = "[IO.File]::WriteAllText($ackFile + '.tmp', 'ack', $utf8)";
            if (script.Split(anchor).Length != 2) throw new TestSeamMismatchException("The Windows after-pid seam no longer matches the start script.");
            script = script.Replace(anchor, "[IO.File]::WriteAllText($ownerFile + '.controller-gap', 'x', $utf8); " +
                "[Threading.Thread]::Sleep([Threading.Timeout]::Infinite); " + anchor, StringComparison.Ordinal);
        }
        return Inner.RunAsync(script, variables, timeout, cancellation);
    }
}

/// <summary>A test seam that no longer matches the script it rewrites: never mistaken for the failure a test expects.</summary>
internal sealed class TestSeamMismatchException(string message) : Exception(message);

/// <summary>Executes the real stop (macOS or Linux), then loses only its reply, so the fixture's cleanup fails after the process is gone.</summary>
internal sealed class StopReplyLosingHost(IGameHost inner) : DelegatingGameHost(inner)
{
    private int _lost;

    public override async Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        var result = await Inner.RunAsync(script, variables, timeout, cancellation);
        if ((ReferenceEquals(script, MacProcess.Stop) || ReferenceEquals(script, InteractiveScripts.LinuxStop)) &&
            Interlocked.Exchange(ref _lost, 1) == 0)
            return new HostResult(HostOutcome.TransportFailed, null, result.Stdout, "test seam lost the completed stop reply", result.Elapsed, false);
        return result;
    }
}

internal static class LauncherTimeoutStandIn
{
    // binary: the game is a copy of sleep itself, so its command line is the launch's (a recovery checks it); otherwise a script
    // that ignores SIGINT and executes sleep in its place.
    internal static GameLaunch Unix(string root, bool binary = false)
    {
        string runtime = Path.Combine(root, "runtime");
        FakeInstalls.Server(runtime);
        FakeInstalls.LinuxLoader(runtime);
        string executable = Path.Combine(runtime, GameLaunch.ServerLinuxExecutable);
        if (binary) File.Copy(File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep", executable, overwrite: true);
        else File.WriteAllText(executable, "#!/bin/bash\ntrap '' INT\nexec sleep 300\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        return GameLaunch.ForServer(runtime, binary ? ["300"] : [], hostPlatform: ServerPlatform.Linux);
    }

    internal static GameLaunch Windows(string root)
    {
        string runtime = Path.Combine(root, "runtime with spaces");
        Directory.CreateDirectory(runtime);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(runtime, GameLaunch.ServerWindowsExecutable));
        var launch = GameLaunch.ForServer(runtime, ["-n", "60", "127.0.0.1"], hostPlatform: ServerPlatform.Windows);
        foreach (string file in launch.RequiredFiles.Skip(1))
        {
            string path = Path.Combine(runtime, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test-only placeholder");
        }
        return launch;
    }

    internal static HostServerProcess? Captured(IGameHost host, GameLaunch launch, string boot, string file,
        ClientPlatform platform)
    {
        string path = Path.Combine(boot, file);
        if (!File.Exists(path)) return null;
        string[] identity = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (identity.Length != 2 || !int.TryParse(identity[0], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int pid) || !identity[1].All(char.IsAsciiDigit)) return null;
        return new HostServerProcess(host, pid, identity[1], boot, launch.GameDirectory, [], null,
            launch.WorkingDirectory, platform);
    }
}

// A test-only host decorator used to make the macOS start's identity read fail after its native stand-in has launched.
internal sealed class MacIdentityFailingHost(IGameHost inner) : DelegatingGameHost(inner)
{
    internal const string CaptureFile = "test-start-identity";

    public override Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (ReferenceEquals(script, HostServerScripts.MacStart))
        {
            const string handoff = "if ($identity !~ /^\\d+$/) { print $writer \"VT-SERVER failed no process identity\\n\"; close $writer; exit 3; }";
            const string capture = """
                          if ($identity =~ /^\d+$/) {
                            open my $captured, ">", "$directory/test-start-identity" or die "capture: $!";
                            print $captured "$game $identity\n"; close $captured;
                            $identity = "";
                          }
                """;
            string replaced = script.Replace(handoff, capture + handoff, StringComparison.Ordinal);
            if (ReferenceEquals(replaced, script)) throw new TestSeamMismatchException("The Mac test handoff seam no longer matches the start script.");
            script = replaced;
        }
        return Inner.RunAsync(script, variables, timeout, cancellation);
    }
}

/// <summary>Builds and ships the one native macOS server stand-in used by every real Mac HostServer lifecycle test.</summary>
internal static class MacServerStandIn
{
    // A Mac dedicated server's install (the stand-in executable in valheim_server/, the loader beside it) and its profile, made
    // as a run makes it (#612): the game linked two folders down (ProfileLayouts.GameFolder) by the profile seed, the loader
    // copied. The stand-in writes a marker where BepInEx puts Paths.GameRootPath on macOS, its executable's fourth parent.
    internal const string GameRootMarker = "vt-game-root-marker";

    internal static async Task<(GameLaunch Launch, string Install, string Profile)> PrepareAsync(IGameHost host, string root, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var source = new TempDirectory();
        string game = Path.Combine(source.Path, "valheim_server", "Valheim");
        string library = Path.Combine(source.Path, "libdoorstop.dylib");
        string code = Path.Combine(source.Path, "stand-in.c");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        File.WriteAllText(code, """
            #include <mach-o/dyld.h>
            #include <signal.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <unistd.h>
            static volatile sig_atomic_t stop;
            static void on_stop(int signal) { (void)signal; stop = 1; }
            int main(void) {
              char path[4096]; uint32_t size = sizeof path;
              if (_NSGetExecutablePath(path, &size) == 0) {
                for (int up = 0; up < 4; up++) { char *slash = strrchr(path, '/'); if (slash) *slash = 0; }
                char marker[4200]; snprintf(marker, sizeof marker, "%s/vt-game-root-marker", path);
                FILE *file = fopen(marker, "w"); if (file) { fputs("game root\n", file); fclose(file); }
              }
              signal(SIGINT, on_stop);
              const char *input = getenv("VT_TEST_INPUT"); if (input) printf("input=%s\n", input);
              puts("native server running"); fflush(stdout);
              while (!stop) usleep(10000);
              puts("native server stopped"); fflush(stdout);
              return 0;
            }
            """);
        Compile(game, code, library: false);
        File.WriteAllText(code, "void vt_doorstop_stand_in(void) {}\n");
        Compile(library, code, library: true);
        File.Delete(code);
        string core = Path.Combine(source.Path, "BepInEx", "core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "test preloader");
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "test core");
        string install = root + "/install", profile = root + "/profile", gameRoot = profile + "/" + ProfileLayouts.MacServerGameFolder;
        await host.ShipFilesAsync(source.Path, install, timeout);
        var listing = await HostInstall.ListAsync(host, install, timeout);
        static string Encode(string relative) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(relative));
        (await host.RunAsync(HostedRuntimeStage.BashProfileSeed, new Dictionary<string, string>
        {
            ["source"] = install, ["runtime"] = profile, ["game"] = gameRoot,
            ["gameFiles"] = string.Join('\n', HostedRuntimeStage.PlanLinkedProfile(ClientPlatform.MacOS, listing.Files.Keys).Links.Select(Encode)),
            ["files"] = string.Join('\n', listing.Files.Keys.Where(path => InstallPins.IsLoaderFile(path)).Select(Encode)),
        }, timeout)).EnsureSuccess("Seeding the Mac server stand-in's profile");
        return (GameLaunch.ForServerWithLoader(gameRoot, profile, ["-nographics"], environment, ServerPlatform.MacOS, linked: true), install, profile);
    }

    private static void Compile(string output, string input, bool library)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/clang")
        {
            ArgumentList = { "-arch", "arm64", library ? "-dynamiclib" : "-Wno-unused-command-line-argument", "-o", output, input },
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new IOException("Could not start clang for the Mac server stand-in.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not compile the Mac server stand-in: " + error);
    }
}

/// <summary>The child test assembly's one intentional leak, exposed only so it exercises this assembly's real teardown hook.</summary>
public static class StandInProcessLeakProbeDriver
{
    public const string IdentityFile = "stand-in-identity";

    public static async Task StartAsync(string root, string test)
    {
        Directory.CreateDirectory(root);
        var host = new LocalGameHost("assembly-guard-probe", HostShell.Bash);
        var prepared = await MacServerStandIn.PrepareAsync(host, root, GameHostChecks.Generous);
        var server = await StandInServer.StartAsync(host, prepared.Launch, Path.Combine(root, "boot"),
            GameHostChecks.Generous, test, logs: []);
        await File.WriteAllTextAsync(Path.Combine(root, IdentityFile),
            $"{server.Process.Id} {server.Process.StartIdentity}\n");
        GC.KeepAlive(server);
    }
}
