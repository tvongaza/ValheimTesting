using System.Collections.Concurrent;
using System.Diagnostics;
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
internal sealed class StandInServer : IAsyncDisposable
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
        Guid registration = StandInProcessGuard.BeginServer(host, launch, bootDirectory, logs ?? [], evidence, test);
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
        string test, Func<HostServerProcess, Task> body, IReadOnlyList<string>? logs = null, string? evidence = null)
    {
        await using var server = await StartAsync(host, launch, bootDirectory, timeout, test, logs, evidence);
        await body(server.Process);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await StandInProcessGuard.StopAndReleaseAsync(_registration);
    }
}

/// <summary>One test-owned stand-in client. Disposal uses the client's own stop and proves its exact PID is gone.</summary>
internal sealed class StandInClient : IAsyncDisposable
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
        Guid registration = StandInProcessGuard.BeginClient(host, launch, launchDirectory, test);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await StandInProcessGuard.StopAndReleaseAsync(_registration);
    }
}

/// <summary>Every real test launch is registered before its mutation, so even a lost start reply remains visible.</summary>
internal static class StandInProcessGuard
{
    private enum ProcessKind { Server, Client }

    private sealed class Entry(ProcessKind kind, IGameHost host, GameLaunch launch, string directory, string test,
        IReadOnlyList<string>? logs = null, string? evidence = null)
    {
        public ProcessKind Kind { get; } = kind;
        public IGameHost Host { get; } = host;
        public GameLaunch Launch { get; } = launch;
        public string Directory { get; } = directory;
        public IReadOnlyList<string> Logs { get; } = logs ?? [];
        public string? Evidence { get; } = evidence;
        public string Test { get; } = test;
        public HostServerProcess? Server { get; set; }
        public InteractiveClientProcess? Client { get; set; }
        public int Id => Server?.Id ?? Client?.Id ?? 0;
        public string? StartIdentity => Server?.StartIdentity ?? Client?.StartIdentity;
    }

    private static readonly ConcurrentDictionary<Guid, Entry> Entries = [];
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    internal static Guid BeginServer(IGameHost host, GameLaunch launch, string bootDirectory,
        IReadOnlyList<string> logs, string? evidence, string test)
        => Begin(new Entry(ProcessKind.Server, host, launch, bootDirectory, test, logs, evidence));

    internal static Guid BeginClient(IGameHost host, GameLaunch launch, string launchDirectory, string test)
        => Begin(new Entry(ProcessKind.Client, host, launch, launchDirectory, test));

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
        if (failures.Count != 0)
            throw new InvalidOperationException("Stand-in process leak guard failed:\n" + string.Join("\n", failures));
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
        if (entry.Id != 0) return;
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

// A test-only host decorator used to make the macOS start's identity read fail after its native stand-in has launched.
internal sealed class MacIdentityFailingHost(IGameHost inner) : IGameHost
{
    internal const string CaptureFile = "test-start-identity";
    public string Name => inner.Name;
    public GameHostKind Kind => inner.Kind;
    public HostShell Shell => inner.Shell;

    public Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout,
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
            if (ReferenceEquals(replaced, script)) throw new InvalidOperationException("The Mac test handoff seam no longer matches the start script.");
            script = replaced;
        }
        return inner.RunAsync(script, variables, timeout, cancellation);
    }

    public Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => inner.AcquireLockAsync(lockPath, owner, timeout, cancellation);
    public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => inner.CheckLockAsync(lockPath, owner, timeout, cancellation);
    public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => inner.ReleaseLockAsync(lockPath, owner, timeout, cancellation);
    public Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => inner.ShipRevisionAsync(repository, revision, hostDirectory, timeout, cancellation);
    public Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => inner.ShipFilesAsync(localDirectory, hostDirectory, timeout, cancellation);
    public Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default) => inner.LogOffsetAsync(logPath, timeout, cancellation);
    public Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default) => inner.WaitForLogAsync(logPath, fromOffset, success, failures, timeout, cancellation);
    public Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default) => inner.FetchDirectoryAsync(hostDirectory, localDirectory, timeout, cancellation);
    public Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default) => inner.OpenCliTunnelAsync(hostPort, readyTimeout, localPort, cancellation);
}

/// <summary>Builds and ships the one native macOS server stand-in used by every real Mac HostServer lifecycle test.</summary>
internal static class MacServerStandIn
{
    internal static async Task<(GameLaunch Launch, string Install)> PrepareAsync(IGameHost host, string root, TimeSpan timeout)
    {
        using var source = new TempDirectory();
        string game = Path.Combine(source.Path, "valheim_server", "Valheim");
        string library = Path.Combine(source.Path, "libdoorstop.dylib");
        string code = Path.Combine(source.Path, "stand-in.c");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        File.WriteAllText(code, """
            #include <signal.h>
            #include <stdio.h>
            #include <unistd.h>
            static volatile sig_atomic_t stop;
            static void on_stop(int signal) { (void)signal; stop = 1; }
            int main(void) {
              signal(SIGINT, on_stop);
              puts("native server running"); fflush(stdout);
              while (!stop) usleep(10000);
              puts("native server stopped"); fflush(stdout);
              return 0;
            }
            """);
        Compile(game, code, library: false);
        File.WriteAllText(code, "void vt_doorstop_stand_in(void) {}\n");
        Compile(library, code, library: true);
        string core = Path.Combine(source.Path, "BepInEx", "core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "test preloader");
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "test core");
        string install = root + "/install", profile = root + "/profile";
        await host.ShipFilesAsync(Path.Combine(source.Path, "valheim_server"), install + "/valheim_server", timeout);
        await host.ShipFilesAsync(source.Path, profile, timeout);
        return (GameLaunch.ForServerWithLoader(install, profile, ["-nographics"], null, ServerPlatform.MacOS), install);
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
