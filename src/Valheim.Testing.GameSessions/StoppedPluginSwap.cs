using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

// The filesystem transaction used by the existing clean-stopped placement. Its profile already belongs to the run journal.
internal static class StoppedPluginSwap
{
    internal static void Apply(string loaderDirectory, string relative, string source, string oldSha256, string newSha256,
        string operation, Action admission, Action<JournalEntry> journal, CancellationToken cancellation)
    {
        if (!Guid.TryParseExact(operation, "N", out _) || Path.IsPathRooted(relative) ||
            !relative.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) ||
            relative.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw new InvalidDataException("A swap target must be one prepared plugin path.");
        string target = Path.Combine(loaderDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        string temporary = target + ".swap-" + operation + ".tmp";
        admission(); cancellation.ThrowIfCancellationRequested();
        journal(JournalEntry.Of("checkpoint-plugin-swap-intended",
            ("operation", operation), ("source", source), ("target", target), ("temporary", temporary),
            ("before", oldSha256), ("after", newSha256)));
        // Both paths are inside the already journalled profile; exact recovery removes even an interrupted temp copy.
        var before = WorldFixture.Manifest(Path.Combine(loaderDirectory, "BepInEx"));
        string key = Path.GetRelativePath(Path.Combine(loaderDirectory, "BepInEx"), target);
        if (!before.TryGetValue(key, out var actual) || actual != oldSha256 || FileHash.Sha256(source) != newSha256)
            throw new InvalidDataException("The stopped swap input changed before staging.");
        File.Copy(source, temporary);
        if (FileHash.Sha256(temporary) != newSha256 || FileHash.Sha256(source) != newSha256)
            throw new InvalidDataException("The source or staged build changed during copy.");
        var withTemporary = new Dictionary<string, string>(before, StringComparer.Ordinal)
        { [Path.GetRelativePath(Path.Combine(loaderDirectory, "BepInEx"), temporary)] = newSha256 };
        WorldFixture.Verify(Path.Combine(loaderDirectory, "BepInEx"), withTemporary);
        admission(); cancellation.ThrowIfCancellationRequested();
        journal(JournalEntry.Of("checkpoint-plugin-activation-intended",
            ("operation", operation), ("target", target), ("after", newSha256)));
        admission(); cancellation.ThrowIfCancellationRequested();
        WorldFixture.Verify(Path.Combine(loaderDirectory, "BepInEx"), withTemporary);
        if (FileHash.Sha256(source) != newSha256) throw new InvalidDataException("The source build changed before activation.");
        File.Move(temporary, target, overwrite: true); // Same directory/volume: one atomic name replacement while stopped.
        var after = new Dictionary<string, string>(before, StringComparer.Ordinal) { [key] = newSha256 };
        WorldFixture.Verify(Path.Combine(loaderDirectory, "BepInEx"), after);
        if (FileHash.Sha256(source) != newSha256) throw new InvalidDataException("The source build changed during activation.");
    }
}
