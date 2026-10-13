using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class OwnedCliCommandTests
{
    [Fact] public async Task SupportedClientPassthroughSendsOnePinnedCommandToTheOwnedListener()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var commands = new List<string>();
        Task serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync("VALHEIM_CLI_READY");
            await writer.WriteLineAsync("VALHEIM_CLI_CAPS completion");
            while (await reader.ReadLineAsync() is { } line)
            {
                Assert.StartsWith("CMDT:", line);
                string command = line[(line.IndexOf(':', 5) + 1)..];
                commands.Add(command);
                await writer.WriteLineAsync("OUTPUT:1");
                await writer.WriteLineAsync(command.StartsWith("cli_expect --strict ", StringComparison.Ordinal) ? "OK: EXPECT" : "OK: MANIFEST");
                await writer.WriteLineAsync("END_OUTPUT");
            }
        });
        string evidence = Directory.CreateTempSubdirectory("owned-cli-live-").FullName;
        try
        {
            using var process = Process.GetCurrentProcess();
            string start = process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
            OwnedClientCommandLease.Write(evidence, process.Id, start, new ClientRunPlan
            {
                Install = evidence, Port = port, Pins = new() { ["example.mod"] = new string('a', 32) },
            });
            using var output = new StringWriter(); using var error = new StringWriter();
            int result = OwnedCliCommand.Run(["--evidence", evidence, "--phase", "menu", "--command", "cli_manifest"], output, error);
            Assert.Equal(0, result);
            Assert.Contains("OK: MANIFEST", output.ToString());
            Assert.Empty(error.ToString());
            Assert.Equal(3, commands.Count); // initial strict check, per-command strict check, one command
            Assert.All(commands.Take(2), command => Assert.StartsWith("cli_expect --strict ", command));
            Assert.Equal("cli_manifest", commands[2]);
            string log = Assert.Single(Directory.GetFiles(evidence, "owned-cli-command-*.jsonl"));
            Assert.Contains("cli_manifest", File.ReadAllText(log));
            Assert.Contains(log, output.ToString());
        }
        finally
        {
            listener.Stop();
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(evidence, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsPortProofReadsTheActualListeningProcess(bool ipv6)
    {
        if (!OperatingSystem.IsWindows() || ipv6 && !Socket.OSSupportsIPv6) return;
        using var listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int pid = Environment.ProcessId;
        OwnedCliCommand.RequirePortOwner(port, pid);
        Assert.Throws<InvalidOperationException>(() => OwnedCliCommand.RequirePortOwner(port, pid + 1));
        Assert.Throws<InvalidOperationException>(() => OwnedCliCommand.RequirePortOwner(0, pid));
    }

    [Fact] public void WindowsPortProofRefusesARecentlyClosedListener()
    {
        if (!OperatingSystem.IsWindows()) return;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            OwnedCliCommand.RequirePortOwner(port, Environment.ProcessId);
            listener.Stop();
        }

        Assert.Throws<InvalidOperationException>(() =>
            OwnedCliCommand.RequirePortOwner(port, Environment.ProcessId));
    }

    [Fact] public void PassthroughRequiresOneCommandAndIndependentStrictPins()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, OwnedCliCommand.Run(["--evidence", "/run", "--phase", "menu"], output, error));
        Assert.Equal(2, OwnedCliCommand.Run(["--evidence", "/run", "--phase", "menu", "--expect-strict", "pins",
            "--command", "cli_world"], output, error));
        Assert.Equal(2, OwnedCliCommand.Run(["--evidence", "/run", "--phase", "menu", "--command", "cli_world\ncli_save"], output, error));
        Assert.Equal(2, OwnedCliCommand.Run(["--evidence", "/run", "--phase", "menu", "--command", "cli_world",
            "--timeout-seconds", "16"], output, error));
    }

    [Fact] public void LinuxPassthroughWaitsForIssue788Part2()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var output = new StringWriter(); using var error = new StringWriter();
        int result = OwnedCliCommand.Run(["--evidence", "/run", "--phase", "menu", "--command", "cli_manifest"], output, error);
        Assert.Equal(3, result);
        Assert.Contains("#788 part 2", error.ToString());
    }

    [Fact]
    public async Task SupportedClientPassthroughRefusesEveryUnprovenProcessOrPortBeforeSending()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        string root = Directory.CreateTempSubdirectory("owned-cli-refusals-").FullName;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add(typeof(Valheim.Testing.ProcessSignalProbe.Marker).Assembly.Location);
        start.ArgumentList.Add("tcp-listener");
        using var child = Process.Start(start)!;
        try
        {
            string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrWhiteSpace(ready), "the child listener did not report readiness");
            string[] fields = ready.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, fields.Length);
            int childPort = int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
            int childPid = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            using var current = Process.GetCurrentProcess();
            string currentStart = current.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var plan = new ClientRunPlan { Install = root, Pins = new() { ["example.mod"] = new string('a', 32) } };
            const string exactProcess = "The exact client this run started is no longer running; a reused PID is never contacted.";
            const string exactPort = "The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.";

            string missing = Path.Combine(root, "missing");
            Directory.CreateDirectory(missing);
            AssertRefused(missing, "owned-cli.json");

            string reused = Path.Combine(root, "reused");
            Directory.CreateDirectory(reused);
            plan.Port = childPort;
            OwnedClientCommandLease.Write(reused, childPid, "1", plan);
            AssertRefused(reused, exactProcess);

            string changed = Path.Combine(root, "changed");
            Directory.CreateDirectory(changed);
            string changedStart = (long.Parse(currentStart, System.Globalization.CultureInfo.InvariantCulture) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            OwnedClientCommandLease.Write(changed, current.Id, changedStart, plan);
            AssertRefused(changed, exactProcess);

            string foreign = Path.Combine(root, "foreign");
            Directory.CreateDirectory(foreign);
            OwnedClientCommandLease.Write(foreign, current.Id, currentStart, plan);
            AssertRefused(foreign, exactPort);

            using var noListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            noListener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Holds the port against reuse without listening on it.
            int closedPort = ((IPEndPoint)noListener.LocalEndPoint!).Port;
            string absent = Path.Combine(root, "absent");
            Directory.CreateDirectory(absent);
            plan.Port = closedPort;
            OwnedClientCommandLease.Write(absent, current.Id, currentStart, plan);
            AssertRefused(absent, exactPort);

            Assert.Empty(Directory.GetFiles(root, "owned-cli-command-*.jsonl", SearchOption.AllDirectories));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertRefused(string evidence, string message)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        int result = OwnedCliCommand.Run(["--evidence", evidence, "--phase", "menu", "--command", "cli_manifest"], output, error);
        Assert.Equal(3, result);
        Assert.Empty(output.ToString());
        Assert.Contains(message, error.ToString());
    }

    [Fact] public void PassthroughChecksTheExactProcessStartAndWritesPinnedPhases()
    {
        using var current = Process.GetCurrentProcess();
        string start = current.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var matching = new OwnedClientCommandLease(current.Id, start, 5689, Path.GetTempPath());
        Assert.True(OwnedCliCommand.IsExactProcess(current, matching));
        Assert.False(OwnedCliCommand.IsExactProcess(current, matching with { StartFileTimeUtc = "1" }));
        Assert.False(OwnedCliCommand.IsExactProcess(current, matching with { Pid = current.Id + 1 }));
        string output = Directory.CreateTempSubdirectory("owned-cli-").FullName;
        try
        {
            var plan = new ClientRunPlan
            {
                Install = Path.GetTempPath(), Port = 5689,
                Pins = new() { ["my.mod"] = new string('a', 32) },
                HostWorld = new HostWorldPlan { WorldUid = "123" },
            };
            OwnedClientCommandLease.Write(output, current.Id, start, plan);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(OwnedClientCommandLease.PrivateMode,
                    File.GetUnixFileMode(Path.Combine(output, OwnedClientCommandLease.FileName)));
                Assert.Equal(OwnedClientCommandLease.PrivateMode,
                    File.GetUnixFileMode(Path.Combine(output, OwnedClientCommandLease.MenuPins)));
            }
            Assert.Contains("my.mod=" + new string('a', 32), File.ReadAllText(Path.Combine(output, OwnedClientCommandLease.MenuPins)));
            Assert.DoesNotContain("worlduid", File.ReadAllText(Path.Combine(output, OwnedClientCommandLease.MenuPins)));
            Assert.False(File.Exists(Path.Combine(output, OwnedClientCommandLease.WorldPins)));
            OwnedClientCommandLease.WriteVerifiedWorld(output, plan, "123");
            Assert.Contains("worlduid=123", File.ReadAllText(Path.Combine(output, OwnedClientCommandLease.WorldPins)));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(OwnedClientCommandLease.PrivateMode,
                    File.GetUnixFileMode(Path.Combine(output, OwnedClientCommandLease.WorldPins)));
            Assert.Contains("--strict", StrictExpectations.Load(Path.Combine(output, OwnedClientCommandLease.WorldPins)));
            OwnedClientCommandLease.LeaveWorld(output);
            Assert.False(File.Exists(Path.Combine(output, OwnedClientCommandLease.WorldPins)));
        }
        finally { Directory.Delete(output, recursive: true); }
    }

    [Theory]
    [InlineData("cli_screenshot", 1)]
    [InlineData("cli_screenshot 2", 2)]
    [InlineData("  cli_screenshot   4 ", 4)]
    public void ALoneIntegerIsTheSupersize(string command, int expected) =>
        Assert.Equal(expected, OwnedCliCommand.ScreenshotSupersize(command));

    [Theory]
    [InlineData("cli_screenshot inspection")]
    [InlineData("cli_screenshot inspection 2")]
    [InlineData("cli_screenshot 0")]
    [InlineData("cli_screenshot 5")]
    [InlineData("cli_screenshot -1")]
    [InlineData("cli_screenshot 2 3")]
    public void AScreenshotNameOrABadSupersizeIsRefusedWithTheReason(string command)
    {
        var refused = Assert.Throws<ArgumentException>(() => OwnedCliCommand.ScreenshotSupersize(command));
        Assert.Contains("takes only a supersize", refused.Message);
        Assert.Contains("no name", refused.Message);
    }

    [Theory]
    [InlineData("cli_screenshot_path64 abc")]
    [InlineData("cli_screenshots 2")]
    [InlineData("cli_manifest")]
    [InlineData("")]
    public void OtherCommandsAreNotScreenshots(string command) => Assert.Null(OwnedCliCommand.ScreenshotSupersize(command));
}

[Trait("Os", "macos")]
public sealed class OwnedCliCommandMacTests
{
    private const string ExactPort = "The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.";
    private const string PrivateLease = "The owned client identity file must be a regular file owned by the current user with mode 0600.";

    [Fact]
    public void CapturedNetstatFixturesFindIPv4AndForeignIPv6AndTcp46Listeners()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string real = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Netstat", "macos-listener.txt"));
        string foreign = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Netstat", "macos-foreign-ipv6.txt"));
        string dual = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Netstat", "macos-foreign-tcp46.txt"));

        Assert.Equal([67332], OwnedCliCommand.MacListenerOwners(real, "", 0, 63661));
        Assert.Equal([4242, 67332], OwnedCliCommand.MacListenerOwners(real + foreign, "", 0, 63661).Order());
        IReadOnlyCollection<int> dualOwners = OwnedCliCommand.MacListenerOwners(real + dual, "", 0, 63661);
        Assert.Equal([5151, 67332], dualOwners.Order());
        Assert.Throws<InvalidOperationException>(() => OwnedCliCommand.RequireOnlyPortOwner(dualOwners, 67332));
        Assert.Empty(OwnedCliCommand.MacListenerOwners(real, "", 0, 63662));
    }

    [Fact]
    public void NetstatFailuresAndUnreadableOwnersAreNotReportedAsNoListener()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var tool = Assert.Throws<InvalidOperationException>(() =>
            OwnedCliCommand.MacListenerOwners("", "sysctl: permission denied", 1, 63661));
        Assert.Contains("Could not read", tool.Message);
        Assert.Contains("sysctl: permission denied", tool.Message);

        const string unreadable =
            "tcp6       0      0  *.63661                *.*                    LISTEN                 0            0  131072  131072        foreign-test:-      00100 00000006 00000000168d6b0c 00000000 00000800      1      0 000000";
        var owner = Assert.Throws<InvalidOperationException>(() =>
            OwnedCliCommand.MacListenerOwners(unreadable, "", 0, 63661));
        Assert.Contains("process ID", owner.Message);
    }

    [Fact]
    public async Task MacPassthroughRefusesNonPrivateSymlinkAndFifoLeasesWithoutFollowingOrBlocking()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string root = Directory.CreateTempSubdirectory("owned-cli-private-").FullName;
        try
        {
            string mode = Path.Combine(root, "mode");
            Directory.CreateDirectory(mode);
            WriteCurrentProcessLease(mode, 5689);
            File.SetUnixFileMode(Path.Combine(mode, OwnedClientCommandLease.FileName),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            AssertLeaseRefused(mode);

            string linked = Path.Combine(root, "linked");
            Directory.CreateDirectory(linked);
            WriteCurrentProcessLease(linked, 5689);
            string lease = Path.Combine(linked, OwnedClientCommandLease.FileName);
            string target = Path.Combine(linked, "lease-target.json");
            File.Move(lease, target);
            File.CreateSymbolicLink(lease, target);
            AssertLeaseRefused(linked);

            string fifo = Path.Combine(root, "fifo");
            Directory.CreateDirectory(fifo);
            string fifoLease = Path.Combine(fifo, OwnedClientCommandLease.FileName);
            var start = new ProcessStartInfo("/usr/bin/mkfifo") { UseShellExecute = false };
            start.ArgumentList.Add(fifoLease);
            using (var mkfifo = Process.Start(start)!)
            {
                Assert.True(mkfifo.WaitForExit(5000), "mkfifo did not exit");
                Assert.Equal(0, mkfifo.ExitCode);
            }
            await Task.Run(() => AssertLeaseRefused(fifo)).WaitAsync(TimeSpan.FromSeconds(2));

            const ushort regular0600 = 0x8000 | 0x0180;
            OwnedCliCommand.RequirePrivateLease(regular0600, 501, 501);
            Assert.Throws<InvalidOperationException>(() => OwnedCliCommand.RequirePrivateLease(regular0600, 502, 501));
            Assert.Throws<InvalidOperationException>(() => OwnedCliCommand.RequirePrivateLease(0x4000 | 0x0180, 501, 501));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProcessIdentityAndPortProofAgreeAcrossProcessesAndRefuseEveryWrongOwner()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Valheim.Testing.ProcessSignalProbe.Marker).Assembly.Location);
        start.ArgumentList.Add("tcp-listener");
        using var child = Process.Start(start)!;
        try
        {
            string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrWhiteSpace(ready), "the child listener did not report readiness");
            string[] fields = ready.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, fields.Length);
            int port = int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
            int pid = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(child.Id, pid);

            using var independentView = Process.GetProcessById(pid);
            var lease = new OwnedClientCommandLease(pid, fields[2], port, Path.GetTempPath());
            Assert.True(OwnedCliCommand.IsExactProcess(independentView, lease));
            Assert.False(OwnedCliCommand.IsExactProcess(independentView, lease with { StartFileTimeUtc = "1" }));
            OwnedCliCommand.RequirePortOwner(port, pid);
            var foreign = Assert.Throws<InvalidOperationException>(() => OwnedCliCommand.RequirePortOwner(port, Environment.ProcessId));
            Assert.Equal("The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.", foreign.Message);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int ownedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        OwnedCliCommand.RequirePortOwner(ownedPort, Environment.ProcessId);
        listener.Stop();

        using var noListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        noListener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Holds the port against reuse without listening on it.
        int closedPort = ((IPEndPoint)noListener.LocalEndPoint!).Port;
        var missing = Assert.Throws<InvalidOperationException>(() => OwnedCliCommand.RequirePortOwner(closedPort, Environment.ProcessId));
        Assert.Equal("The recorded ValheimCLI port is not listening in this exact owned client; no command was sent.", missing.Message);
    }

    [Fact]
    public async Task IPv6OnlyForeignListenerIsRefusedBeforeAnyConnection()
    {
        if (!OperatingSystem.IsMacOS() || !Socket.OSSupportsIPv6) return;
        string evidence = Directory.CreateTempSubdirectory("owned-cli-ipv6-only-").FullName;
        using var child = StartIPv6Listener(0);
        try
        {
            (int port, _, _) = await ReadListener(child);
            WriteCurrentProcessLease(evidence, port);

            AssertRefusedBeforeConnection(evidence);
        }
        finally
        {
            Stop(child);
            Directory.Delete(evidence, recursive: true);
        }
    }

    [Fact]
    public async Task MixedOwnedIPv4AndForeignIPv6ListenersAreRefusedBeforeAnyConnection()
    {
        if (!OperatingSystem.IsMacOS() || !Socket.OSSupportsIPv6) return;
        string evidence = Directory.CreateTempSubdirectory("owned-cli-mixed-family-").FullName;
        using var owned = new TcpListener(IPAddress.Loopback, 0);
        owned.Start();
        int port = ((IPEndPoint)owned.LocalEndpoint).Port;
        using var child = StartIPv6Listener(port);
        try
        {
            (int childPort, int childPid, _) = await ReadListener(child);
            Assert.Equal(port, childPort);
            Assert.NotEqual(Environment.ProcessId, childPid);
            WriteCurrentProcessLease(evidence, port);

            AssertRefusedBeforeConnection(evidence);
            Assert.False(owned.Pending());
        }
        finally
        {
            Stop(child);
            Directory.Delete(evidence, recursive: true);
        }
    }

    private static Process StartIPv6Listener(int port)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(Valheim.Testing.ProcessSignalProbe.Marker).Assembly.Location);
        start.ArgumentList.Add("tcp-listener6");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start)!;
    }

    private static async Task<(int Port, int Pid, string Start)> ReadListener(Process child)
    {
        string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (string.IsNullOrWhiteSpace(ready))
        {
            string diagnostic = child.HasExited ? await child.StandardError.ReadToEndAsync() : "child is still running";
            Assert.Fail("the IPv6 child listener did not report readiness: " + diagnostic);
        }
        string[] fields = ready!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, fields.Length);
        return (int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), fields[2]);
    }

    private static void WriteCurrentProcessLease(string evidence, int port)
    {
        using var current = Process.GetCurrentProcess();
        OwnedClientCommandLease.Write(evidence, current.Id,
            current.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture),
            new ClientRunPlan { Install = evidence, Port = port, Pins = new() { ["example.mod"] = new string('a', 32) } });
    }

    private static void AssertRefusedBeforeConnection(string evidence)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int result = OwnedCliCommand.Run(
            ["--evidence", evidence, "--phase", "menu", "--command", "cli_manifest", "--timeout-seconds", "1"], output, error);
        Assert.Equal(3, result);
        Assert.Empty(output.ToString());
        Assert.Contains(ExactPort, error.ToString());
        Assert.Empty(Directory.GetFiles(evidence, "owned-cli-command-*.jsonl"));
    }

    private static void AssertLeaseRefused(string evidence)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int result = OwnedCliCommand.Run(
            ["--evidence", evidence, "--phase", "menu", "--command", "cli_manifest"], output, error);
        Assert.Equal(3, result);
        Assert.Empty(output.ToString());
        Assert.Contains(PrivateLease, error.ToString());
        Assert.Empty(Directory.GetFiles(evidence, "owned-cli-command-*.jsonl"));
    }

    private static void Stop(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(5000), "the IPv6 child listener did not stop");
        }
    }
}
