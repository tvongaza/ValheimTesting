using System.Text.Json;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using valheimCLI;
namespace Valheim.Testing.Game;

// A short-lived pointer to one owned game process on this machine. It is useful only while that exact process is alive;
// a separate valheim-test invocation checks the start identity again before sending a strictly pinned command. This is
// the one owner for client and server leases: their separate file names let one evidence directory hold both roles.
internal sealed record OwnedClientCommandLease(int Pid, string StartFileTimeUtc, int Port, string Install)
{
    internal const string ClientRole = "client", ServerRole = "server";
    internal const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal const string FileName = "owned-cli.json";
    internal const string MenuPins = "owned-cli-menu.pins";
    internal const string WorldPins = "owned-cli-world.pins";
    internal const string ServerFileName = "owned-cli-server.json";
    internal const string ServerMenuPins = "owned-cli-server-menu.pins";
    internal const string ServerWorldPins = "owned-cli-server-world.pins";
    public string Role { get; init; } = ClientRole;

    internal static string LeaseFile(string role) => role == ServerRole ? ServerFileName : FileName;
    internal static string PhasePins(string role, string phase) => (role, phase) switch
    {
        (ServerRole, "menu") => ServerMenuPins,
        (ServerRole, "world") => ServerWorldPins,
        (_, "menu") => MenuPins,
        _ => WorldPins,
    };

    // Process.StartTime is exact on Windows and macOS. On Linux, separate reads reconstruct it from a boot time and can
    // differ by sub-milliseconds, so derive the same UTC file time directly from the kernel's stable start ticks instead.
    // Both client and server lease writers, the separate command reader, and a hold's owner marker (read by finish from
    // another shell) use this one local identity rule. This overload requires the process to be running.
    internal static string LocalStartFileTimeUtc(int pid)
    {
        using var process = Process.GetProcessById(pid);
        if (process.HasExited) throw new InvalidOperationException($"Process {pid} has exited.");
        return LocalStartFileTimeUtc(process);
    }

    // The same identity for a process this runner already holds, running or exited: a launch records it even when the
    // process exits at once, and failure evidence still names an exited launch. A caller that needs it live checks itself.
    internal static string LocalStartFileTimeUtc(Process process)
    {
        if (!OperatingSystem.IsLinux())
            return process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);

        int pid = process.Id;
        string stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
        int commandEnd = stat.LastIndexOf(") ", StringComparison.Ordinal);
        string[] fields = commandEnd < 0 ? [] : stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks) || startTicks < 0)
            throw new InvalidDataException($"The start identity of process {pid} could not be read from /proc.");

        string? bootLine = File.ReadLines("/proc/stat").FirstOrDefault(line => line.StartsWith("btime ", StringComparison.Ordinal));
        if (bootLine == null || !long.TryParse(bootLine.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out long bootSeconds))
            throw new InvalidDataException("The Linux boot time could not be read from /proc/stat.");
        long ticksPerSecond = LinuxSysconf(2); // _SC_CLK_TCK on glibc Linux.
        if (ticksPerSecond <= 0) throw new InvalidOperationException("The Linux process clock-tick rate could not be read.");
        long fileTime = DateTimeOffset.FromUnixTimeSeconds(bootSeconds).UtcDateTime.ToFileTimeUtc() +
            checked(startTicks * TimeSpan.TicksPerSecond / ticksPerSecond);
        return fileTime.ToString(CultureInfo.InvariantCulture);
    }

    [DllImport("libc", EntryPoint = "sysconf", SetLastError = true)]
    private static extern long LinuxSysconf(int name);

    internal static void Write(string output, int pid, string startFileTimeUtc, ClientRunPlan plan)
    {
        Write(output, ClientRole, pid, startFileTimeUtc, plan.Port, plan.Install,
            plan.Pins.Select(pair => pair.Key + "=" + pair.Value).ToArray());
    }

    internal static void WriteServer(string output, int pid, string startFileTimeUtc, int port, string profile, ServerRunPlan plan)
    {
        Write(output, ServerRole, pid, startFileTimeUtc, port, profile,
            plan.Pins.Where(pair => !Expectations.IsWorldKey(pair.Key)).Select(pair => pair.Key + "=" + pair.Value).ToArray());
    }

    private static void Write(string output, string role, int pid, string startFileTimeUtc, int port, string install,
        IReadOnlyCollection<string> pluginPins)
    {
        LeaveWorld(output, role); // A new process has not verified a world, even when this is a restart in the same evidence.
        var lease = new OwnedClientCommandLease(pid, startFileTimeUtc, port, install) { Role = role };
        string path = Path.Combine(output, LeaseFile(role));
        string json = JsonSerializer.Serialize(lease, new JsonSerializerOptions { WriteIndented = true });
        if (OperatingSystem.IsWindows()) File.WriteAllText(path, json);
        else WritePrivateUnixFile(path, json);
        WritePins(Path.Combine(output, PhasePins(role, "menu")), pluginPins);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WritePrivateUnixFile(string path, string contents)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = PrivateMode,
            };
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                File.SetUnixFileMode(stream.SafeFileHandle, PrivateMode);
                writer.Write(contents);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // A joined client's world is learned from the server fixture, not HostWorld. Publish this file only
    // after the client has verified that exact world; a concurrent diagnostic must never read half a pin set.
    internal static void WriteVerifiedWorld(string output, ClientRunPlan plan, string worldUid)
    {
        WriteVerifiedWorld(output, ClientRole, plan.Pins, worldUid);
    }

    internal static void WriteVerifiedServerWorld(string output, ServerRunPlan plan, string worldUid)
    {
        WriteVerifiedWorld(output, ServerRole, plan.Pins.Where(pair => !Expectations.IsWorldKey(pair.Key)), worldUid);
    }

    private static void WriteVerifiedWorld(string output, string role, IEnumerable<KeyValuePair<string, string>> pluginPins, string worldUid)
    {
        if (!File.Exists(Path.Combine(output, LeaseFile(role)))) return;
        string path = Path.Combine(output, PhasePins(role, "world"));
        string[] pins = [.. pluginPins.Select(pair => pair.Key + "=" + pair.Value), "worlduid=" + worldUid];
        if (!OperatingSystem.IsWindows())
        {
            WritePins(path, pins);
            return;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, pins);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WritePins(string path, IReadOnlyCollection<string> pins)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllLines(path, pins);
            return;
        }
        WritePrivateUnixFile(path, string.Join(Environment.NewLine, pins) + Environment.NewLine);
    }

    internal static void LeaveWorld(string output) => LeaveWorld(output, ClientRole);

    private static void LeaveWorld(string output, string role) => File.Delete(Path.Combine(output, PhasePins(role, "world")));

    // Only the owned run's evidence files are retired. Command records remain as evidence.
    internal static void Retire(string output)
    {
        Retire(output, ClientRole);
    }

    internal static void RetireServer(string output) => Retire(output, ServerRole);

    private static void Retire(string output, string role)
    {
        LeaveWorld(output, role);
        File.Delete(Path.Combine(output, PhasePins(role, "menu")));
        File.Delete(Path.Combine(output, LeaseFile(role)));
    }
}
