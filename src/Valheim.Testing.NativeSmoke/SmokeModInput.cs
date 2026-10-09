using Valheim.Testing.Game;

/// <summary>One rule for the mod under test when a one-shot command names no --mod.</summary>
internal static class SmokeModInput
{
    internal sealed record Selection(IReadOnlyList<string> Mods, string Reason);

    internal static Selection Select(IReadOnlyList<string> given, string directory, bool requireExplicit = false)
    {
        if (given.Count != 0) return new(given.Select(Path.GetFullPath).ToArray(), "explicit --mod");
        if (requireExplicit) throw new ArgumentException("server-load-ab needs at least two explicit --mod DLLs and one --remove-mod DLL.");
        directory = Path.GetFullPath(directory);
        string[] projects = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        if (projects.Length != 1)
            throw new ArgumentException(projects.Length == 0
                ? $"No --mod and no .csproj in {directory}; build a mod project here or give --mod DLL."
                : $"No --mod and several .csproj files in {directory}: {string.Join(", ", projects)}. Give --mod DLL.");

        string bin = Path.Combine(directory, "bin");
        string[] outputs = Directory.Exists(bin) ? Directory.GetFiles(bin, "*.dll", SearchOption.AllDirectories)
            .Where(file => IsBuildOutput(bin, file)).ToArray() : [];
        if (outputs.Length == 0)
            throw new ArgumentException($"No built DLL is under {bin} for {Path.GetFileName(projects[0])}; build the project first or give --mod DLL.");
        var plugins = new List<string>();
        foreach (string file in outputs.Order(StringComparer.Ordinal))
        {
            try { if (PluginMetadata.Read(file).Plugins.Count != 0) plugins.Add(file); }
            catch (BadImageFormatException) { } // A native DLL beside the mod is not a BepInEx plugin.
        }
        // Dependencies copied into bin can be newer than the plugin. Choose the latest build
        // containing a plugin, then require precisely one plugin in that build directory.
        string? latest = plugins.GroupBy(Path.GetDirectoryName, StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(File.GetLastWriteTimeUtc))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Key;
        string[] chosen = latest == null ? [] : plugins.Where(file => Path.GetDirectoryName(file) == latest).ToArray();
        if (chosen.Length != 1)
            throw new ArgumentException(chosen.Length == 0
                ? $"No [BepInPlugin] assembly is in the build output under {bin}. Inspected: {string.Join(", ", outputs)}. Give --mod DLL."
                : $"Several [BepInPlugin] assemblies are in {latest}: {string.Join(", ", chosen)}. Give --mod DLL.");

        string selected = chosen[0];
        DateTime latestSource = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Select(File.GetLastWriteTimeUtc).Append(File.GetLastWriteTimeUtc(projects[0])).Max();
        if (latestSource > File.GetLastWriteTimeUtc(selected))
            throw new ArgumentException($"The newest source file in {directory} is newer than built plugin {selected}; rebuild the project or give --mod DLL explicitly.");
        string reason = $"one project {Path.GetFileName(projects[0])}; newest build output {latest}; one [BepInPlugin] assembly";
        return new([selected], reason);
    }

    private static bool IsBuildOutput(string bin, string file)
    {
        string relative = Path.GetRelativePath(bin, file);
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        return parts.Length == 3 && (parts[0] is "Debug" or "Release") && parts[1].StartsWith("net", StringComparison.OrdinalIgnoreCase);
    }
}
