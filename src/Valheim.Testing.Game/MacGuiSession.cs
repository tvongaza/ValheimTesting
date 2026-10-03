using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>Proves this runner is in the unlocked console user's Aqua session before it launches a local GUI client.</summary>
internal static class MacGuiSession
{
    internal static void Require()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("A local macOS GUI client needs a macOS runner.");
        string Run(string file, params string[] arguments)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("The macOS GUI-session probe timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException("The macOS GUI-session probe failed: " + file);
            Task.WaitAll(stdout, stderr);
            return stdout.Result;
        }
        RequireEvidence(Environment.UserName, Run("/bin/launchctl", "managername"), Run("/usr/bin/stat", "-f", "%Su", "/dev/console"),
            Run("/usr/sbin/ioreg", "-n", "Root", "-d1"));
    }

    internal static void RequireEvidence(string user, string manager, string consoleUser, string registry)
    {
        if (manager.Trim() != "Aqua" || consoleUser.Trim() != user)
            throw new InvalidOperationException("The runner is not in the logged-in macOS console user's Aqua session; launch it from that unlocked desktop, not SSH or a service.");
        bool active = Regex.Matches(registry, @"\{[^{}]*\}").Select(match => match.Value).Any(entry =>
            entry.Contains("\"kCGSSessionUserNameKey\"=\"" + user + "\"", StringComparison.Ordinal) &&
            entry.Contains("\"kCGSSessionOnConsoleKey\"=Yes", StringComparison.Ordinal) &&
            entry.Contains("\"kCGSessionLoginDoneKey\"=Yes", StringComparison.Ordinal) &&
            !entry.Contains("\"CGSSessionScreenIsLocked\"=Yes", StringComparison.Ordinal));
        if (!active) throw new InvalidOperationException("The macOS console session is locked or unavailable; unlock the desktop before starting an owned client.");
    }
}
