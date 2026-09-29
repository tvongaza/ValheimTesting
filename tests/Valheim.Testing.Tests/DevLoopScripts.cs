using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

// Moved from ValheimCLI (commit ee4cd23, Tests/RequestBroker.Tests/ExampleScripts.cs and RepoPaths.cs) on
// 28 Sep 2026 with the scripts in tools/dev-loop. The repository root is now found by cli-dependency.json.

/// <summary>A test that runs the bash scripts in tools/ with <c>#!/bin/sh</c> fakes; reported as skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Exercises the bash scripts in tools/; runs on macOS/Linux";
    }
}

/// <summary>A test that runs the PowerShell scripts in tools/ with <c>.cmd</c> fakes; reported as skipped off Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Exercises the PowerShell scripts in tools/ under Windows PowerShell; runs on Windows";
    }
}

/// <summary>The output of one run of a dev-loop script.</summary>
internal sealed record ScriptRun(int ExitCode, string Stdout, string Stderr)
{
    public void AssertExit(int expected) =>
        Assert.True(expected == ExitCode, $"expected exit {expected}, got {ExitCode}\nstdout:\n{Stdout}\nstderr:\n{Stderr}");
}

/// <summary>Runs the real scripts in tools/dev-loop (or another tools/ folder) against inert fake tools in a temporary directory.</summary>
internal sealed class DevLoopScripts : IDisposable
{
    private readonly string _tools;

    public DevLoopScripts(string tools = "dev-loop")
    {
        _tools = tools;
        Root = Directory.CreateTempSubdirectory(tools + "-scripts-").FullName;
    }

    /// <summary>The temporary directory, removed on dispose.</summary>
    public string Root { get; }

    public string PathOf(string relative) => Path.Combine(new[] { Root }.Concat(relative.Split('/')).ToArray());

    public string Folder(string relative)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes an executable (0755) shell script.</summary>
    public string Tool(string relative, string script)
    {
        string path = PathOf(relative);
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                       UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    /// <summary>
    /// Writes a fake Windows tool <paramref name="name"/>: <c>bin/&lt;name&gt;.cmd</c>, which PowerShell finds on
    /// PATH through PATHEXT, runs <paramref name="script"/> (kept in <c>fakes/&lt;name&gt;.ps1</c>) in its own
    /// Windows PowerShell process and exits with its exit code. Returns the path of the .cmd.
    /// </summary>
    public string WindowsTool(string name, string script)
    {
        Folder("fakes");
        Folder("bin");
        File.WriteAllText(PathOf("fakes/" + name + ".ps1"), script);
        string launcher = PathOf("bin/" + name + ".cmd");
        File.WriteAllText(launcher,
            "@powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0..\\fakes\\" + name + ".ps1\" %*\r\n" +
            "@exit /b %ERRORLEVEL%\r\n");
        return launcher;
    }

    /// <summary>
    /// Runs <c>bash tools/&lt;folder&gt;/&lt;script&gt; args...</c>, or for a <c>.ps1</c> script
    /// <c>powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/&lt;folder&gt;/&lt;script&gt; args...</c>, with this
    /// process's environment plus <paramref name="environment"/>, where a null value removes the variable.
    /// </summary>
    public async Task<ScriptRun> Run(string script, IEnumerable<string> args, IReadOnlyDictionary<string, string?> environment)
    {
        bool powerShell = script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(powerShell ? "powershell.exe" : "bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Root,
        };
        if (powerShell)
        {
            foreach (string option in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" })
                start.ArgumentList.Add(option);
            // A PowerShell 7 module path (dotnet test started from pwsh) makes Windows PowerShell load 7's modules.
            start.Environment.Remove("PSModulePath");
        }
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "tools", _tools, script));
        foreach (string arg in args) start.ArgumentList.Add(arg);
        foreach ((string name, string? value) in environment)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException(start.FileName + " did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"tools/{_tools}/{script} did not finish within 60 s");
        }
        return new ScriptRun(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// The repository root, found from the test binaries so tests read the source tree on any OS: the nearest
    /// directory above them that holds cli-dependency.json, as scripts/*.cs find it.
    /// </summary>
    public static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
        throw new InvalidOperationException("No directory above " + AppContext.BaseDirectory + " contains cli-dependency.json");
    }

    /// <summary>PATH with <paramref name="directory"/> first, as the fake tools there must shadow real ones.</summary>
    public static string PathWith(string directory) =>
        directory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");

    /// <summary>The lines of a text, without the empty string after a final newline.</summary>
    public static string[] Lines(string text)
    {
        List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.ToArray();
    }

    public static string Md5Hex(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
