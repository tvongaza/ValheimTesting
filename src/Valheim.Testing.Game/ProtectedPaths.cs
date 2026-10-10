namespace Valheim.Testing.Game;

internal static class ProtectedPaths
{
    internal static bool Contains(string root, string path)
    {
        // The usual Windows and macOS volumes ignore case. Refuse a possible overlap even on a case-sensitive Mac volume.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
