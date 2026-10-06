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
