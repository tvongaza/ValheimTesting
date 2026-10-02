namespace Valheim.Testing.Game;

/// <summary>A password-free, one-use startup request for the ValheimCLI Standard pack.</summary>
internal static class DirectWorldStart
{
    internal const string FileVariable = "VALHEIMCLI_START_WORLD_FILE";
    internal const string ClaimedVariable = "VALHEIMCLI_START_WORLD_CLAIMED";

    internal static string Write(ClientRunPlan plan, string output)
    {
        if (!plan.DirectStart) throw new ArgumentException("The client plan did not request direct start.");
        plan.Validate();
        string mode = plan.HostWorld == null ? "join" : plan.HostWorld.Local ? "local" : "host";
        string target = plan.HostWorld == null ? plan.Join : plan.HostWorld.Preflight().Name;
        if (target.Length == 0 || target.Any(char.IsWhiteSpace) || target.Contains('=') || target.Contains('/') || target.Contains('\\'))
            throw new ArgumentException("The direct-start address or fixture world name must be one token without a path or '='.");
        if (plan.PasswordVariable is { } variable &&
            (variable.Length == 0 || !(char.IsLetter(variable[0]) || variable[0] == '_') ||
             variable.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))))
            throw new ArgumentException("The direct-start passwordVariable must name an environment variable, not contain a password.");
        string path = Path.GetFullPath(Path.Combine(output, "client-direct-start.txt"));
        // The character, address and fixture name have already passed the plan's token validation. The file carries
        // neither the password nor its value, only the variable the owned game process inherits from the runner.
        var lines = new List<string> { "version=1", "mode=" + mode, "character=" + plan.Character,
            "target=" + target, "devcommands=true" };
        if (plan.PasswordVariable != null) lines.Add("passwordVariable=" + plan.PasswordVariable);
        if (mode == "host")
        {
            lines.Add("public=false");
            lines.Add("crossplay=" + (plan.HostWorld!.Crossplay ? "true" : "false"));
        }
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
            foreach (string line in lines) writer.WriteLine(line);
        return path;
    }
}
