// Run after bootstrap-cli.cs and validate.cs; checks their cache-only mode without restoring packages.
//   dotnet run scripts/test-nuget-cache-preflight.cs
using System.Diagnostics;
using System.Runtime.CompilerServices;

string root = FindRoot();
string test = Path.Combine(Path.GetTempPath(), "valheimtesting-cache-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(test);
try
{
    string blocked = Path.Combine(test, "blocked-file");
    File.WriteAllText(blocked, "not a directory");
    string packages = Path.Combine(test, "packages");
    string http = Path.Combine(test, "http");
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
    if (Directory.EnumerateFiles(packages).Any() || Directory.EnumerateFiles(http).Any())
        throw new InvalidOperationException("The write probe left a file in a NuGet cache.");
    Console.WriteLine("NuGet cache preflight passed for both scripts.");
}
finally
{
    Directory.Delete(test, recursive: true);
}
return 0;

string Run(string script, string packages, string http, bool noRestore)
{
    var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    var arguments = new List<string> { "run", Path.Combine(root, "scripts", script) };
    if (noRestore) arguments.Add("--no-restore");
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

static string FindRoot([CallerFilePath] string source = "") =>
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
