using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>One strictly pinned, recorded command to the exact Windows client a concurrent one-shot run owns.</summary>
internal static class OwnedCliCommand
{
    internal const string Usage = "valheim-test cli --evidence RUN_EVIDENCE --phase menu|world --command TEXT [--timeout-seconds 1..15] " +
        "(or --expect-strict FILE in place of --phase)";

    internal static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args.Length % 2 != 0) { error.WriteLine("Usage: " + Usage); return 2; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--evidence" or "--phase" or "--expect-strict" or "--command" or "--timeout-seconds") ||
                !values.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
            { error.WriteLine("Usage: " + Usage); return 2; }
        }
        if (!values.ContainsKey("--evidence") || !values.ContainsKey("--command") ||
            values.ContainsKey("--phase") == values.ContainsKey("--expect-strict") ||
            values.GetValueOrDefault("--phase") is { } phase && phase is not ("menu" or "world") ||
            values["--command"].IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            values.TryGetValue("--timeout-seconds", out string? timeoutText) &&
                (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 15))
        { error.WriteLine("Usage: " + Usage); return 2; }
        if (!OperatingSystem.IsWindows())
        { error.WriteLine("REFUSED: this owned-client passthrough runs beside a Windows desktop client; use the pinned GameActor in a Mac or Linux test."); return 3; }
        try
        {
            string evidence = Path.GetFullPath(values["--evidence"]);
            string leaseFile = Path.Combine(evidence, OwnedClientCommandLease.FileName);
            var lease = JsonSerializer.Deserialize<OwnedClientCommandLease>(File.ReadAllText(leaseFile))
                ?? throw new InvalidDataException("The owned client identity file is empty.");
            if (lease.Pid <= 0 || lease.Port is < 1 or > 65535 || lease.StartFileTimeUtc.Length == 0 ||
                !Path.IsPathFullyQualified(lease.Install))
                throw new InvalidDataException("The owned client identity file is incomplete.");
            using var process = Process.GetProcessById(lease.Pid);
            if (!IsExactProcess(process, lease))
                throw new InvalidOperationException("The exact client this run started is no longer running; a reused PID is never contacted.");
            RequirePortOwner(lease.Port, lease.Pid);
            string pins = values.TryGetValue("--expect-strict", out string? supplied) ? supplied :
                Path.Combine(evidence, values["--phase"] == "menu" ? OwnedClientCommandLease.MenuPins : OwnedClientCommandLease.WorldPins);
            string expected = StrictExpectations.Load(pins);
            // A separate invocation is outside the runner's connection log. Open its own create-new record before
            // any pin check or game command, so a mutating diagnostic cannot succeed without private evidence.
            string commandLog = Path.Combine(evidence, "owned-cli-command-" + Guid.NewGuid().ToString("N") + ".jsonl");
            using var actor = new GameActor("owned diagnostic client",
                new RecordingTransport(new CliTransport("127.0.0.1", lease.Port), commandLog))
            { CommandTimeout = TimeSpan.FromSeconds(values.TryGetValue("--timeout-seconds", out string? limit) ? int.Parse(limit, CultureInfo.InvariantCulture) : 5) };
            actor.VerifyEnvironment(expected);
            var reply = actor.Execute(values["--command"], requireAccepted: false); // One command, never retried.
            foreach (string line in reply.Output) output.WriteLine(line);
            output.WriteLine("Private command evidence: " + commandLog);
            if (reply.Accepted) return 0;
            error.WriteLine("REFUSED: ValheimCLI completed but did not accept the command: " + (reply.Refusal ?? reply.ErrorCode ?? reply.Message ?? "no accepted reply"));
            return 1;
        }
        catch (Exception failure) when (failure is IOException or ArgumentException or InvalidOperationException or JsonException or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { error.WriteLine("REFUSED: " + failure.Message); return 3; }
    }

    internal static bool IsExactProcess(Process process, OwnedClientCommandLease lease) =>
        process.Id == lease.Pid && !process.HasExited && string.Equals(
            process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), lease.StartFileTimeUtc, StringComparison.Ordinal);

    // Exact process identity alone is insufficient if a different game has taken over the recorded CLI port.
    internal static void RequirePortOwner(int port, int pid)
    {
        using var query = new Process { StartInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-Command", $"Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess" },
        } };
        if (!query.Start() || !query.WaitForExit(8000))
        {
            if (!query.HasExited) query.Kill();
            throw new InvalidOperationException("Could not prove which process owns the recorded ValheimCLI port within 8 seconds.");
        }
        string owners = query.StandardOutput.ReadToEnd();
        if (query.ExitCode != 0 || !OnlyOwner(owners, pid))
            throw new InvalidOperationException("The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.");
    }

    internal static bool OnlyOwner(string output, int pid)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int owner) && owner == pid);
    }
}
