namespace Valheim.Testing.ProcessSignalProbe;

public static class Marker { }

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args is ["tcp-listener"])
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine(string.Join(' ', ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, process.Id,
                process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Console.Out.Flush();
            Thread.Sleep(TimeSpan.FromMinutes(2));
            return;
        }
        if (args is ["owned-cli-listener"])
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine(string.Join(' ', ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, process.Id,
                process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Console.Out.Flush();
            using var client = listener.AcceptTcpClient();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine("VALHEIM_CLI_READY");
            writer.WriteLine("VALHEIM_CLI_CAPS completion");
            while (reader.ReadLine() is { } line)
            {
                string command = line[(line.IndexOf(':', 5) + 1)..];
                writer.WriteLine("OUTPUT:1");
                writer.WriteLine(command.StartsWith("cli_expect --strict ", StringComparison.Ordinal) ? "OK: EXPECT" : "OK: MANIFEST");
                writer.WriteLine("END_OUTPUT");
            }
            return;
        }
        if (args is ["tcp-listener6", var portText])
        {
            int port = int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture);
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, port);
            listener.Server.DualMode = false;
            listener.Start();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine(string.Join(' ', ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, process.Id,
                process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Console.Out.Flush();
            Thread.Sleep(TimeSpan.FromMinutes(2));
            return;
        }
        if (args is ["run-cancellation", var cancellationMarker])
        {
            using var cancellation = new Valheim.Testing.GameSessions.RunCancellation();
            File.WriteAllText(cancellationMarker + ".ready", Environment.ProcessId.ToString());
            bool signalled = cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            File.WriteAllText(cancellationMarker, signalled ? "cancelled" : "timed-out");
            return;
        }
        if (args is ["stdio-gate", var outputMarker])
        {
            File.WriteAllText(outputMarker + ".ready", Environment.ProcessId.ToString());
            using var released = new ManualResetEventSlim();
            using var watcher = new FileSystemWatcher(Path.GetDirectoryName(outputMarker)!, Path.GetFileName(outputMarker) + ".release")
            { EnableRaisingEvents = false };
            watcher.Created += (_, _) => released.Set();
            watcher.EnableRaisingEvents = true;
            // The test releases this gate only after the scheduled-task launcher has exited.
            if (!File.Exists(outputMarker + ".release") && !released.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The stdio test gate was not released.");
            Console.Out.WriteLine("VT-STDOUT after launcher exit");
            Console.Error.WriteLine("VT-STDERR after launcher exit");
            Console.Out.Flush();
            Console.Error.Flush();
            Thread.Sleep(TimeSpan.FromMinutes(2));
            return;
        }
        if (args is ["process-tree", var treeMarker])
        {
            // A process that starts a child, as a build starts compiler processes. Ready means the child is running.
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { typeof(Program).Assembly.Location, "tree-child", treeMarker },
                UseShellExecute = false,
            })!;
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(treeMarker + ".child") && started.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(25);
            File.WriteAllText(treeMarker + ".ready", Environment.ProcessId.ToString());
            Thread.Sleep(TimeSpan.FromMinutes(2));
            return;
        }
        if (args is ["tree-child", var childMarker])
        {
            File.WriteAllText(childMarker + ".child", Environment.ProcessId.ToString());
            Thread.Sleep(TimeSpan.FromMinutes(2));
            return;
        }
        string marker = args[0];
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            File.WriteAllText(marker, eventArgs.SpecialKey.ToString());
            eventArgs.Cancel = false;
        };
        File.WriteAllText(marker + ".ready", Environment.ProcessId.ToString());
        Thread.Sleep(TimeSpan.FromMinutes(2));
    }
}
