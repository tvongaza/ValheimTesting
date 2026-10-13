using Valheim.Testing.Game;

internal static class SmokeOutput
{
    // Check the actual selected actors, not every inventory entry or a separately guessed default.
    public static void RefuseResolved(string output, string? cliFiles, EnvironmentInventory? inventory,
        IEnumerable<EnvironmentRecipe?> actors, params string?[] otherProtected)
    {
        string[] sources = actors.OfType<EnvironmentRecipe>().Select(actor => actor.Install)
            .Concat(new[] { cliFiles, inventory?.SteamUserData }.OfType<string>())
            .Concat(otherProtected.OfType<string>()).ToArray();
        RefuseInside(output, sources);
        // An output that holds a selected source is as wrong as one inside it (server-load-phases checked this before #745).
        foreach (string source in sources)
            if (ProtectedPaths.Contains(output, source))
                throw new ArgumentException($"--output must not contain the prepared install and account directories: {Path.GetFullPath(source)}");
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
}
