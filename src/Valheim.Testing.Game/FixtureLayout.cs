using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// Finds the one world a hosted fixture root holds, and when the root has another shape says which one and what to point
/// at instead: the world folder itself, a <c>worlds_local</c> wrapper, several worlds or none. Every refusal prints the
/// expected tree and what the root holds. <see cref="HostWorldPlan.Preflight"/> then checks the UID.
/// </summary>
public static class FixtureLayout
{
    /// <summary>The one-world tree a fixture root holds, as the error messages print it.</summary>
    public const string ExpectedTree =
        "<fixture root>/\n" +
        "  <WorldName>/          one Valheim 1.0 chunked save, exactly as the game wrote it\n" +
        "    _main.<n>.fwl2\n" +
        "    _main.<n>.db2\n" +
        "    ...                 (its chunk files)\n" +
        "or, from an older save, <WorldName>.fwl and <WorldName>.db directly in <fixture root>. Nothing else.";

    private static readonly Regex Metadata = new(@"^_main\.\d+\.fwl2$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The identity of the one world in <paramref name="root"/>, refusing any other layout with the corrective action, the
    /// expected tree and the tree found; then refuses a world whose own metadata holds another UID than <paramref name="worldUid"/>.
    /// </summary>
    public static WorldIdentity Discover(string root, string worldUid)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw Refuse(root, $"fixture root {root} does not exist. Point fixture.root at the directory that holds the one world folder.");
        var relative = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path)).ToList();
        try { HostedWorld.NameOf(relative); }
        catch (ArgumentException error) { throw Refuse(root, Diagnose(root, error.Message)); }
        var plan = new HostWorldPlan { World = new() { Source = root }, WorldUid = worldUid };
        return plan.Preflight();
    }

    // What the root is instead of a one-world fixture, and what to do about it.
    private static string Diagnose(string root, string problem)
    {
        var files = Directory.EnumerateFiles(root).Select(Path.GetFileName).OfType<string>().ToList();
        var directories = Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().ToList();
        string parent = Path.GetDirectoryName(root) ?? root;
        if (files.Any(file => Metadata.IsMatch(file)))
            return $"{root} is a world folder itself (it holds {files.First(file => Metadata.IsMatch(file))}), not the root that contains it. " +
                $"Point fixture.root at its parent {parent} if that holds nothing else, or copy {Path.GetFileName(root)}/ into a new empty directory and point at that.";
        if (directories.FirstOrDefault(name => name.Equals("worlds_local", StringComparison.OrdinalIgnoreCase)) is { } wrapper)
            return $"{root} holds a {wrapper}/ wrapper, as a copied save directory does. Copy only the one world folder from {Path.Combine(root, wrapper)} into a new empty directory and point fixture.root at that.";
        var worlds = directories.Where(name => Directory.EnumerateFiles(Path.Combine(root, name)).Any(file => Metadata.IsMatch(Path.GetFileName(file))))
            .Concat(files.Where(file => file.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase)).Select(file => file[..^4])).Order(StringComparer.Ordinal).ToList();
        if (worlds.Count > 1)
            return $"{root} holds {worlds.Count} worlds ({string.Join(", ", worlds)}). Copy only the world under test into a new empty directory and point fixture.root at that.";
        return $"{root} is not a one-world fixture: {problem} Copy the world exactly as the game saved it.";
    }

    private static InvalidOperationException Refuse(string root, string message) =>
        new($"The fixture is not one world: {message}\nExpected:\n{ExpectedTree}\nFound:\n{Tree(root)}");

    /// <summary>The first two levels under <paramref name="root"/>, at most a few entries per folder, for a message.</summary>
    internal static string Tree(string root)
    {
        if (!Directory.Exists(root)) return "  (nothing: the directory does not exist)";
        var text = new StringBuilder();
        text.Append(Path.GetFileName(Path.TrimEndingDirectorySeparator(root))).Append("/\n");
        void List(string directory, string indent, int depth)
        {
            var entries = Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).ToList();
            foreach (string entry in entries.Take(8))
            {
                bool folder = Directory.Exists(entry);
                text.Append(indent).Append(Path.GetFileName(entry)).Append(folder ? "/" : "").Append('\n');
                if (folder && depth < 2) List(entry, indent + "  ", depth + 1);
            }
            if (entries.Count > 8) text.Append(indent).Append($"... and {entries.Count - 8} more\n");
            if (entries.Count == 0) text.Append(indent).Append("(empty)\n");
        }
        List(root, "  ", 1);
        return text.ToString().TrimEnd('\n');
    }
}
