// Starts pwsh many times in parallel, the way a local game host starts its PowerShell wrapper, and counts startup crashes
// (#133: parallel pwsh starts corrupt PowerShell's shared startup JIT profile; PowerShell/PowerShell#26528,
// dotnet/runtime#121977). Run it without the guard to see whether upstream fixed the race (#145), with it to check the guard.
// Never launches Valheim.
//
//   dotnet run scripts/pwsh-startup-stress.cs -- [--starts 3000] [--parallel 8] [--guard] [--out <dir>]
//
// Exit 0: no start crashed. Exit 1: at least one did; each failure's exit code and stderr is written to --out (default: a new
// temporary directory). On GitHub's runners without the guard (1 October 2026, pwsh 7.6.5/7.6.6 on .NET 10), 13 of 12,000 starts
// crashed; with it, 0 of 6,000.
using System.Diagnostics;
using System.Text;

int starts = 3000, parallel = 8; bool guard = false; string? output = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--starts": starts = int.Parse(args[++i]); break;
        case "--parallel": parallel = int.Parse(args[++i]); break;
        case "--guard": guard = true; break;
        case "--out": output = args[++i]; break;
        default: Console.Error.WriteLine($"Unknown argument {args[i]}."); return 2;
    }
}
output ??= Directory.CreateTempSubdirectory("pwsh-startup-stress-").FullName;
Directory.CreateDirectory(output);
// Loads an assembly by name, as the reported crashes did, and reports its end as the host wrapper does.
string script = Convert.ToBase64String(Encoding.Unicode.GetBytes(
    "$null = [System.Collections.Concurrent.ConcurrentDictionary[string,int]]::new(); [Console]::Error.WriteLine('[vt-exit] 0'); exit 0"));
int failures = 0;
var clock = Stopwatch.StartNew();
Parallel.For(0, starts, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
{
    var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    // The value local and container hosts give their pwsh wrapper (ScriptedGameHost.NoStartupJitProfile).
    if (guard) start.Environment["DOTNET_MultiCoreJitMinNumCpus"] = "FFFF";
    foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", script }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    process.StandardInput.Close();
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    process.WaitForExit();
    string errors = stderr.Result; _ = stdout.Result;
    if (process.ExitCode == 0 && errors.Contains("[vt-exit] 0", StringComparison.Ordinal)) return;
    Interlocked.Increment(ref failures);
    File.WriteAllText(Path.Combine(output, $"failure-{i}.txt"), $"exit {process.ExitCode}\n{errors}");
});
Console.WriteLine($"pwsh {(guard ? "with" : "without")} the guard: {starts} starts, {parallel} at a time, {failures} crashed in {clock.Elapsed.TotalSeconds:0} s" +
    (failures == 0 ? "." : $"; see {output}"));
return failures == 0 ? 0 : 1;
