using System.Globalization;

namespace Valheim.Testing.GameSessions;

// The three disposable-client staging paths must agree on ValheimCLI's server settings. An explicitly selected
// config is never rewritten: refusing it before a copy is safer than silently granting client mutation access.
internal static class CliServerConfig
{
    internal const string RelativePath = "BepInEx/config/valheimCLI.valheimCLI.cfg";

    // The single decision point for a disposable actor's selected ValheimCLI config. An explicit config is kept as
    // selected; a client scenario needing protection or arrival refuses insufficient access before copying.
    internal static List<HostedRuntimeFile> Stage(IEnumerable<HostedRuntimeFile> selected, string actor, int port,
        string generatedPath, bool requireClientMutations = true)
    {
        var files = selected.ToList();
        var chosen = files.SingleOrDefault(file => file.RelativePath.Equals(RelativePath, StringComparison.OrdinalIgnoreCase));
        if (chosen != null)
        {
            if (requireClientMutations) RequireClientMutations(chosen.Source, actor);
            return files;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllText(generatedPath, ForOwnedTestActor(port));
        files.Add(new HostedRuntimeFile(generatedPath, RelativePath));
        return files;
    }

    private static string ForOwnedTestActor(int port) =>
        "[Server]\nEnabled = true\nAllowOnServerClients = true\nPort = " + port.ToString(CultureInfo.InvariantCulture) + "\n";

    internal static void RequireClientMutations(string path, string actor)
    {
        string section = "";
        bool allowed = false;
        foreach (string line in File.ReadLines(path))
        {
            string value = line.Trim();
            if (value.Length == 0 || value[0] is '#' or ';') continue;
            if (value[0] == '[' && value.EndsWith(']'))
            {
                // BepInEx keeps whitespace inside section brackets; [ Server ] is not [Server].
                section = value[1..^1];
                continue;
            }
            if (!section.Equals("Server", StringComparison.Ordinal)) continue;
            int equals = value.IndexOf('=');
            if (equals < 0 || !value[..equals].Trim().Equals("AllowOnServerClients", StringComparison.Ordinal)) continue;
            // BepInEx keeps inline comment text as part of a value and matches ConfigDefinition names exactly.
            // Accept only the value the loaded plugin can actually parse as true.
            allowed = bool.TryParse(value[(equals + 1)..].Trim(), out bool parsed) && parsed;
        }
        if (!allowed)
            throw new InvalidDataException($"{actor}'s selected {RelativePath} does not set [Server] AllowOnServerClients = true. " +
                "This owned joined-client test needs mutation access for protection or arrival. Set that value in the selected config, " +
                "or use an attached or in-place read-only scenario without those actions; the source install will not be changed.");
    }
}
