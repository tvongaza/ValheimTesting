using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>One strictly pinned, recorded command to the exact client a concurrent one-shot run owns.</summary>
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
        if (OperatingSystem.IsLinux())
        { error.WriteLine("REFUSED: Linux has no owned client; held dedicated-server passthrough waits for #788 part 2."); return 3; }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        { error.WriteLine("REFUSED: this owned-client passthrough supports Windows and macOS clients."); return 3; }
        try
        {
            string evidence = Path.GetFullPath(values["--evidence"]);
            string leaseFile = Path.Combine(evidence, OwnedClientCommandLease.FileName);
            string leaseJson = OperatingSystem.IsMacOS() ? ReadPrivateMacLease(leaseFile) : File.ReadAllText(leaseFile);
            var lease = JsonSerializer.Deserialize<OwnedClientCommandLease>(leaseJson)
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
            var recording = new RecordingTransport(new CliTransport("127.0.0.1", lease.Port), commandLog);
            using var actor = new GameActor("owned diagnostic client", recording)
            { CommandTimeout = TimeSpan.FromSeconds(values.TryGetValue("--timeout-seconds", out string? limit) ? int.Parse(limit, CultureInfo.InvariantCulture) : 5) };
            actor.VerifyEnvironment(expected);
            if (ScreenshotSupersize(values["--command"]) is { } supersize)
            {
                var capture = OwnedScreenshot.Capture(actor, evidence, values["--command"], supersize,
                    TimeSpan.FromSeconds(15));
                recording.RecordEvidence(new { kind = "owned-screenshot", capture.Status, capture.Complete,
                    capture.RelativeImage, capture.Sha256, capture.ResultFile });
                output.WriteLine("Private screenshot evidence: " + capture.ResultFile);
                output.WriteLine("Private command evidence: " + commandLog);
                if (capture.Complete) return 0;
                error.WriteLine("REFUSED: " + capture.Status);
                return 1;
            }
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

    // Only the built-in screenshot is redirected into private evidence. The one argument it takes is its supersize, 1..4;
    // a name is refused, since the owned runner chooses the destination and a caller's name would be silently dropped.
    internal static int? ScreenshotSupersize(string command)
    {
        string[] parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "cli_screenshot") return null;
        if (parts.Length == 1) return 1;
        if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size is >= 1 and <= 4)
            return size;
        throw new ArgumentException("cli_screenshot takes only a supersize, 1..4 (cli_screenshot 2); it takes no name, because the owned runner " +
            "chooses the destination and writes the image to private run evidence.");
    }

    internal static bool IsExactProcess(Process process, OwnedClientCommandLease lease) =>
        process.Id == lease.Pid && !process.HasExited && string.Equals(
            process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), lease.StartFileTimeUtc, StringComparison.Ordinal);

    // Exact process identity alone is insufficient if a different game has taken over the recorded CLI port.
    // Each supported OS has one owner proof behind this method.
    internal static void RequirePortOwner(int port, int pid)
    {
        IEnumerable<int> owners;
        if (OperatingSystem.IsWindows())
        {
            // Query the listener table directly: starting PowerShell and Get-NetTCPConnection for each command
            // made this proof depend on an unrelated shell's cold-start time.
            owners = new[] { AfInet, AfInet6 }.SelectMany(family => WindowsListenerOwners(family, port));
        }
        else if (OperatingSystem.IsMacOS()) owners = MacListenerOwners(port);
        else throw new PlatformNotSupportedException("Linux port-owner proof waits for #788 part 2.");

        RequireOnlyPortOwner(owners, pid);
    }

    internal static void RequireOnlyPortOwner(IEnumerable<int> owners, int pid)
    {
        bool found = false;
        foreach (int owner in owners)
        {
            found = true;
            if (owner != pid)
                throw new InvalidOperationException("The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.");
        }
        if (!found)
            throw new InvalidOperationException("The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.");
    }

    private const string PrivateLeaseMessage = "The owned client identity file must be a regular file owned by the current user with mode 0600.";
    private const int MacOpenNonBlock = 0x00000004;
    private const int MacOpenNoFollow = 0x00000100;
    private const int MacOpenCloseOnExec = 0x01000000;
    private const ushort MacFileTypeMask = 0xf000;
    private const ushort MacRegularFile = 0x8000;

    // Open first with O_NOFOLLOW, then validate the descriptor. The later JSON read therefore cannot be redirected
    // through a symlink or switched to a different path after validation.
    private static string ReadPrivateMacLease(string path)
    {
        int descriptor = MacOpen(path, MacOpenNonBlock | MacOpenNoFollow | MacOpenCloseOnExec);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 2) throw new FileNotFoundException("Could not find the owned client identity file: " + path, path);
            throw new InvalidOperationException(PrivateLeaseMessage, new System.ComponentModel.Win32Exception(error));
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (MacFStatCurrent(descriptor, out MacFileStatus status) != 0)
                throw new InvalidOperationException("Could not inspect the owned client identity file.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            RequirePrivateLease(status.Mode, status.Owner, MacEffectiveUserId());
            using var stream = new FileStream(handle, FileAccess.Read);
            handle = null!; // FileStream owns the descriptor from here.
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        finally { handle?.Dispose(); }
    }

    internal static void RequirePrivateLease(ushort rawMode, uint owner, uint currentUser)
    {
        var permissions = (UnixFileMode)(rawMode & 0x0fff);
        if ((rawMode & MacFileTypeMask) != MacRegularFile || owner != currentUser || permissions != OwnedClientCommandLease.PrivateMode)
            throw new InvalidOperationException(PrivateLeaseMessage);
    }

    // Darwin's public stat layout is shared by arm64 and x64. Only the stable prefix through st_uid is read.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacFileStatus
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(16)] public uint Owner;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFStat(int descriptor, out MacFileStatus status);

    [DllImport("libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacFStatInode64(int descriptor, out MacFileStatus status);

    // On x64 Darwin, plain fstat is the legacy-inode ABI with a different layout. arm64 exposes only
    // the current symbol, whose layout is the one represented by MacFileStatus.
    private static int MacFStatCurrent(int descriptor, out MacFileStatus status) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MacFStatInode64(descriptor, out status)
            : MacFStat(descriptor, out status);

    [DllImport("libSystem.B.dylib", EntryPoint = "geteuid")]
    private static extern uint MacEffectiveUserId();

    // netstat reads the kernel TCP table rather than one user's descriptors, so it can prove that no listener
    // in either address family belongs to another account. Keep the process bounded like every host observation.
    private static IEnumerable<int> MacListenerOwners(int port)
    {
        var start = new ProcessStartInfo("/usr/sbin/netstat")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-anv", "-p", "tcp" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start macOS netstat for the TCP listener proof.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(1000);
            throw new TimeoutException("macOS netstat did not finish the TCP listener proof within 5 seconds.");
        }
        string output = stdout.GetAwaiter().GetResult();
        string diagnostic = stderr.GetAwaiter().GetResult().Trim();
        return MacListenerOwners(output, diagnostic, process.ExitCode, port);
    }

    internal static IReadOnlyCollection<int> MacListenerOwners(string output, string diagnostic, int exitCode, int port)
    {
        if (exitCode != 0)
            throw new InvalidOperationException($"Could not read the macOS TCP listeners (netstat exited {exitCode}{(diagnostic.Length == 0 ? "" : ": " + diagnostic)}).");
        var owners = new HashSet<int>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match listener = MacListenerRow.Match(line);
            if (!listener.Success) continue;
            ReadOnlySpan<char> endpoint = listener.Groups["local"].ValueSpan;
            int separator = endpoint.LastIndexOf('.');
            if (separator < 0 || !int.TryParse(endpoint[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int rowPort) || rowPort != port)
                continue;
            Match owner = MacListenerOwner.Match(listener.Groups["tail"].Value);
            if (!owner.Success || !int.TryParse(owner.Groups["pid"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                throw new InvalidOperationException("Could not read a process ID from macOS netstat's TCP listener result.");
            owners.Add(pid);
        }
        return owners;
    }

    private static readonly Regex MacListenerRow = new(
        @"^\s*tcp(?:4|6|46)\s+\d+\s+\d+\s+(?<local>\S+)\s+\S+\s+LISTEN\s+(?<tail>.*)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex MacListenerOwner = new(
        @"^\d+\s+\d+\s+\d+\s+\d+\s+.*:(?<pid>\d+)\s+[0-9a-fA-F]{5}\s",
        RegexOptions.CultureInvariant);

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaxTableBytes = 16 * 1024 * 1024;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool ordered,
        int addressFamily, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, OwnerPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScope, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScope, RemotePort, State, OwnerPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpTableOwnerPid { public uint Count; public TcpRowOwnerPid First; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6TableOwnerPid { public uint Count; public Tcp6RowOwnerPid First; }

    private static IEnumerable<int> WindowsListenerOwners(int family, int port)
    {
        uint size = 0;
        uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (status != ErrorInsufficientBuffer || size < sizeof(uint) || size > MaxTableBytes)
            throw new InvalidOperationException($"Could not read the Windows TCP listener table (error {status}).");

        // The table can grow between the size query and the read. Retry with the new size, never without a bound.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr table = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                uint available = size;
                status = GetExtendedTcpTable(table, ref available, false, family, TcpTableOwnerPidListener, 0);
                if (status == ErrorInsufficientBuffer && available > size && available <= MaxTableBytes)
                { size = available; continue; }
                if (status != 0)
                    throw new InvalidOperationException($"Could not read the Windows TCP listener table (error {status}).");

                // Windows permits alignment padding before and between rows. Use the marshalled native
                // layouts for both the first-row offset and stride instead of assuming a packed table.
                int firstRow = family == AfInet
                    ? Marshal.OffsetOf<TcpTableOwnerPid>(nameof(TcpTableOwnerPid.First)).ToInt32()
                    : Marshal.OffsetOf<Tcp6TableOwnerPid>(nameof(Tcp6TableOwnerPid.First)).ToInt32();
                int rowBytes = family == AfInet ? Marshal.SizeOf<TcpRowOwnerPid>() : Marshal.SizeOf<Tcp6RowOwnerPid>();
                int portOffset = family == AfInet
                    ? Marshal.OffsetOf<TcpRowOwnerPid>(nameof(TcpRowOwnerPid.LocalPort)).ToInt32()
                    : Marshal.OffsetOf<Tcp6RowOwnerPid>(nameof(Tcp6RowOwnerPid.LocalPort)).ToInt32();
                int pidOffset = family == AfInet
                    ? Marshal.OffsetOf<TcpRowOwnerPid>(nameof(TcpRowOwnerPid.OwnerPid)).ToInt32()
                    : Marshal.OffsetOf<Tcp6RowOwnerPid>(nameof(Tcp6RowOwnerPid.OwnerPid)).ToInt32();
                uint count = unchecked((uint)Marshal.ReadInt32(table));
                if (available < firstRow || count > (available - firstRow) / rowBytes)
                    throw new InvalidOperationException("The Windows TCP listener table has an invalid size.");
                var owners = new List<int>();
                for (int row = 0; row < count; row++)
                {
                    int offset = firstRow + row * rowBytes;
                    int localPort = Marshal.ReadByte(table, offset + portOffset) << 8 |
                                    Marshal.ReadByte(table, offset + portOffset + 1);
                    if (localPort == port) owners.Add(Marshal.ReadInt32(table, offset + pidOffset));
                }
                return owners;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new InvalidOperationException("The Windows TCP listener table changed too often to verify its owner.");
    }
}
