using System.Diagnostics;
using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>How an owned process ended when it was stopped.</summary>
public enum StopOutcome
{
    /// <summary>It quit by itself after it was asked to, within the wait: the game's own shutdown ran (save, lobby retirement).</summary>
    Clean,
    /// <summary>It did not quit within the wait (or could not be asked) and was killed: nothing after the last confirmed save was kept.</summary>
    Killed,
    /// <summary>It had exited before the stop.</summary>
    AlreadyExited,
}

/// <summary>
/// How an owned game process was asked to quit. The game quits through Unity's <c>OnApplicationQuit</c>: it saves the
/// profile and, on a server, the world, then retires its PlayFab lobby.
/// </summary>
public enum QuitRequest
{
    /// <summary>
    /// SIGINT on Linux and macOS; on Windows a Ctrl+C sent to the process's own console, which a dedicated server started by
    /// <see cref="ServerLaunch.CreateStartInfo"/> has (a windowless one, so a Ctrl+C in the runner's console never reaches it).
    /// </summary>
    Interrupt,
    /// <summary>Windows: close the main window, as the window's close button does. Linux and macOS: SIGTERM.</summary>
    CloseWindow,
    /// <summary>Never asked: the stop kills at once.</summary>
    None,
}

/// <summary>What stopping an owned process did.</summary>
/// <param name="Outcome">Whether it quit cleanly, was killed or had already exited.</param>
/// <param name="ExitCode">Its exit code, when known.</param>
/// <param name="Elapsed">From the stop request to the exit.</param>
/// <param name="Request">How it was asked to quit, and anything that went wrong asking.</param>
public sealed record ProcessStop(StopOutcome Outcome, int? ExitCode, TimeSpan Elapsed, string Request)
{
    /// <summary>For example <c>clean after 8.2 s (interrupt, exit 0)</c> or <c>killed after 120.0 s (interrupt; no exit within 120 s)</c>.</summary>
    public override string ToString()
    {
        string seconds = Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        string exit = ExitCode is { } code ? ", exit " + code.ToString(CultureInfo.InvariantCulture) : "";
        return Outcome switch
        {
            StopOutcome.Clean => $"clean after {seconds} ({Request}{exit})",
            StopOutcome.Killed => $"killed after {seconds} ({Request}{exit})",
            _ => $"already exited ({Request}{exit})",
        };
    }
}

/// <summary>Asks a local process to quit. Only the process given is ever signalled.</summary>
internal static class ProcessQuit
{
    /// <summary>Sends the request; returns what was done, and false when the process could not be asked.</summary>
    internal static (bool Sent, string Description) Request(Process process, QuitRequest request)
    {
        if (request == QuitRequest.None) return (false, "not asked to quit");
        if (!OperatingSystem.IsWindows())
        {
            // The shell's kill builtin: present wherever sh is, including minimal containers without procps.
            string name = request == QuitRequest.Interrupt ? "INT" : "TERM";
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string argument in new[] { "-c", "kill -s \"$1\" \"$2\"", "sh", name, process.Id.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            try
            {
                using var kill = Process.Start(start) ?? throw new IOException("/bin/sh did not start");
                string error = kill.StandardError.ReadToEnd();
                kill.WaitForExit();
                return kill.ExitCode == 0 ? (true, "SIG" + name) : (false, $"SIG{name} failed: {error.Trim()}");
            }
            catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception) { return (false, $"SIG{name} could not be sent: {failure.Message}"); }
        }
        if (request == QuitRequest.CloseWindow)
        {
            try { return process.CloseMainWindow() ? (true, "window closed") : (false, "no main window to close"); }
            catch (InvalidOperationException) { return (false, "exited before the window could be closed"); }
        }
        return WindowsCtrlC(process.Id);
    }

    // Ctrl+C reaches every process attached to a console, so a separate PowerShell process detaches from the runner's
    // console, attaches to the target's own and ignores the event itself. The target must have a console of its own:
    // ServerLaunch starts the dedicated server with CreateNoWindow for that.
    internal const string WindowsCtrlCScript = """
        $ErrorActionPreference = 'Stop'
        Add-Type -Namespace ValheimTesting -Name ConsoleControl -MemberDefinition @'
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(System.IntPtr handler, bool add);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
        '@
        [void][ValheimTesting.ConsoleControl]::FreeConsole()
        if (-not [ValheimTesting.ConsoleControl]::AttachConsole([uint32]$env:VT_QUIT_PID)) { exit 3 }
        [void][ValheimTesting.ConsoleControl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
        if (-not [ValheimTesting.ConsoleControl]::GenerateConsoleCtrlEvent(0, 0)) { exit 4 }
        exit 0
        """;

    private static (bool, string) WindowsCtrlC(int pid)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", WindowsCtrlCScript }) start.ArgumentList.Add(argument);
        start.Environment["VT_QUIT_PID"] = pid.ToString(CultureInfo.InvariantCulture);
        try
        {
            using var helper = Process.Start(start) ?? throw new IOException("powershell.exe did not start");
            var output = helper.StandardOutput.ReadToEndAsync();
            var error = helper.StandardError.ReadToEndAsync();
            if (!helper.WaitForExit(30_000)) { helper.Kill(); return (false, "Ctrl+C helper did not finish within 30 s"); }
            return helper.ExitCode switch
            {
                0 => (true, "Ctrl+C"),
                3 => (false, "Ctrl+C: could not attach to the process's console (it has none of its own)"),
                4 => (false, "Ctrl+C: GenerateConsoleCtrlEvent failed"),
                int code => (false, $"Ctrl+C helper exited {code}: {(error.Result + output.Result).Trim()}"),
            };
        }
        catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception)
        {
            return (false, "Ctrl+C helper could not run: " + failure.Message);
        }
    }
}
