using System.IO.Compression;

namespace Valheim.Testing.Game;

/// <summary>A ValheimCLI core-and-pack set ready to stage: its capability manifest, the folder holding its DLLs, and where it came from.</summary>
[ResultShape]
public sealed record CliBundleSource(string Manifest, string Files, string Origin);

/// <summary>
/// The pinned ValheimCLI plugin bundle the toolkit ships: one zip of the core, its packs, their capability manifest
/// (<c>cli-manifest.json</c>) and ValheimCLI's license, built once from the pinned commit and identified by its SHA-256.
/// Valheim.Testing.GameSessions embeds it with its pin (<c>cli-dependency.json</c>), so a disposable client copy and
/// <c>valheim-test</c> stage the same set.
/// <see cref="Extract"/> unpacks it once under ValheimTesting's own folder on this machine (<c>cli/&lt;commit&gt;</c>) and
/// reuses that copy while every file still matches the manifest, so a run needs no network and no plugins in the game.
/// </summary>
public static class CliBundle
{
    /// <summary>The manifest's file name inside a bundle.</summary>
    public const string ManifestFile = "cli-manifest.json";

    /// <summary>ValheimTesting's own folder on this machine (<c>%LOCALAPPDATA%\ValheimTesting</c>; macOS and Linux likewise).</summary>
    public static string DataRoot => new LocalSteamLocator().DataRoot;

    /// <summary>
    /// The bundle in <paramref name="zip"/>, which must be <paramref name="sha256"/>, as a verified folder under
    /// <paramref name="dataRoot"/> (default <see cref="DataRoot"/>)<c>/cli/&lt;commit&gt;</c>. An existing folder is reused only when
    /// it was extracted from this zip and every file its manifest names still has its hash; otherwise it is extracted again
    /// beside it and swapped in, one run at a time (<c>&lt;commit&gt;.lock</c> beside the folder), so concurrent runs share one copy
    /// and never replace a current one. A zip of another hash, or a manifest that does not match the files in the zip, is refused.
    /// </summary>
    public static CliBundleSource Extract(Stream zip, string sha256, string commit, string origin, string? dataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(zip);
        if (commit.Length is < 7 or > 40 || !commit.All(Uri.IsHexDigit)) throw new ArgumentException("Name the bundle's ValheimCLI commit.", nameof(commit));
        using var buffer = new MemoryStream();
        zip.CopyTo(buffer);
        string found = FileHash.Sha256(buffer.ToArray());
        if (!found.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The ValheimCLI bundle for {commit} is sha256 {found}, not the pinned {sha256.ToLowerInvariant()}; it is not the reviewed build.");
        string target = Path.Combine(dataRoot ?? DataRoot, "cli", commit);
        // What every file must be: the zip's own entries, flat. A copy is trusted only file by file against them, so an
        // edited manifest beside a swapped DLL is extracted again, not staged.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        buffer.Position = 0;
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains('/') || entry.FullName.Contains('\\') || entry.FullName is "" or "." or ".." || entry.FullName.StartsWith('.'))
                    throw new InvalidDataException($"The ValheimCLI bundle holds {entry.FullName}, which is not a file at its root.");
                using var content = entry.Open();
                expected[entry.FullName] = FileHash.Sha256(content);
            }
        // One extraction at a time for this folder, across processes (<commit>.lock beside it; ExtractOnce owns the swap).
        bool extracted = ExtractOnce.Ensure(target, folder => Current(folder, Path.Combine(folder, ".bundle-sha256"), found, expected), staging =>
        {
            buffer.Position = 0;
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in archive.Entries)
                {
                    // Flat files only (checked above): the DLLs, the manifest and the license, never a path.
                    Directory.CreateDirectory(staging);
                    entry.ExtractToFile(Path.Combine(staging, entry.FullName));
                }
            }
            if (!Intact(staging, expected)) throw new InvalidDataException("The ValheimCLI bundle's files do not match its own manifest.");
            File.WriteAllText(Path.Combine(staging, ".bundle-sha256"), found + "\n");
        }, "ValheimCLI bundle");
        return new CliBundleSource(Path.Combine(target, ManifestFile), target, origin + $" (sha256 {found}), {(extracted ? "extracted to" : "at")} {target}");
    }

    // A complete extraction of this very zip.
    private static bool Current(string target, string stamp, string sha256, IReadOnlyDictionary<string, string> expected) =>
        Directory.Exists(target) && File.Exists(stamp) && File.ReadAllText(stamp).Trim().Equals(sha256, StringComparison.Ordinal) && Intact(target, expected);

    // Every file of the zip is there with its hash, and every file the manifest names is one of them with the hash it gives.
    private static bool Intact(string folder, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var (name, hash) in expected)
            if (!File.Exists(Path.Combine(folder, name)) || !FileHash.Sha256(Path.Combine(folder, name)).Equals(hash, StringComparison.Ordinal)) return false;
        string manifestFile = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifestFile)) return false;
        CliCapabilityManifest manifest;
        try { manifest = CliCapabilityManifest.Read(manifestFile); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or ArgumentException) { return false; }
        return manifest.Files.Count != 0 && manifest.Files.All(file =>
            expected.TryGetValue(file.File, out string? hash) && hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase));
    }
}
