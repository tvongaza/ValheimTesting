namespace Valheim.Testing.Game;

/// <summary>The one owner of "is this path inside that protected folder". Every output, install, source and fixture check asks here.</summary>
internal static class ProtectedPaths
{
    private const int MaxLinkHops = 40;

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
    /// The physical location of a path, walked component by component: each existing link (symlink or junction) is replaced
    /// by its target and the walk continues there, so <c>/tmp</c> becomes <c>/private/tmp</c>, a symlinked folder inside an install is
    /// the install, and a <c>..</c> in the given path after a link goes up from the link's target (one inside a link's own target text is collapsed as written). The final path need not exist yet. More than
    /// <see cref="MaxLinkHops"/> links in one path, which includes any link loop, throws <see cref="IOException"/>.
    /// </summary>
    internal static string Resolve(string path)
    {
        // GetFullPath would collapse "link/.." lexically, before the link is followed.
        string raw = Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);
        string root = Path.GetPathRoot(raw)!;
        string current = Path.GetFullPath(root); // "C:/" becomes "C:\" on Windows
        var pending = new Stack<string>(Parts(raw[root.Length..]).Reverse());
        int hops = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".") continue;
            if (part == "..") { current = Directory.GetParent(current)?.FullName ?? current; continue; }
            // The part holds no dot segments now, so GetFullPath only normalizes what Windows does: trailing dots and spaces.
            string next = Path.GetFullPath(Path.Combine(current, part));
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            // A dangling link is still a link: its target may be a protected folder that is created later.
            if (!info.Exists && info.LinkTarget == null) { current = next; continue; }
            FileSystemInfo? target;
            try { target = info.ResolveLinkTarget(returnFinalTarget: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Cannot read the symlink or junction {next} in {path}.", error);
            }
            if (target == null) { current = next; continue; }
            if (++hops > MaxLinkHops)
                throw new IOException($"The path {path} passes through more than {MaxLinkHops} symlinks or junctions; it may be a link loop.");
            current = Path.GetPathRoot(target.FullName)!;
            foreach (string again in Parts(target.FullName[current.Length..]).Reverse()) pending.Push(again);
        }
        return current;
    }

    private static string[] Parts(string relative) =>
        relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
