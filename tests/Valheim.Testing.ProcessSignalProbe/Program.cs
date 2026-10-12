namespace Valheim.Testing.ProcessSignalProbe;

public static class Marker { }

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args is ["run-cancellation", var cancellationMarker])
        {
            using var cancellation = new Valheim.Testing.GameSessions.RunCancellation();
            File.WriteAllText(cancellationMarker + ".ready", Environment.ProcessId.ToString());
            bool signalled = cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            File.WriteAllText(cancellationMarker, signalled ? "cancelled" : "timed-out");
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
