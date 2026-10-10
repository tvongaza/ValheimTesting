using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

// A real host whose macOS bundle check (MacAppBundle.Bash) answers "accepted": for tests that run the real BSD copy and
// staging scripts on a fake, unsigned Valheim.app, which the real check rightly refuses (MacAppBundleTests covers that
// check against real codesign). Records each bundle check, so a test can show the stage asked about its copy. Copy tests
// can also supply an idle process verdict; HostedRuntimeStageTests exercise the real process guard separately.
internal sealed class BundleAcceptingHost(IGameHost inner, bool assumeNoGame = false) : IGameHost
{
    public List<IReadOnlyDictionary<string, string>> BundleChecks { get; } = [];
    public string Name => inner.Name;
    public GameHostKind Kind => inner.Kind;
    public HostShell Shell => inner.Shell;

    public Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (assumeNoGame && (script == HostedRuntimeStage.WindowsProcessCheck || script == HostedRuntimeStage.BashProcessCheck))
            return Task.FromResult(new HostResult(HostOutcome.Exited, 0, "VT-GAME idle\n", "", TimeSpan.Zero, false));
        if (script != MacAppBundle.Bash) return inner.RunAsync(script, variables, timeout, cancellation);
        BundleChecks.Add(new Dictionary<string, string>(variables ?? new Dictionary<string, string>()));
        return Task.FromResult(new HostResult(HostOutcome.Exited, 0, "VT-BUNDLE accepted 0 -\n", "", TimeSpan.Zero, false));
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
