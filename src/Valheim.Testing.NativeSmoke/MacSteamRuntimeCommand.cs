using Valheim.Testing.Game;

/// <summary>Captures SteamCMD's macOS native dependency closure as a reviewed input for server profiles.</summary>
internal static class MacSteamRuntimeCommand
{
    internal const string Usage = "valheim-test steam-runtime capture --steamcmd DIR --output NEW_FILE";

    internal static int Run(string[] args)
    {
        if (args.Length != 5 || args[0] != "capture" ||
            args[1] is not ("--steamcmd" or "--output") || args[3] is not ("--steamcmd" or "--output") ||
            args[1] == args[3] || string.IsNullOrWhiteSpace(args[2]) || string.IsNullOrWhiteSpace(args[4]))
        {
            Console.Error.WriteLine("Usage: " + Usage);
            return 2;
        }
        try
        {
            string source = args[1] == "--steamcmd" ? args[2] : args[4];
            string output = args[1] == "--output" ? args[2] : args[4];
            output = Path.GetFullPath(output);
            if (Path.Exists(output)) throw new IOException("The reviewed manifest already exists; choose a new output file: " + output);
            var package = MacSteamRuntimePackage.Capture(source);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            package.Write(output);
            Console.WriteLine($"Captured SteamCMD macOS build {package.Version}: {package.Files.Count} pinned native files in {output}");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or PlatformNotSupportedException or TimeoutException)
        {
            Console.Error.WriteLine("REFUSED: " + error.Message);
            return 3;
        }
    }
}
