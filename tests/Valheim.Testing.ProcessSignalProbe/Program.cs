namespace Valheim.Testing.ProcessSignalProbe;

public static class Marker { }

internal static class Program
{
    private static void Main(string[] args)
    {
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
