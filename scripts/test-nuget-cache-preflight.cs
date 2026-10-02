// Run after bootstrap-cli.cs and validate.cs; checks their cache-only mode without restoring packages.
//   dotnet run scripts/test-nuget-cache-preflight.cs
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

string root = FindRoot();
string test = Path.Combine(Path.GetTempPath(), "valheimtesting-cache-test-" + Guid.NewGuid().ToString("N"));
string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12].ToLowerInvariant();
string writable = Path.Combine(Path.GetTempPath(), "valheimtesting-cache-preflight-tests", key);
Directory.CreateDirectory(test);
try
{
    string blocked = Path.Combine(test, "blocked-file");
    File.WriteAllText(blocked, "not a directory");
    string packages = Path.Combine(writable, "packages");
    string http = Path.Combine(writable, "http");
    Directory.CreateDirectory(packages);
    Directory.CreateDirectory(http);

    foreach (string script in new[] { "bootstrap-cli.cs", "validate.cs" })
    {
        // Compile the current file-based script before giving its child an intentionally invalid NuGet path.
        // With no assets file, dotnet run itself would fail before the script's preflight could execute.
        string normal = Run(script, packages, http, noRestore: false);
        if (!normal.Contains("NuGet caches writable") || !normal.Contains("packages=" + packages) || !normal.Contains("HTTP=" + http))
            throw new InvalidOperationException(script + " did not keep the writable caches: " + normal);
        string fallback = Run(script, blocked, blocked, noRestore: true);
        if (!fallback.Contains("NuGet caches not writable") || !fallback.Contains("using packages=") || !fallback.Contains("HTTP="))
            throw new InvalidOperationException(script + " did not select both fallback caches: " + fallback);
    }
    // A file-based script may restore before its C# entry point. Copy it to a fresh path so
    // no runfile assets exist, then prove the supported launcher handles a blocked cache first.
    string fresh = Path.Combine(test, "fresh checkout");
    Directory.CreateDirectory(Path.Combine(fresh, "scripts"));
    File.WriteAllText(Path.Combine(fresh, "cli-dependency.json"), "{}");
    foreach (string name in new[] { "bootstrap-cli.cs", "validate.cs", OperatingSystem.IsWindows() ? "run.ps1" : "run.sh" })
        File.Copy(Path.Combine(root, "scripts", name), Path.Combine(fresh, "scripts", name));
    foreach (string task in new[] { "bootstrap", "validate" })
    {
        string output = RunLauncher(fresh, task, blocked, blocked);
        if (!output.Contains("NuGet caches blocked before dotnet run") || !output.Contains("NuGet caches writable"))
            throw new InvalidOperationException(task + " launcher did not protect first restore: " + output);
        string explicitCache = RunLauncher(fresh, task, packages, http);
        if (!explicitCache.Contains("NuGet caches writable before dotnet run: packages=" + packages) ||
            !explicitCache.Contains("HTTP=" + http))
            throw new InvalidOperationException(task + " launcher did not preserve writable explicit caches: " + explicitCache);
    }
    if (Directory.EnumerateFiles(packages, ".valheimtesting-write-*", SearchOption.AllDirectories).Any() ||
        Directory.EnumerateFiles(http, ".valheimtesting-write-*", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException("The write probe left a file in a NuGet cache.");
    Console.WriteLine("NuGet cache preflight passed for both scripts.");
}
finally
{
    Directory.Delete(test, recursive: true);
}
return 0;

string RunLauncher(string fresh, string task, string packages, string http)
{
    var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh" : "bash")
    {
        WorkingDirectory = fresh, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    info.ArgumentList.Add(Path.Combine(fresh, "scripts", OperatingSystem.IsWindows() ? "run.ps1" : "run.sh"));
    info.ArgumentList.Add(task);
    info.ArgumentList.Add("--cache-preflight-only");
    info.Environment["NUGET_PACKAGES"] = packages;
    info.Environment["NUGET_HTTP_CACHE_PATH"] = http;
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start launcher");
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(task + " launcher failed: " + error + output);
    return output;
}

string Run(string script, string packages, string http, bool noRestore)
{
    if (!noRestore)
    {
        // Rebuild the file-based app's assets against a stable test cache. Those assets can
        // otherwise reference packages in a temporary directory deleted by an earlier run.
        var restore = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "restore", Path.Combine(root, "scripts", script), "--force" }) restore.ArgumentList.Add(argument);
        restore.Environment["NUGET_PACKAGES"] = packages;
        restore.Environment["NUGET_HTTP_CACHE_PATH"] = http;
        using Process restored = Process.Start(restore) ?? throw new InvalidOperationException("Could not restore " + script);
        string restoreOutput = restored.StandardOutput.ReadToEnd();
        string restoreError = restored.StandardError.ReadToEnd();
        restored.WaitForExit();
        if (restored.ExitCode != 0) throw new InvalidOperationException(script + " test setup restore failed: " + restoreError + restoreOutput);
    }
    var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    var arguments = new List<string> { "run", Path.Combine(root, "scripts", script) };
    arguments.Add("--no-restore");
    arguments.AddRange(new[] { "--", "--cache-preflight-only" });
    foreach (string argument in arguments)
        info.ArgumentList.Add(argument);
    info.Environment["NUGET_PACKAGES"] = packages;
    info.Environment["NUGET_HTTP_CACHE_PATH"] = http;
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + script);
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(script + " cache preflight failed: " + error + output);
    return output;
}

static string FindRoot([CallerFilePath] string source = "")
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(source) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}
