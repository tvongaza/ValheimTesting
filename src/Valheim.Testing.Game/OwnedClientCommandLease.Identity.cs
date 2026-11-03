#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
namespace Valheim.Testing.Game;

internal sealed partial record OwnedClientCommandLease
{
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

    // Linux requires a readable kernel entry; an exited/reaped process may no longer have one. No StartTime fallback.
    internal static long LocalStartUtcTicks(Process process) =>
        DateTime.FromFileTimeUtc(long.Parse(LocalStartFileTimeUtc(process), NumberStyles.None, CultureInfo.InvariantCulture)).Ticks;
}
