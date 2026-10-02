internal static class SmokeOutput
{
    // Keep disposable copies and evidence outside installs and account data, before creating any output directory.
    public static void RefuseInside(string output, params string[] sources)
    {
        output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string source in sources)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            if (output.Equals(root, comparison) || output.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException($"--output must be outside the prepared install and account directories: {root}");
        }
    }

    // A server-load copies the server once (the staged runtime, which the run then runs from) and the client once, all
    // inside --output. Refuse before the first copy when they do not fit.
    public static void RequireSpace(string output, string server, string? client) =>
        Valheim.Testing.Game.DiskSpace.Require(output,
            Valheim.Testing.Game.DiskSpace.DirectoryBytes(server) + (client == null ? 0 : Valheim.Testing.Game.DiskSpace.DirectoryBytes(client)),
            client == null ? "the staged server copy" : "the staged server copy and the staged clean client");
}
