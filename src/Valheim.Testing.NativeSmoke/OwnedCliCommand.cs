using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    // Query Windows' listener table directly: starting PowerShell and Get-NetTCPConnection for each command
    // made this proof depend on an unrelated shell's cold-start time.
    internal static void RequirePortOwner(int port, int pid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The owned-client port proof requires Windows.");
        bool found = false;
        foreach (int family in new[] { AfInet, AfInet6 })
        {
            foreach (int owner in ListenerOwners(family, port))
            {
                found = true;
                if (owner != pid)
                    throw new InvalidOperationException("The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.");
            }
        }
        if (!found)
            throw new InvalidOperationException("The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.");
    }

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

    private static IEnumerable<int> ListenerOwners(int family, int port)
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
