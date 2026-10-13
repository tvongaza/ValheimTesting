using System.Text.Json;
using System.Text.Json.Nodes;

namespace Valheim.Testing.Game;

/// <summary>
/// The one owner of what makes a game image private evidence: when a PNG is complete (and its digest, taken from the same
/// bytes), whether the game's reply named the destination that was asked for, and the sidecar that records it. A local
/// client's screenshot (<see cref="OwnedScreenshot"/>) and a review still fetched from any host (<c>ReviewCapture</c>) both
/// go through it; fetching from a remote host is the only step that differs.
/// </summary>
internal static class ImageEvidence
{
    /// <summary>The largest image evidence accepts. A bigger file is never read, hashed or reported as complete.</summary>
    internal const long MaxBytes = 100_000_000;

    internal sealed record Verified(string Sha256, long Bytes);

    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];
    // The last chunk: length 0, "IEND" and its constant CRC.
    private static readonly byte[] PngTrailer = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];

    /// <summary>
    /// The digest and size of the PNG at <paramref name="path"/>, or null while it is missing, still held open by its writer,
    /// truncated or over <see cref="MaxBytes"/>. One shared-read handle supplies the check and the hash, so evidence never
    /// hashes a different set of bytes than it checked, and the image is streamed, never held in memory.
    /// </summary>
    internal static Verified? VerifyPng(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length < PngHeader.Length + PngTrailer.Length || file.Length > MaxBytes) return null;
            Span<byte> header = stackalloc byte[8];
            Span<byte> trailer = stackalloc byte[12];
            file.ReadExactly(header);
            file.Seek(-trailer.Length, SeekOrigin.End);
            file.ReadExactly(trailer);
            if (!header.SequenceEqual(PngHeader) || !trailer.SequenceEqual(PngTrailer)) return null;
            file.Seek(0, SeekOrigin.Begin);
            return new Verified(FileHash.Sha256(file), file.Length);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The destination a ValheimCLI capture reply names: the text after <c>path=</c> up to the reply's next field
    /// (<c> size=</c> or <c> bytes=</c>), without quotes. A path may contain spaces. Null when the reply names none.
    /// </summary>
    internal static string? ReportedPath(string? reply)
    {
        if (reply == null) return null;
        int start = reply.IndexOf("path=", StringComparison.Ordinal);
        if (start < 0) return null;
        string rest = reply[(start + "path=".Length)..];
        int end = rest.Length;
        foreach (string next in new[] { " size=", " bytes=" })
        {
            int at = rest.IndexOf(next, StringComparison.Ordinal);
            if (at >= 0 && at < end) end = at;
        }
        string path = rest[..end].Trim().Trim('"');
        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// Whether <paramref name="reply"/> names exactly <paramref name="expected"/>. The comparison is the destination host's:
    /// <paramref name="ignoreCase"/> for a Windows path. <paramref name="local"/> paths are compared fully qualified, so
    /// the spelling of separators does not matter; a remote host's path is another machine's and is compared as written.
    /// </summary>
    internal static bool NamesPath(string? reply, string expected, bool ignoreCase, bool local)
    {
        string? reported = ReportedPath(reply);
        if (reported == null) return false;
        if (local) { reported = Path.GetFullPath(reported); expected = Path.GetFullPath(expected); }
        return string.Equals(reported, expected, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Whether a path on this machine is compared without regard to case.</summary>
    internal static bool LocalIgnoresCase => OperatingSystem.IsWindows();

    /// <summary>
    /// Writes the sidecar: <c>schema</c>, <paramref name="kind"/>, then <paramref name="fields"/> in order, then
    /// <c>recordedUtc</c>. The file is new; an existing one is never replaced. Returns the sidecar's own SHA-256.
    /// </summary>
    internal static string WriteSidecar(string path, string kind, JsonObject fields)
    {
        var sidecar = new JsonObject { ["schema"] = 1, ["kind"] = kind };
        foreach (var (name, value) in fields) sidecar[name] = value?.DeepClone();
        sidecar["recordedUtc"] = DateTimeOffset.UtcNow;
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(file))
            writer.Write(sidecar.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return FileHash.Sha256(path);
    }
}
