namespace Valheim.Testing.Game;

/// <summary>The one owner of "is this path inside that protected folder". Every output, install, source and fixture check asks here.</summary>
internal static class ProtectedPaths
{
    private const int MaxLinkDepth = 16;

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies under it, after both are resolved to their physical locations.</summary>
    internal static bool Contains(string root, string path)
    {
        // The usual Windows and macOS volumes ignore case. Refuse a possible overlap even on a case-sensitive Mac volume.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Resolve(root));
        path = Path.TrimEndingDirectorySeparator(Resolve(path));
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// The physical location of a path: every existing ancestor is resolved through symlinks and junctions
    /// (<c>/tmp</c> to <c>/private/tmp</c>, a symlinked folder inside an install), a link may point through further links
    /// up to a bound, and the final path need not exist yet. A link loop or too deep a chain throws <see cref="IOException"/>.
    /// </summary>
    internal static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        for (int i = 0; i < MaxLinkDepth; i++)
        {
            string resolved = ResolveExistingLinks(full, path);
            if (resolved == full) return resolved;
            full = resolved;
        }
        throw new IOException("The path has too many symlink or junction levels, or a link loop: " + path);
    }

    private static string ResolveExistingLinks(string full, string given)
    {
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            // A dangling link is still a link: its target may be a protected folder that is created later.
            if (!info.Exists && info.LinkTarget == null) continue;
            try { current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Cannot resolve the symlink or junction {current} in {given}; it may be a link loop or unreadable.", error);
            }
        }
        return Path.GetFullPath(current);
    }
}
