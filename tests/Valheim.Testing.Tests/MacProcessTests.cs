using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// The one macOS process identity and stop (#665), through the real bash and BSD ps of a Mac. Every process signalled here is a
// short-lived child this test started itself (a sleep with SIGINT at its default, as the launchers start the game); nothing
// else on the machine is ever touched, and every file is under the test's own temporary folder.
[Trait("Os", "macos")]
public sealed class MacProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-mac-process-").FullName;
    private readonly List<Process> _children = [];
    private static readonly LocalGameHost Host = new("mac-process", HostShell.Bash);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public void Dispose()
    {
        foreach (var child in _children)
        {
            try { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } } catch (InvalidOperationException) { }
            child.Dispose();
        }
        Directory.Delete(_root, recursive: true);
    }

    // A child that sleeps with SIGINT at its default disposition whatever this test host ignores, so a quit request ends it. Its
    // start identity is returned once perl has executed the sleep in its place (so its command line is the sleep's).
    private async Task<(Process Child, string Start)> ChildAsync(string executable = "/bin/sleep")
    {
        var start = new ProcessStartInfo("/usr/bin/perl") { UseShellExecute = false };
        foreach (string argument in new[] { "-e", "$SIG{INT} = 'DEFAULT'; exec @ARGV or die", executable, "30" }) start.ArgumentList.Add(argument);
        var child = Process.Start(start)!;
        _children.Add(child);
        // The launchers' own read: vt_mac_identity.
        var result = (await Host.RunAsync("set -u\n" + MacProcess.Functions + """
            i=0
            while [ "$i" -lt 200 ]; do
              case "$(vt_mac_command "$game")" in "$exe "*) break ;; esac
              sleep 0.05; i=$((i + 1))
            done
            vt_mac_identity "$game"
            """, new Dictionary<string, string> { ["game"] = child.Id.ToString(CultureInfo.InvariantCulture), ["exe"] = executable }, Timeout))
            .EnsureSuccess("reading a child's identity");
        Assert.Matches("^[0-9]+$", result.Stdout.Trim());
        return (child, result.Stdout.Trim());
    }

    private static Dictionary<string, string> Stop(int pid, string start, string quit, string seconds = "5", string dir = "/nonexistent/vt-mac-process") => new()
    {
        ["game"] = pid.ToString(CultureInfo.InvariantCulture), ["start"] = start, ["quit"] = quit, ["seconds"] = seconds, ["dir"] = dir,
    };

    // Exactly one VT-STOP line, whose word is returned; and the GUI agent's verdict (null when its line is absent), which follows it.
    private static (string Verdict, string? Agent) Verdicts(HostResult result)
    {
        Assert.True(result.Succeeded, result.Describe());
        var stops = Regex.Matches(result.Stdout, "^VT-STOP (.*)$", RegexOptions.Multiline);
        Assert.True(stops.Count == 1, "Expected exactly one VT-STOP verdict: " + result.Stdout);
        var agents = Regex.Matches(result.Stdout, "^VT-AGENT (.*)$", RegexOptions.Multiline);
        Assert.True(agents.Count <= 1, "Expected at most one VT-AGENT verdict: " + result.Stdout);
        if (agents.Count == 1) Assert.True(agents[0].Index > stops[0].Index, "The agent is retired after the stop's verdict: " + result.Stdout);
        return (stops[0].Groups[1].Value.TrimEnd('\r'), agents.Count == 1 ? agents[0].Groups[1].Value.TrimEnd('\r') : null);
    }

    [Fact]
    public async Task EveryStopPathPrintsExactlyOneVerdict()
    {
        if (!OperatingSystem.IsMacOS()) return;
        foreach (string script in new[] { MacProcess.Stop, InteractiveScripts.MacStop })
        {
            bool client = ReferenceEquals(script, InteractiveScripts.MacStop);

            // Asked to quit: the game exits by itself. The client's launch directory is missing, so its agent is gone with it.
            var (quits, quitsStart) = await ChildAsync();
            var quit = Verdicts(await Host.RunAsync(script, Stop(quits.Id, quitsStart, quit: "10"), Timeout));
            Assert.Equal(("quit", client ? "removed" : null), quit);
            Assert.True(quits.WaitForExit(5000));

            // Not asked (quit 0): killed.
            var (killed, killedStart) = await ChildAsync();
            Assert.Equal(("stopped", client ? "removed" : null),
                Verdicts(await Host.RunAsync(script, Stop(killed.Id, killedStart, quit: "0"), Timeout)));
            Assert.True(killed.WaitForExit(5000));

            // Gone already, and a start identity of another process with that ID: neither is touched.
            Assert.Equal(("gone", client ? "removed" : null), Verdicts(await Host.RunAsync(script, Stop(killed.Id, "1", quit: "10"), Timeout)));
            var (other, start) = await ChildAsync();
            Assert.Equal(("gone", client ? "removed" : null), Verdicts(await Host.RunAsync(script, Stop(other.Id, "1", quit: "10"), Timeout)));
            Assert.False(other.HasExited);

            // Signals that never arrive: still running after both budgets, and the client keeps its agent.
            Assert.Equal(("running", null), Verdicts(await Host.RunAsync("kill() { return 0; }\n" + script, Stop(other.Id, start, quit: "1", seconds: "1"), Timeout)));
            Assert.False(other.HasExited);

            // A process ps cannot read is unproven, never signalled, and keeps its agent.
            Assert.Equal(("unproven", null), Verdicts(await Host.RunAsync("ps() { return 1; }\n" + script, Stop(other.Id, start, quit: "10"), Timeout)));
            Assert.False(other.HasExited);

            // So is one whose command line is not the journalled one (recovery's third fact).
            var variables = Stop(other.Id, start, quit: "10");
            variables["commandLine"] = new string('0', 64);
            Assert.Equal(("unproven", null), Verdicts(await Host.RunAsync(script, variables, Timeout)));
            Assert.False(other.HasExited);
        }
    }

    // The probe, the launchers and the stop read one identity: what the probe reports is what the stop matches.
    [Fact]
    public async Task TheProbeAndTheStopReadTheSameIdentity()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (child, start) = await ChildAsync();
        var probed = (await HostProcessProbe.ProbeAsync(Host, [(child.Id, "")], Timeout, settle: true))[(child.Id, "")];
        Assert.Equal(ProbedState.Same, probed.State);
        Assert.Equal(start, probed.StartIdentity);
        Assert.Equal(HostProcessProbe.ExpectedMacCommandLineSha256("/bin/sleep", ["30"]), probed.CommandLineSha256);
        Assert.Equal(ProbedState.Reused, (await HostProcessProbe.ProbeAsync(Host, [(child.Id, "1")], Timeout))[(child.Id, "1")].State);
        var variables = Stop(child.Id, start, quit: "0");
        variables["commandLine"] = probed.CommandLineSha256!;
        Assert.Equal(("stopped", null), Verdicts(await Host.RunAsync(MacProcess.Stop, variables, Timeout)));
        Assert.True(child.WaitForExit(5000));
        Assert.Equal(ProbedState.Gone, (await HostProcessProbe.ProbeAsync(Host, [(child.Id, start)], Timeout))[(child.Id, start)].State);
    }

    // The server's supervisor reads its own game's identity, which stays a zombie until the supervisor reaps it: a game that
    // exited at once still has one, so its boot reports the exit code, while every stop and wait already sees it gone.
    [Fact]
    public async Task AGameThatExitedAtOnceStillHasTheIdentityItsSupervisorRecords()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var result = (await Host.RunAsync("set -u\n" + MacProcess.Functions + """
            /usr/bin/perl -e '
              my $child = fork(); if (!$child) { exit 0 }
              select undef, undef, undef, 0.5;
              print "$child\n"; $| = 1;
              system("/bin/bash", "-c", "$ARGV[0]\nvt_mac_started $child && vt_mac_state $child", "vt");
              waitpid($child, 0);
            ' "$(declare -f vt_mac_started vt_mac_state vt_mac_observe)"
            """, null, Timeout)).EnsureSuccess("reading a zombie");
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Matches("^[0-9]+$", lines[1]);
        Assert.Equal("gone", lines[2]);
    }

    // A game in a runtime is found however either path is spelled: $TMPDIR is /var/folders, which is /private/var/folders once
    // resolved, and a runtime may be reached through a link (#700's note on #665).
    [Fact]
    public async Task TheGameProcessCheckResolvesTheRuntimeAndTheGamesPath()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string real = Path.Combine(_root, "real"), link = Path.Combine(_root, "link");
        string game = Path.Combine(real, "valheim_server", "Valheim");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        File.CreateSymbolicLink(game, "/bin/sleep");
        Directory.CreateSymbolicLink(link, real);
        Assert.StartsWith("/var/", _root);
        string resolved = "/private" + real;
        Assert.True(Directory.Exists(resolved));

        var (viaReal, _) = await ChildAsync(game);
        foreach (string root in new[] { real, resolved, link })
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostedRuntimeStage.RequireStoppedAsync(Host, Timeout, runtimes: [root], clientSession: false));
            Assert.Contains($"process {viaReal.Id}", refused.Message);
        }
        viaReal.Kill(); Assert.True(viaReal.WaitForExit(5000));

        var (viaLink, _) = await ChildAsync(Path.Combine(link, "valheim_server", "Valheim"));
        foreach (string root in new[] { real, resolved })
            Assert.Contains($"process {viaLink.Id}",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    HostedRuntimeStage.RequireStoppedAsync(Host, Timeout, runtimes: [root], clientSession: false))).Message);
        // Another runtime is not this game's.
        await HostedRuntimeStage.RequireStoppedAsync(Host, Timeout, runtimes: [Path.Combine(_root, "elsewhere")], clientSession: false);
        viaLink.Kill(); Assert.True(viaLink.WaitForExit(5000));

        // A game in a folder this user cannot enter (another user's) is compared as ps wrote its path: not this runtime's, and
        // found in its own.
        string locked = Path.Combine(_root, "locked");
        string hidden = Path.Combine(locked, "valheim_server", "Valheim");
        Directory.CreateDirectory(Path.GetDirectoryName(hidden)!);
        File.CreateSymbolicLink(hidden, "/bin/sleep");
        var (unreadable, _) = await ChildAsync(hidden);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            await HostedRuntimeStage.RequireStoppedAsync(Host, Timeout, runtimes: [Path.Combine(_root, "elsewhere")], clientSession: false);
            Assert.Contains($"process {unreadable.Id}", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HostedRuntimeStage.RequireStoppedAsync(Host, Timeout, runtimes: [locked], clientSession: false))).Message);
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    // The field case on #665: a check's cleanup removed a Mac server's folder while the server still ran from it. With a server
    // still running there, the cleanup refuses and the folder stays.
    [Fact]
    public async Task CleanupWithTheServerStillRunningKeepsItsFolder()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string? kept = null;
        Process? server = null;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => GameHostChecks.WithRootAsync(Host, _root, async root =>
        {
            kept = root;
            string game = Path.Combine(root, "install", "valheim_server", "Valheim");
            Directory.CreateDirectory(Path.GetDirectoryName(game)!);
            File.CreateSymbolicLink(game, "/bin/sleep");
            (server, _) = await ChildAsync(game);
        }, serversRunFromIt: true));
        Assert.Contains($"process {server!.Id}", refused.Message);
        Assert.True(Directory.Exists(kept));
        Assert.False(server.HasExited);

        // Once it is stopped, the same cleanup removes the folder.
        server.Kill(); Assert.True(server.WaitForExit(5000));
        string? removed = null;
        await GameHostChecks.WithRootAsync(Host, _root, root =>
        {
            removed = root;
            Directory.CreateDirectory(Path.Combine(root, "install"));
            return Task.CompletedTask;
        }, serversRunFromIt: true);
        Assert.False(Directory.Exists(removed));
    }
}
