using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>The exact local client process joins the same run journal as its disposable world.</summary>
internal sealed class LocalClientJournal(ClientRunPlan plan, string evidence, bool desktopTask, string? expectedCommandLineForTest = null,
    Func<int, ProbedProcess>? processProbeForTest = null, ClientPlatform? hostPlatformForTest = null)
{
    private readonly string _launchDirectory = desktopTask ? Path.Combine(evidence, "desktop-launch") : evidence;
    private readonly RunJournal _journal = RunJournal.ThisProcess;
    private IOwnedProcess? _process;
    private int _pid;
    private string? _startIdentity;
    private bool _intended;
    private bool _started;
    internal IOwnedProcess? Process => _process;

    internal void Begin()
    {
        // Match ClientSession.StartInfo: on Unix the executable lives in the source install while
        // BepInEx and Doorstop live in the owned profile. Windows desktop tasks carry a secret
        // variable into their own session, so keep that host launch path there.
        string expected = expectedCommandLineForTest ?? (desktopTask
            ? GameLaunch.ForClient(plan.Install, plan.LaunchArguments, plan.Environment,
                hostPlatform: ClientPlatform.Windows, architecture: plan.LaunchArchitecture,
                secretVariables: plan.PasswordVariable is { } password ? [password] : null)
            : GameLaunch.LocalClient(plan.Install, plan.LaunchArguments, plan.Environment, plan.LaunchArchitecture,
                console: true, builtOn: hostPlatformForTest ?? GameLaunch.CurrentClientHost, loaderDirectory: plan.LoaderRoot)).CommandLineSha256();
        _journal.AppendLocal("client", JournalEntry.Of(JournalEntry.ProcessIntended,
            ("launchDirectory", _launchDirectory), ("expectedCommandLineSha256", expected)));
        _intended = true;
    }

    internal void Started(IOwnedProcess process)
    {
        _process = process;
        _pid = process.Id;
        // A launch interrupted between the start and this probe can still be found by its pid file.
        string pidFile = Path.Combine(_launchDirectory, "pid");
        if (!File.Exists(pidFile)) File.WriteAllText(pidFile, _pid.ToString(CultureInfo.InvariantCulture));
        var found = processProbeForTest != null ? processProbeForTest(_pid) : Probe(_pid);
        if (found.State != ProbedState.Same || found.StartIdentity == null || found.CommandLineSha256 == null)
            throw new InvalidOperationException($"Could not journal the owned client process {_pid} by ID, start time and command line ({found.State}); it will be stopped rather than held.");
        _startIdentity = found.StartIdentity;
        File.WriteAllText(pidFile, _pid.ToString(CultureInfo.InvariantCulture) + " " + _startIdentity);
        _journal.AppendLocal("client", JournalEntry.Of(JournalEntry.ProcessStarted,
            ("pid", _pid.ToString(CultureInfo.InvariantCulture)), ("startIdentity", _startIdentity),
            ("commandLineSha256", found.CommandLineSha256), ("launchDirectory", _launchDirectory)));
        _started = true;
    }

    private static ProbedProcess Probe(int pid)
    {
        var host = new LocalGameHost("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash);
        return HostProcessProbe.ProbeAsync(host, [(pid, "")], TimeSpan.FromSeconds(30), settle: true)
            .GetAwaiter().GetResult()[(pid, "")];
    }

    internal void Complete()
    {
        if (_process != null)
        {
            if (!_process.HasExited) throw new InvalidOperationException("The held client's stop could not be proved; it remains in the run journal for env status/recover.");
            if (_started)
                _journal.AppendLocal("client", JournalEntry.Of(JournalEntry.ProcessStopped,
                    ("pid", _pid.ToString(CultureInfo.InvariantCulture)), ("startIdentity", _startIdentity!)));
            else if (_intended)
                _journal.AppendLocal("client", JournalEntry.Of(JournalEntry.LaunchSettled,
                    ("actor", "client"), ("directory", _launchDirectory)));
        }
        // A failed launch with no process handle is ambiguous: recovery must inspect the pid file.
        if (_intended && _process == null)
            throw new InvalidOperationException("The client launch did not return a process after its intent was journalled; inspect env status before reusing this host.");
    }
}
