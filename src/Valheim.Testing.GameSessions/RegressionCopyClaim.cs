using System.Text.Json;

namespace Valheim.Testing.GameSessions;

// A sibling claim is written after copy-intended and before the first byte of a plain start's
// game copy. The complete install gets its own marker later. A killed mid-copy run can therefore
// retire an unmarked partial directory only when this exact run claimed it first.
internal static class RegressionCopyClaim
{
    private sealed record Claim(string Tool, string Run, string Install);

    internal static string PathFor(string install) => Path.GetFullPath(install) + ".vt-claim.json";

    internal static void Create(string install, string run)
    {
        install = Path.GetFullPath(install);
        if (Directory.Exists(install)) throw new IOException("The disposable install already exists: " + install);
        string path = PathFor(install);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bool created = false;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            JsonSerializer.Serialize(file, new Claim(nameof(RegressionCopyClaim), run, install));
            file.Flush(flushToDisk: true);
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
    }

    internal static void Require(string install, string run)
    {
        install = Path.GetFullPath(install);
        string path = PathFor(install);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The partial game copy has no ordinary run claim: " + install);
        Claim? claim;
        try { claim = JsonSerializer.Deserialize<Claim>(File.ReadAllText(path)); }
        catch (JsonException error) { throw new InvalidDataException("The partial game copy has an unreadable run claim: " + install, error); }
        if (claim is not { Tool: nameof(RegressionCopyClaim) } || claim.Run != run || claim.Install != install)
            throw new InvalidDataException("The partial game copy's run claim does not match its journal: " + install);
    }

    internal static void Retire(string install, string run)
    {
        Require(install, run);
        File.Delete(PathFor(install));
    }
}
