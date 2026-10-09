using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class OwnedCliCommandTests
{
    [Fact] public async Task WindowsPassthroughSendsOnePinnedCommandToTheOwnedListener()
    {
        if (!OperatingSystem.IsWindows()) return;
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
            Assert.Contains("my.mod=" + new string('a', 32), File.ReadAllText(Path.Combine(output, OwnedClientCommandLease.MenuPins)));
            Assert.DoesNotContain("worlduid", File.ReadAllText(Path.Combine(output, OwnedClientCommandLease.MenuPins)));
            Assert.Contains("worlduid=123", File.ReadAllText(Path.Combine(output, OwnedClientCommandLease.WorldPins)));
            Assert.Contains("--strict", StrictExpectations.Load(Path.Combine(output, OwnedClientCommandLease.WorldPins)));
        }
        finally { Directory.Delete(output, recursive: true); }
    }
}
