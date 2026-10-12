using System.Security.Cryptography;
using System.Text;

namespace Valheim.Testing.Game;

/// <summary>
/// The toolkit's one owner of hashing: every SHA256 and MD5 this package computes, as lower-case hex. Pins, manifests,
/// listings and evidence checksums are SHA256; plugin pins are MD5 (<see cref="Md5"/>). Two hashes are computed elsewhere on
/// purpose: a host computes its own listing remotely (<c>sha256sum</c>, or .NET in PowerShell), which
/// <see cref="Listing(IEnumerable{ValueTuple{string, string}})"/> then reads, and a character save's SHA512 is the game's own
/// integrity check, read as the game writes it.
/// </summary>
public static class FileHash
{
    /// <summary>The SHA256 of the file at <paramref name="path"/>.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256(stream);
    }

    /// <summary>The SHA256 of the rest of <paramref name="content"/>.</summary>
    public static string Sha256(Stream content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>The SHA256 of content in memory (a file's bytes before or after it is written, or a digest's input).</summary>
    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>The SHA256 of the file at <paramref name="path"/>, read asynchronously.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellation = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
    }

    /// <summary>
    /// A plugin file's MD5 as ValheimCLI reports and compares it (<c>cli_manifest</c>, <c>cli_expect</c>): 32 lower-case hex
    /// characters. MD5 stays for plugin pins because the game side computes and compares MD5; a plan's plugin pin must be the
    /// value the game reports. It identifies a build; it is not a security check.
    /// </summary>
    public static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    /// <summary>Finder's <c>.DS_Store</c> and AppleDouble <c>._*</c> files, which copying through macOS adds; never part of a listing.</summary>
    public static bool IsMacMetadata(string path)
    {
        string name = Path.GetFileName(path);
        return name == ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every regular file under <paramref name="root"/>, depth first. A link anywhere, the root included, is refused, as a
    /// host's listing refuses it. <paramref name="directories"/>, when given, receives every directory walked, the root first.
    /// </summary>
    internal static IEnumerable<string> Files(string root, ICollection<string>? directories = null)
    {
        RefuseLink(root);
        directories?.Add(root);
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            RefuseLink(path);
            if (Directory.Exists(path)) { foreach (string child in Files(path, directories)) yield return child; }
            else yield return path;
        }
    }

    /// <summary>
    /// The entries under <paramref name="root"/> that <paramref name="pattern"/> names (relative, <c>/</c> separators, <c>*</c>
    /// and <c>?</c> allowed in a segment), walked one segment at a time as a host's listing walks a pattern
    /// (<c>HostInstall.ListAsync</c>): a linked directory on the way to the named entry is refused, never entered, and a named
    /// entry that is a link is refused whether or not its target exists (<see cref="RefuseLink"/>). A link on the way that leads
    /// nowhere has nothing to enter and names nothing. The root itself may be a link, such as <c>/tmp</c>: the rule applies below it.
    /// </summary>
    internal static IReadOnlyList<string> Named(string root, string pattern)
    {
        var current = new List<string> { root };
        string[] segments = pattern.Split('/');
        for (int i = 0; i < segments.Length && current.Count != 0; i++)
        {
            bool last = i == segments.Length - 1;
            var next = new List<string>();
            foreach (string directory in current)
            {
                IEnumerable<string> candidates = segments[i].IndexOfAny(['*', '?']) >= 0
                    ? Directory.EnumerateFileSystemEntries(directory, segments[i]).Order(StringComparer.Ordinal)
                    : [Path.Combine(directory, segments[i])];
                foreach (string candidate in candidates)
                {
                    if (last) { RefuseLink(candidate); if (File.Exists(candidate) || Directory.Exists(candidate)) next.Add(candidate); }
                    else if (!IsLink(candidate)) { if (Directory.Exists(candidate)) next.Add(candidate); }
                    else if (LeadsToDirectory(candidate)) throw new IOException("Links are unsupported: " + candidate);
                }
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Refuses <paramref name="path"/> when it is a link (any reparse point, a symbolic link included, whether or not its
    /// target exists), as a host's listing refuses one; a pinned file or folder is hashed as itself, never through a link.
    /// </summary>
    internal static void RefuseLink(string path)
    {
        if (IsLink(path)) throw new IOException("Links are unsupported: " + path);
    }

    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget != null || ((info.Exists || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
    }

    // Whether a link on the way to a named path leads to a directory, so a walk would enter it, as this platform's host listing
    // decides it (bash's -d, PowerShell's open on Windows): a dangling link or one to a file leads nowhere to enter. A reparse
    // point that is not a name link, or a link whose target cannot be followed (a loop), is a directory when the system says so
    // (on Windows a junction always is, so it is refused as the listing refuses an unlistable one; a looping symlink elsewhere is not).
    private static bool LeadsToDirectory(string link)
    {
        try { return new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true) is { } target ? target.Exists : Directory.Exists(link); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Directory.Exists(link); }
    }

    /// <summary>
    /// The listing hash of <paramref name="files"/> under <paramref name="root"/>: the SHA256 of one line per file,
    /// <c>&lt;sha256&gt;  &lt;relative path&gt;\n</c> with <c>/</c> separators, ordered by path (ordinal), as
    /// <c>sha256sum</c> prints it. <see cref="IsMacMetadata"/> files are left out; no files hash the empty listing.
    /// </summary>
    internal static string Listing(string root, IEnumerable<string> files) =>
        Listing(files.Where(path => !IsMacMetadata(path))
            .Select(path => (Relative: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Sha256: Sha256(path))));

    /// <summary>The listing hash of files already hashed (a host's listing, a package manifest): relative paths with <c>/</c> and their SHA256.</summary>
    internal static string Listing(IEnumerable<(string Relative, string Sha256)> files)
    {
        var listing = new StringBuilder();
        foreach (var (relative, sha256) in files.Where(file => !IsMacMetadata(file.Relative)).OrderBy(file => file.Relative, StringComparer.Ordinal))
            listing.Append(sha256.ToLowerInvariant()).Append("  ").Append(relative).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(listing.ToString()));
    }
}
