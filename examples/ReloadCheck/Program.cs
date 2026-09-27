using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Valheim.Testing.Game;
using valheim_cli.Testing;

// Attach to an already owned disposable game. This example never starts/stops it.
// ScriptEngine must watch ONLY this probe; the CLI core remains in plugins/.
if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: ReloadCheck <port> <probe-A.dll> <probe-B.dll> <empty-scripts-dir> <report.json>");
    return 2;
}
var evidence = new List<object>();
var scripts = Path.GetFullPath(args[3]);
var deployed = Path.Combine(scripts, "ReloadProbe.dll");
var report = Path.GetFullPath(args[4]);
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
bool passed = false;
bool ownsProbe = false;
try
{
    if (!Directory.Exists(scripts) || Directory.EnumerateFiles(scripts, "*.dll", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException("Use an empty, dedicated ScriptEngine directory: reload affects every script.");
    ownsProbe = true;
    int port = int.Parse(args[0]);
    using var control = new CliTransport("127.0.0.1", port);
    CommandResult Run(string command)
    {
        var result = control.Execute(command, TimeSpan.FromSeconds(15));
        evidence.Add(new { command, result.Ok, result.ErrorCode, result.Output });
        if (!result.Ok) throw new InvalidOperationException(command + ": " + result.ErrorCode);
        return result;
    }
    var originalBuild = Run("cli_build").Output.ToArray();
    Run("cli_extensions");
    JsonElement? Probe()
    {
        using var doc = GameActor.ParseLine(Run("cli_extensions"), "EXTENSIONS ");
        foreach (var item in doc.RootElement.GetProperty("extensions").EnumerateArray())
            if (item.GetProperty("id").GetString() == "example.probe" && !item.GetProperty("closing").GetBoolean()) return item.Clone();
        return null;
    }
    async Task<JsonElement> AwaitRevision(string revision)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            var probe = Probe();
            if (probe is { } found && found.GetProperty("version").GetString() == revision) return found;
            await Task.Delay(500);
        }
        throw new TimeoutException("Probe revision did not register: " + revision);
    }
    JsonElement Hello()
    {
        using var doc = GameActor.ParseLine(Run("cli_extension example.probe/hello"), "EXTENSION_RESULT ");
        return doc.RootElement.Clone();
    }
    void Install(string source)
    {
        File.Copy(source, deployed + ".incoming", true);
        File.Move(deployed + ".incoming", deployed, true);
        evidence.Add(new { installed = Path.GetFileName(source), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(deployed))) });
    }
    void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        evidence.Add(new { check = description, passed = true });
    }
    Install(args[1]);
    var first = await AwaitRevision("0.1.0");
    string firstInstance = first.GetProperty("instance").GetString()!;
    var helloA = Hello();
    Require(helloA.GetProperty("instance").GetString() == firstInstance && helloA.GetProperty("data").GetProperty("leases").GetInt32() == 1, "A owns exactly one live resource");
    Run("cli_extension example.probe/removed");
    using var waitingConnection = new CliTransport("127.0.0.1", port);
    var pending = Task.Run(() => waitingConnection.Execute("cli_extension example.probe/wait", TimeSpan.FromSeconds(90)));
    var pendingTimer = Stopwatch.StartNew();
    while (Hello().GetProperty("data").GetProperty("waiting").GetInt32() != 1)
    {
        if (pendingTimer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Probe wait never started");
        await Task.Delay(200);
    }
    Install(args[2]);
    var second = await AwaitRevision("0.2.0");
    Require(second.GetProperty("instance").GetString() != firstInstance, "B has a fresh registration identity");
    var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(20));
    evidence.Add(new { cancelled.Ok, cancelled.ErrorCode, cancelled.Output });
    using (var doc = GameActor.ParseLine(cancelled, "EXTENSION_RESULT "))
        Require(!cancelled.Ok && doc.RootElement.GetProperty("code").GetString() == "extension_unloaded" && doc.RootElement.GetProperty("instance").GetString() == firstInstance,
            "A's active command returns extension_unloaded with A's identity");
    var helloB = Hello();
    Require(helloB.GetProperty("data").GetProperty("revision").GetString() == "0.2.0" && helloB.GetProperty("data").GetProperty("leases").GetInt32() == 1,
        "B answers on the existing connection and A's resource is gone");
    var removed = control.Execute("cli_extension example.probe/removed", TimeSpan.FromSeconds(15));
    evidence.Add(new { removed.Ok, removed.ErrorCode, removed.Output });
    using (var doc = GameActor.ParseLine(removed, "EXTENSION_RESULT "))
        Require(!removed.Ok && doc.RootElement.GetProperty("code").GetString() == "no_extension_command", "Removed command cannot invoke A's code");
    Require(Run("cli_build").Output.SequenceEqual(originalBuild), "CLI core identity and load time are unchanged");
    File.Delete(deployed);
    var removalTimer = Stopwatch.StartNew();
    while (Probe() != null)
    {
        if (removalTimer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Deleted probe remains registered");
        await Task.Delay(500);
    }
    Require(Run("cli_build").Output.SequenceEqual(originalBuild), "CLI still answers after complete extension removal");
    passed = true;
    Console.WriteLine("PASS: A -> B reload, active cancellation, resource cleanup, removed command, stable core and connection, final unregister.");
}
catch (Exception error)
{
    evidence.Add(new { failure = error.ToString() });
    Console.Error.WriteLine(error.Message);
}
finally
{
    // Remove only the file this run installed, never any other script or process.
    if (ownsProbe && File.Exists(deployed)) File.Delete(deployed);
    File.WriteAllText(report, JsonSerializer.Serialize(new { passed, evidence }, new JsonSerializerOptions { WriteIndented = true }));
}
return passed ? 0 : 1;
