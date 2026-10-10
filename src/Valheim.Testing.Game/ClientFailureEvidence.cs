using System.Diagnostics;
using System.Text.Json;

namespace Valheim.Testing.Game;

// Private failure evidence. It never probes a PID until the launch's exact start identity still matches.
internal static class ClientFailureEvidence
{
    internal static void CaptureLocal(IOwnedProcess owned, GameActor? actor, ClientRunPlan plan, string output)
    {
        Directory.CreateDirectory(output);
        var facts = new Dictionary<string, object?>
        {
            ["pid"] = owned.Id,
            ["capturedUtc"] = DateTime.UtcNow,
            ["screenshot"] = "not attempted: strict pin verification has not completed; CLI responsiveness unknown",
        };
        if (owned is not IClientProcessIdentity identity)
            facts["process"] = "not inspected: no exact launch identity was supplied";
        else
            try
            {
                using var process = Process.GetProcessById(owned.Id);
                string actual = process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(actual, identity.StartFileTimeUtc, StringComparison.Ordinal))
                    facts["process"] = "not inspected: PID now belongs to a different launch";
                else
                {
                    facts["process"] = process.HasExited ? "exited" : "same owned process";
                    facts["sessionId"] = Try(() => process.SessionId);
                    facts["windowHandle"] = Try(() => process.MainWindowHandle.ToInt64());
                    facts["windowTitle"] = Try(() => process.MainWindowTitle);
                    facts["responding"] = Try(() => process.Responding);
                    if (!process.HasExited)
                    {
                        if (actor?.Pinned == true) facts["screenshot"] = Screenshot(actor, output);
                        else BootstrapStatus(plan, facts);
                    }
                }
            }
            catch (ArgumentException) { facts["process"] = "exited before diagnosis"; }
            catch (InvalidOperationException) { facts["process"] = "exited before diagnosis"; }
            catch (System.ComponentModel.Win32Exception) { facts["process"] = "process facts unavailable to this runner"; }

        Tail(Path.Combine(plan.LoaderRoot, "BepInEx", "LogOutput.log"), Path.Combine(output, "client-failure-bepinex-tail.log"), facts, "bepInExTail");
        ClientPlatform platform = OperatingSystem.IsWindows() ? ClientPlatform.Windows : OperatingSystem.IsMacOS() ? ClientPlatform.MacOS : ClientPlatform.Linux;
        Tail(ClientSession.PlayerLog(platform), Path.Combine(output, "client-failure-player-tail.log"), facts, "playerTail");
        File.WriteAllText(Path.Combine(output, "client-failure-diagnostic.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
        string place = facts.TryGetValue("sessionId", out object? session) ? $"session {session}" : facts["process"]?.ToString() ?? "unknown process";
        string window = facts.TryGetValue("windowHandle", out object? handle) && Convert.ToInt64(handle) != 0 ? "window present" : "no window";
        Console.Error.WriteLine($"Client diagnosis before teardown: {window} ({place}); screenshot: {facts["screenshot"]}. Private evidence: client-failure-diagnostic.json");
    }

    private static object? Try<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static void BootstrapStatus(ClientRunPlan plan, Dictionary<string, object?> facts)
    {
        try
        {
            using var transport = new CliTransport(plan.Host, plan.Port);
            var status = transport.ReadStatus(); // Independent read-only socket; no unpinned game command is sent.
            facts["cliStatus"] = status;
            facts["screenshot"] = "not attempted: strict pins are unavailable before menu readiness; STATUS answered but a screenshot cannot be authorized yet";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or FormatException or TimeoutException)
        {
            facts["cliStatus"] = "unavailable: " + error.Message;
            facts["screenshot"] = "CLI did not answer the separate STATUS probe; screenshot not attempted before strict pin verification";
        }
    }

    private static void Tail(string source, string destination, Dictionary<string, object?> facts, string key)
    {
        try
        {
            using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Startup logs can be very large. Diagnosis has to finish before teardown, even on a noisy run.
            const int maxBytes = 256 * 1024;
            long start = Math.Max(0, file.Length - maxBytes);
            file.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            if (start > 0) reader.ReadLine(); // The bounded window may begin partway through a line.
            var last = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (last.Count == 80) last.Dequeue();
                last.Enqueue(line);
            }
            File.WriteAllLines(destination, last);
            facts[key] = Path.GetFileName(destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { facts[key] = "unavailable: " + error.GetType().Name; }
    }

    private static string Screenshot(GameActor actor, string output)
    {
        string name = "vt-failure-" + Guid.NewGuid().ToString("N");
        TimeSpan previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = TimeSpan.FromSeconds(3);
            var reply = actor.Execute("cli_screenshot " + name, requireAccepted: false);
            if (!reply.Accepted) return "CLI refused or did not answer within 3 s: " + (reply.Refusal ?? reply.ErrorCode ?? reply.Message ?? "no accepted reply");
            string? line = reply.Line("OK: screenshot queued path=");
            if (line == null) return "CLI replied but did not name a screenshot path";
            string path = line["OK: screenshot queued path=".Length..].Split(" size=", 2, StringSplitOptions.None)[0];
            if (!Path.IsPathRooted(path)) return "CLI named a non-absolute screenshot path";
            string destination = Path.Combine(output, "client-failure-screenshot.png");
            if (!File.Exists(path))
            {
                string? directory = Path.GetDirectoryName(path);
                if (directory == null || !Directory.Exists(directory)) return "screenshot queued but its directory did not appear before teardown";
                using var watcher = new FileSystemWatcher(directory, Path.GetFileName(path)) { EnableRaisingEvents = true };
                if (!File.Exists(path)) watcher.WaitForChanged(WatcherChangeTypes.Created | WatcherChangeTypes.Changed, 2000);
            }
            if (!File.Exists(path)) return "screenshot queued but CLI did not write it within 2 s";
            File.Copy(path, destination);
            return "captured: " + Path.GetFileName(destination);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        { return "CLI unresponsive or screenshot unavailable within the bounded attempt: " + error.Message; }
        finally { actor.CommandTimeout = previous; }
    }
}
