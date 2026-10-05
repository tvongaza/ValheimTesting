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
    /// Refuses <paramref name="path"/> when it is a link (any reparse point, a symbolic link included, whether or not its
    /// target exists), as a host's listing refuses one; a pinned file or folder is hashed as itself, never through a link.
    /// </summary>
    internal static void RefuseLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null || ((info.Exists || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Links are unsupported: " + path);
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
