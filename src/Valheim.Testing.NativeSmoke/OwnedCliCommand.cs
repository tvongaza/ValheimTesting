using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>One strictly pinned, recorded command to the exact local client or server a concurrent run owns.</summary>
internal static class OwnedCliCommand
{
    internal const string Usage = "valheim-test cli --evidence RUN_EVIDENCE [--role client|server] --phase menu|world --command TEXT [--timeout-seconds 1..15] " +
        "(or --expect-strict FILE in place of --phase)";

    internal static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args.Length % 2 != 0) { error.WriteLine("Usage: " + Usage); return 2; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--evidence" or "--role" or "--phase" or "--expect-strict" or "--command" or "--timeout-seconds") ||
                !values.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
            { error.WriteLine("Usage: " + Usage); return 2; }
        }
        if (!values.ContainsKey("--evidence") || !values.ContainsKey("--command") ||
            values.ContainsKey("--phase") == values.ContainsKey("--expect-strict") ||
            values.GetValueOrDefault("--role") is { } roleValue && roleValue is not (OwnedClientCommandLease.ClientRole or OwnedClientCommandLease.ServerRole) ||
            values.GetValueOrDefault("--phase") is { } phase && phase is not ("menu" or "world") ||
            values["--command"].IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            values.TryGetValue("--timeout-seconds", out string? timeoutText) &&
                (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 15))
        { error.WriteLine("Usage: " + Usage); return 2; }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        { error.WriteLine("REFUSED: this owned-game passthrough supports Windows, macOS and Linux."); return 3; }
        try
        {
            string evidence = Path.GetFullPath(values["--evidence"]);
            string role = values.TryGetValue("--role", out string? chosen) ? chosen : DefaultRole(evidence);
            string leaseFile = Path.Combine(evidence, OwnedClientCommandLease.LeaseFile(role));
            string leaseJson = OperatingSystem.IsWindows() ? File.ReadAllText(leaseFile) : ReadPrivateUnixLease(leaseFile, role);
            var lease = JsonSerializer.Deserialize<OwnedClientCommandLease>(leaseJson)
                ?? throw new InvalidDataException($"The owned {role} identity file is empty.");
            if (lease.Pid <= 0 || lease.Port is < 1 or > 65535 || lease.StartFileTimeUtc.Length == 0 ||
                !Path.IsPathFullyQualified(lease.Install) || lease.Role != role)
                throw new InvalidDataException($"The owned {role} identity file is incomplete or names another role.");
            using var process = Process.GetProcessById(lease.Pid);
            if (!IsExactProcess(process, lease))
                throw new InvalidOperationException($"The exact {role} this run started is no longer running; a reused PID is never contacted.");
            RequirePortOwner(lease.Port, lease.Pid, role);
            string pins = values.TryGetValue("--expect-strict", out string? supplied) ? supplied :
                Path.Combine(evidence, OwnedClientCommandLease.PhasePins(role, values["--phase"]));
            string expected = StrictExpectations.Load(pins);
            // A separate invocation is outside the runner's connection log. Open its own create-new record before
            // any pin check or game command, so a mutating diagnostic cannot succeed without private evidence.
            string commandLog = Path.Combine(evidence, "owned-cli-command-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var recording = new RecordingTransport(new CliTransport("127.0.0.1", lease.Port), commandLog);
            using var actor = new GameActor("owned diagnostic " + role, recording)
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
        catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
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

    // Without --role: the one role whose lease this evidence holds; a server-only hold needs no --role. Evidence holding
    // both is ambiguous and refused; evidence holding neither reads the client lease, whose absence is the refusal.
    internal static string DefaultRole(string evidence)
    {
        bool client = File.Exists(Path.Combine(evidence, OwnedClientCommandLease.FileName));
        bool server = File.Exists(Path.Combine(evidence, OwnedClientCommandLease.ServerFileName));
        if (client && server)
            throw new InvalidOperationException("This evidence holds both an owned client and server; choose --role client or --role server.");
        return server ? OwnedClientCommandLease.ServerRole : OwnedClientCommandLease.ClientRole;
    }

    internal static bool IsExactProcess(Process process, OwnedClientCommandLease lease) =>
        process.Id == lease.Pid && !process.HasExited && string.Equals(
            OwnedClientCommandLease.LocalStartFileTimeUtc(process.Id), lease.StartFileTimeUtc, StringComparison.Ordinal);

    // Exact process identity alone is insufficient if a different game has taken over the recorded CLI port.
    // Each supported OS has one owner proof behind this method.
    internal static void RequirePortOwner(int port, int pid, string role = OwnedClientCommandLease.ClientRole)
    {
        IEnumerable<int> owners;
        if (OperatingSystem.IsWindows())
        {
            // Query the listener table directly: starting PowerShell and Get-NetTCPConnection for each command
            // made this proof depend on an unrelated shell's cold-start time.
            owners = new[] { AfInet, AfInet6 }.SelectMany(family => WindowsListenerOwners(family, port));
        }
        else if (OperatingSystem.IsMacOS()) owners = MacListenerOwners(port);
        else if (OperatingSystem.IsLinux()) owners = LinuxListenerOwners(port, pid);
        else throw new PlatformNotSupportedException("TCP listener ownership is supported on Windows, macOS and Linux.");

        RequireOnlyPortOwner(owners, pid, role);
    }

    internal static void RequireOnlyPortOwner(IEnumerable<int> owners, int pid, string role = OwnedClientCommandLease.ClientRole)
    {
        bool found = false;
        foreach (int owner in owners)
        {
            found = true;
            if (owner != pid)
                throw new InvalidOperationException($"The recorded ValheimCLI port is not listening in this exact owned {role}; no command was sent.");
        }
        if (!found)
            throw new InvalidOperationException($"The recorded ValheimCLI port is not listening in this exact owned {role}; no command was sent.");
    }

    private static string PrivateLeaseMessage(string role) => $"The owned {role} identity file must be a regular file owned by the current user with mode 0600.";
    private const int MacOpenNonBlock = 0x00000004;
    private const int MacOpenNoFollow = 0x00000100;
    private const int MacOpenCloseOnExec = 0x01000000;
    private const ushort MacFileTypeMask = 0xf000;
    private const ushort MacRegularFile = 0x8000;

    // Open first with O_NOFOLLOW, then validate the descriptor. The later JSON read therefore cannot be redirected
    // through a symlink or switched to a different path after validation.
    private static string ReadPrivateUnixLease(string path, string role)
    {
        if (OperatingSystem.IsMacOS()) return ReadPrivateMacLease(path, role);
        if (OperatingSystem.IsLinux()) return ReadPrivateLinuxLease(path, role);
        throw new PlatformNotSupportedException("Private Unix lease reads are supported on macOS and Linux.");
    }

    private static string ReadPrivateMacLease(string path, string role)
    {
        int descriptor = MacOpen(path, MacOpenNonBlock | MacOpenNoFollow | MacOpenCloseOnExec);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 2) throw new FileNotFoundException($"Could not find the owned {role} identity file: " + path, path);
            throw new InvalidOperationException(PrivateLeaseMessage(role), new System.ComponentModel.Win32Exception(error));
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (MacFStatCurrent(descriptor, out MacFileStatus status) != 0)
                throw new InvalidOperationException($"Could not inspect the owned {role} identity file.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            RequirePrivateLease(status.Mode, status.Owner, MacEffectiveUserId(), role);
            using var stream = new FileStream(handle, FileAccess.Read);
            handle = null!; // FileStream owns the descriptor from here.
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        finally { handle?.Dispose(); }
    }

    internal static void RequirePrivateLease(ushort rawMode, uint owner, uint currentUser, string role = OwnedClientCommandLease.ClientRole)
    {
        var permissions = (UnixFileMode)(rawMode & 0x0fff);
        if ((rawMode & MacFileTypeMask) != MacRegularFile || owner != currentUser || permissions != OwnedClientCommandLease.PrivateMode)
            throw new InvalidOperationException(PrivateLeaseMessage(role));
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

    private const int LinuxOpenNonBlock = 0x00000800;
    private const int LinuxOpenCloseOnExec = 0x00080000;

    // Linux does not give arm64 and x64 the same open(2) flag values or struct stat layout. These are the glibc
    // layouts through st_uid; _STAT_VER is 1 on x64 and 0 on arm64 for the pre-2.33 __fxstat compatibility entry point.
    internal static (int OpenNoFollow, int ModeOffset, int OwnerOffset, int StatVersion) LinuxLeaseAbi(Architecture architecture) => architecture switch
    {
        Architecture.X64 => (0x00020000, 24, 28, 1),
        Architecture.Arm64 => (0x00008000, 16, 24, 0),
        _ => throw new InvalidOperationException("Linux private lease inspection supports x64 and arm64 processes."),
    };

    private static string ReadPrivateLinuxLease(string path, string role)
    {
        var abi = LinuxLeaseAbi(RuntimeInformation.ProcessArchitecture);
        int descriptor = LinuxOpen(path, LinuxOpenNonBlock | abi.OpenNoFollow | LinuxOpenCloseOnExec);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 2) throw new FileNotFoundException($"Could not find the owned {role} identity file: " + path, path);
            throw new InvalidOperationException(PrivateLeaseMessage(role), new System.ComponentModel.Win32Exception(error));
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        IntPtr status = Marshal.AllocHGlobal(256);
        try
        {
            if (LinuxFStatCurrent(descriptor, status, abi.StatVersion) != 0)
                throw new InvalidOperationException($"Could not inspect the owned {role} identity file.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            RequirePrivateLease((ushort)Marshal.ReadInt32(status, abi.ModeOffset),
                unchecked((uint)Marshal.ReadInt32(status, abi.OwnerOffset)), LinuxEffectiveUserId(), role);
            using var stream = new FileStream(handle, FileAccess.Read);
            handle = null!; // FileStream owns the descriptor from here.
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        finally
        {
            Marshal.FreeHGlobal(status);
            handle?.Dispose();
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int LinuxFStat(int descriptor, IntPtr status);

    [DllImport("libc", EntryPoint = "__fxstat", SetLastError = true)]
    private static extern int LinuxFxStat(int version, int descriptor, IntPtr status);

    private static int LinuxFStatCurrent(int descriptor, IntPtr status, int statVersion) =>
        LinuxFStatWithFallback(() => LinuxFStat(descriptor, status), () => LinuxFxStat(statVersion, descriptor, status));

    internal static int LinuxFStatWithFallback(Func<int> current, Func<int> compatibility)
    {
        try { return current(); }
        catch (EntryPointNotFoundException)
        {
            try { return compatibility(); }
            catch (EntryPointNotFoundException error)
            {
                throw new InvalidOperationException("This Linux libc exports neither fstat nor its __fxstat compatibility entry point; " +
                    "the private owned-game lease cannot be inspected.", error);
            }
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint LinuxEffectiveUserId();

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

    // Linux exposes the complete listener table in /proc/net, with each socket's inode. Prove every listener for the
    // port is one of the leased process's own descriptors; no other process's descriptor tree needs to be read.
    // procRoot is a test seam for a kernel's /proc (production: /proc).
    internal static IReadOnlyCollection<int> LinuxListenerOwners(int port, int pid, string procRoot = "/proc")
    {
        string tcp = File.ReadAllText(Path.Combine(procRoot, "net", "tcp"));
        // A kernel booted with ipv6.disable=1 has no tcp6 table: no IPv6 listeners. Only absence counts as empty; an
        // unreadable table still refuses, since it could hide a foreign listener.
        string tcp6;
        try { tcp6 = File.ReadAllText(Path.Combine(procRoot, "net", "tcp6")); }
        catch (FileNotFoundException) { tcp6 = ""; }
        var targets = new List<string>();
        foreach (string descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture), "fd")))
        {
            try
            {
                if (new FileInfo(descriptor).LinkTarget is { } target) targets.Add(target);
            }
            catch (FileNotFoundException) { } // An unrelated descriptor closed while the process was inspected.
            catch (DirectoryNotFoundException) { }
        }
        return LinuxListenerOwners(tcp, tcp6, targets, port, pid);
    }

    internal static IReadOnlyCollection<int> LinuxListenerOwners(string tcp, string tcp6,
        IEnumerable<string> ownedDescriptorTargets, int port, int pid)
    {
        var owned = ownedDescriptorTargets.Select(target => LinuxSocketTarget.Match(target))
            .Where(match => match.Success)
            .Select(match => long.Parse(match.Groups["inode"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToHashSet();
        return LinuxListenerInodes(tcp, tcp6, port).Select(inode => owned.Contains(inode) ? pid : 0).ToArray();
    }

    internal static IReadOnlyCollection<long> LinuxListenerInodes(string tcp, string tcp6, int port)
    {
        var inodes = new HashSet<long>();
        foreach (string line in new[] { tcp, tcp6 }.SelectMany(table => table.Split('\n', StringSplitOptions.RemoveEmptyEntries)))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[3] != "0A") continue; // TCP_LISTEN
            int separator = fields[1].LastIndexOf(':');
            if (separator < 0 || !int.TryParse(fields[1].AsSpan(separator + 1), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out int rowPort) || rowPort != port) continue;
            if (fields.Length < 10 || !long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out long inode) || inode <= 0)
                throw new InvalidOperationException("Could not read a socket inode from Linux /proc/net's TCP listener result.");
            inodes.Add(inode);
        }
        return inodes;
    }

    private static readonly Regex LinuxSocketTarget = new(@"^socket:\[(?<inode>\d+)\]$", RegexOptions.CultureInvariant);

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
