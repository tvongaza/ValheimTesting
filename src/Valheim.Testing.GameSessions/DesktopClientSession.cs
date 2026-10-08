using System.Diagnostics;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// Opens a disposable Windows client. An SSH/session-0 runner starts it in the signed-in desktop through a temporary task;
/// a runner already in that desktop uses the ordinary direct client path. Both retain exact process ownership and logs.
/// </summary>
internal static class DesktopClientSession
{
    // A Windows SSH process runs in session 0; an ordinary terminal already has the desktop token and needs no task.
    internal static bool NeedsDesktopTask(int currentSessionId) => currentSessionId == 0;

    private static readonly AsyncLocal<Func<CancellationToken, Task>?> s_preflightForTest = new();

    // The NativeSmoke offline fixture has no interactive Windows desktop. Scope only that fixture's initial guard;
    // Open still performs the real desktop check before it launches a client.
    internal static IDisposable ReplacePreflightForTest(Func<CancellationToken, Task> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = s_preflightForTest.Value;
        s_preflightForTest.Value = replacement;
        return new PreflightReset(previous);
    }

    private sealed class PreflightReset(Func<CancellationToken, Task>? previous) : IDisposable
    {
        public void Dispose() => s_preflightForTest.Value = previous;
    }

    /// <summary>Refuses without a side effect if this runner cannot reach one Steam-backed Windows desktop session.</summary>
    public static Task PreflightAsync(CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A desktop task is only needed for a Windows client started from an SSH session.");
        if (s_preflightForTest.Value is { } substitute) return substitute(cancellation);
        return InteractiveClient.RequireWindowsDesktopAsync(new LocalGameHost("this machine", HostShell.WindowsPowerShell), cancellation);
    }

    /// <summary>Launches the plan's pinned disposable install in the desktop session and waits for its menu.</summary>
    public static ClientSession Open(ClientRunPlan plan, string output, ICollection<RunLog> logs, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(logs);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DesktopClientSession requires Windows.");
        if (!plan.Owned || plan.CopySource) throw new ArgumentException("Desktop launch needs a bound, owned disposable client plan.", nameof(plan));
        if (!NeedsDesktopTask(Process.GetCurrentProcess().SessionId))
            return ClientSession.Open(plan, output, logs, cancellation);
        plan.CheckOwnedInstall();
        var host = new LocalGameHost("this machine", HostShell.WindowsPowerShell);
        var quick = TimeSpan.FromSeconds(30);
        InteractiveClient.RequireWindowsDesktopAsync(host, cancellation).GetAwaiter().GetResult();
        HostInstall.RequirePortFreeAsync(host, plan.Port, quick, cancellation).GetAwaiter().GetResult();
        HostClientPreflight.CheckAsync(host, plan.Install, ClientPlatform.Windows, plan, quick, cancellation).GetAwaiter().GetResult();

        string launchDirectory = Path.Combine(output, "desktop-launch");
        string keptDirectory = Path.Combine(output, "desktop-client");
        string log = Path.Combine(plan.Install, HostedServerRun.BepInExLog);
        var moved = host.RunAsync(HostedClientScripts.MoveAside(host.Shell.Kind), new Dictionary<string, string>
        {
            ["log"] = log, ["to"] = Path.Combine(output, "previous-LogOutput.log"),
        }, quick, cancellation).GetAwaiter().GetResult().EnsureSuccess("Moving the client's previous BepInEx log aside");
        if (InteractiveClient.Line(moved.Stdout, "VT-MOVED") == null && InteractiveClient.Line(moved.Stdout, "VT-NONE") == null)
            throw new HostOperationException("Unexpected reply while moving the client's previous BepInEx log", moved);

        var launch = GameLaunch.ForClient(plan.Install, plan.LaunchArguments, plan.Environment, hostPlatform: ClientPlatform.Windows,
            secretVariables: plan.PasswordVariable is { } password ? [password] : null);
        var tunnel = host.OpenCliTunnelAsync(plan.Port, quick, cancellation: cancellation).GetAwaiter().GetResult();
        var keptLogs = new[]
        {
            new RunLog("client BepInEx log", Path.Combine(keptDirectory, "game-0.log"), Required: true),
            new RunLog("client Player.log", Path.Combine(keptDirectory, "game-1.log")),
        };
        try
        {
            var session = ClientSession.Launch(plan, output,
                () => new HostedClientProcess(InteractiveClient.StartAsync(host, launch, launchDirectory,
                    TimeSpan.FromSeconds(Math.Max(30, plan.StartSeconds)), cancellation: cancellation).GetAwaiter().GetResult(),
                    host, plan.Install, tunnel, keptDirectory),
                () => new CliTransport(tunnel.Address, tunnel.LocalPort),
                async (left, token) =>
                {
                    var clock = Stopwatch.StartNew();
                    var bepInEx = TimeSpan.FromSeconds(plan.BepInExSeconds);
                    (await host.WaitForLogAsync(log, 0, new Regex("^"), StartupEvents.StartupFailures,
                        bepInEx < left ? bepInEx : left, token).ConfigureAwait(false)).EnsureMatched();
                    (await host.WaitForLogAsync(log, 0, StartupEvents.CliListening, StartupEvents.StartupFailures,
                        left - clock.Elapsed, token).ConfigureAwait(false)).EnsureMatched();
                    using var states = StateWait.Connect(tunnel.Address, tunnel.LocalPort);
                    await states.WaitAsync([StateWait.MainMenu], left - clock.Elapsed, cancellation: token).ConfigureAwait(false);
                }, cancellation, null, keptLogs);
            foreach (var kept in session.Logs) logs.Add(kept);
            return session;
        }
        catch (Exception error)
        {
            foreach (var kept in ClientSession.KeptLogs(error)) logs.Add(kept);
            tunnel.Dispose();
            throw;
        }
    }
}
