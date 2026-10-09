using Valheim.Testing.Game;

internal static class SmokeOutput
{
    // Check the actual selected actors, not every inventory entry or a separately guessed default.
    public static void RefuseResolved(string output, string cliFiles, EnvironmentInventory? inventory,
        IEnumerable<EnvironmentRecipe?> actors, params string?[] otherProtected)
    {
        RefuseInside(output, actors.OfType<EnvironmentRecipe>().Select(actor => actor.Install)
            .Concat(new[] { cliFiles, inventory?.SteamUserData }.OfType<string>())
            .Concat(otherProtected.OfType<string>()).ToArray());
    }

    // The same marker means every one-shot command stopped without a passing result. Keep the refusal and any
    // staged inputs for review; recovery is still governed by the run journal, not by this file.
    public static void MarkRefused(string? output, string command, IEnumerable<string> reasons)
    {
        if (output == null || !Directory.Exists(output)) return;
        try
        {
            File.WriteAllText(Path.Combine(output, "REFUSED.txt"),
                $"This folder belongs to a refused {command}; no passing result was established. Check valheim-test env status before reusing this environment.\n" +
                string.Concat(reasons.Select(reason => "- " + reason + "\n")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // refusal already printed
    }

    // Keep disposable copies and evidence outside installs and account data, before creating any output directory.
    public static void RefuseInside(string output, params string[] sources)
    {
        foreach (string source in sources)
        {
            if (ProtectedPaths.Contains(source, output))
                throw new ArgumentException($"--output must be outside the prepared install and account directories: {Path.GetFullPath(source)}");
        }
    }

    // A server-load copies the server once (the staged runtime, which the run then runs from) and the client once, all
    // inside --output. Refuse before the first copy when they do not fit.
    public static void RequireSpace(string output, string server, string? client) =>
        Valheim.Testing.Game.DiskSpace.Require(output,
            Valheim.Testing.Game.DiskSpace.DirectoryBytes(server) + (client == null ? 0 : Valheim.Testing.Game.DiskSpace.DirectoryBytes(client)),
            client == null ? "the staged server copy" : "the staged server copy and the staged clean client");
}
