using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

// Boots one owned dedicated server with BepInEx through ServerLaunch, waits for bounded startup evidence,
// stops exactly that process, then boots it again on the saved world. No ValheimCLI, pins or gameplay: it
// establishes that the runtime starts, loads BepInEx, generates a new world, saves it at a clean stop and loads
// that save on the next boot, on this host (Windows, Linux or macOS: the runtime's own server).
if (args.Length is < 2 or > 4)
{
    Console.Error.WriteLine("Usage: LinuxServerSmoke <disposable-server-runtime> <new-output-directory> [seconds 30..1800, default 600] [port, default 2456]\n" +
        "Starts and stops one owned dedicated server twice. Use a disposable runtime copy: BepInEx writes its log and config into it.");
    return 2;
}
string runtime = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (!int.TryParse(args.Length > 2 ? args[2] : "600", NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 30 or > 1800 ||
    !int.TryParse(args.Length > 3 ? args[3] : "2456", NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1024 or > 65533)
{
    Console.Error.WriteLine("Seconds must be 30..1800 and the port 1024..65533 (the server also uses the next two ports).");
    return 2;
}
if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("Use a new output directory for each run."); return 2; }
Directory.CreateDirectory(output);

using var cancellation = new CancellationTokenSource();
// Ctrl+C or a container stop still stops the owned server in the finally block below.
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
var report = new ScenarioReport("linux-server-smoke");
var clock = Stopwatch.StartNew();
var deadline = TimeSpan.FromSeconds(seconds);
string bepinexLog = Path.Combine(runtime, "BepInEx", "LogOutput.log");
string world = "Smoke" + Guid.NewGuid().ToString("N")[..8];
string saves = Path.Combine(output, "savedir");
// Private, unlisted, and a throwaway password that is never printed or written by this tool.
string password = Guid.NewGuid().ToString("N");
DirectServerProcess? server = null;
try
{
    report.Step(StepPhase.Setup, "prepare launch from the runtime's own platform", () =>
    {
        // A previous run's log would satisfy the chainloader check without this boot.
        if (File.Exists(bepinexLog)) throw new InvalidOperationException("BepInEx/LogOutput.log already exists; use a fresh runtime copy.");
        report.Provenance["platform"] = ServerLaunch.Detect(runtime).ToString();
        report.Provenance["world"] = world;
        report.Provenance["steamBuildId"] = SteamBuildId(runtime);
        Directory.CreateDirectory(saves);
        Start(0); // Builds (and so checks) the launch without starting anything.
    });
    // Observed on Valheim 1.0 for a new world: world creation, then the server socket after location generation.
    Boot(1, "server created and loaded the generated world", "Get create world " + world, "Opened Steam server");
    report.Step("the clean stop saved the world", () => Require(Log(1), "World save (5/5) done"));
    // The same world and save folder: the game loads the save instead of creating a world ("save number" counts saves).
    Boot(2, "server loaded the saved world", "ZNet.LoadWorld: " + world + " (" + world + "), save number", "Opened Steam server");
}
catch (Exception error) { Console.Error.WriteLine(error.Message); }
finally
{
    try { Stop(); } catch (Exception error) { Console.Error.WriteLine(error.Message); } // A boot a failed step left running.
    // Informational only: counts for the reviewer, and whether Steam's backend answered in time.
    report.Provenance["bepinexErrorLines"] = Count(bepinexLog, "[Error");
    report.Provenance["bepinexWarningLines"] = Count(bepinexLog, "[Warning");
    report.Provenance["steamBackendConnected"] = (Read(Log(1))?.Contains("Game server connected", StringComparison.Ordinal) ?? false).ToString();
    report.Write(output);
}
Console.WriteLine((report.Passed ? "PASS" : "FAIL") + $": {report.Steps.Count(x => x.Passed)}/{report.Steps.Count} steps; see {output}");
return report.Passed ? 0 : 1;

string Log(int boot) => Path.Combine(output, boot == 1 ? "server.log" : $"server-{boot}.log");
ProcessStartInfo Start(int boot) => ServerLaunch.CreateStartInfo(runtime, ["-batchmode", "-nographics", "-name", world, "-world", world,
    "-port", port.ToString(CultureInfo.InvariantCulture), "-password", password, "-public", "0", "-savedir", saves, "-logFile", Log(Math.Max(boot, 1))]);
// One owned boot: start, BepInEx's chainloader (the runtime's log starts afresh each boot), the world evidence, a clean stop.
void Boot(int boot, string worldStep, params string[] markers)
{
    string suffix = boot == 1 ? "" : $" (boot {boot})";
    report.Step(StepPhase.Setup, "start owned server process" + suffix, () =>
    {
        // The previous boot's log, already kept as boot-N.game-0.log, would pass the chainloader check before this boot
        // truncates it.
        if (boot > 1) File.Delete(bepinexLog);
        server = new DirectServerProcess(Start(boot), Path.Combine(output, $"boot-{boot}"), bepinexLog);
        report.Provenance[boot == 1 ? "pid" : $"pid{boot}"] = server.Id.ToString(CultureInfo.InvariantCulture);
    });
    report.Step("BepInEx chainloader finished" + suffix, () => WaitFor(bepinexLog, "Chainloader startup complete"));
    report.Step(worldStep, () => WaitFor(Log(boot), markers));
    report.Step(StepPhase.Cleanup, "stop owned server process cleanly" + suffix, () =>
    {
        var stop = Stop()!;
        report.Provenance[$"stop{boot}"] = stop.ToString();
        if (stop.Outcome != StopOutcome.Clean) throw new InvalidOperationException("The server did not quit when asked: " + stop);
    });
}
// Asks the server to quit (SIGINT; Ctrl+Break over Windows SSH, Ctrl+C on other Windows hosts); killed only after 60 s.
ProcessStop? Stop()
{
    if (server == null) return null;
    try { return server.StopCleanly(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)); }
    finally { server.Dispose(); server = null; }
}
static void Require(string path, string marker)
{
    if (!(Read(path)?.Contains(marker, StringComparison.Ordinal) ?? false)) throw new InvalidOperationException($"'{marker}' is not in {Path.GetFileName(path)}.");
}

// Poll read-only log files; require each marker after the previous one. Fails on exit, cancellation or deadline.
void WaitFor(string path, params string[] markers)
{
    while (true)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        string? text = Read(path);
        int at = 0;
        foreach (string marker in markers)
        {
            if (text == null || at < 0) break;
            at = text.IndexOf(marker, at, StringComparison.Ordinal);
            if (at >= 0) at += marker.Length;
        }
        if (text != null && at >= 0) return;
        if (server!.HasExited) throw new InvalidOperationException($"Server exited before '{markers[^1]}' appeared in {Path.GetFileName(path)}; see its boot's stderr log.");
        if (clock.Elapsed >= deadline) throw new TimeoutException($"'{string.Join("' then '", markers)}' not in {Path.GetFileName(path)} within {seconds} s.");
        cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
    }
}
static string? Read(string path)
{
    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
}
static string Count(string path, string prefix) => Read(path) is { } text
    ? text.Split('\n').Count(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture) : "absent";
static string SteamBuildId(string runtime)
{
    string manifest = Path.Combine(runtime, "steamapps", "appmanifest_896660.acf");
    var match = File.Exists(manifest) ? Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"") : Match.Empty;
    return match.Success ? match.Groups[1].Value : "unknown";
}
