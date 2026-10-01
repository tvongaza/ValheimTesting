using valheimCLI;

namespace Valheim.Testing.Game;

/// <summary>
/// ValheimCLI's standing expectations file (its <c>[Expectations] File</c> setting): one <c>key=value</c> pin per line, which
/// the game checks before every command but its diagnostics. <see cref="Format"/> writes one, <see cref="Parse"/> and
/// <see cref="Read"/> check one with ValheimCLI's own parser before a launch, so a malformed line fails here rather than
/// as a refusal of every command in the game. Written by a script, eight pins once ended up on one space-separated line
/// (a PowerShell array expanded into a string); that line is named as such.
/// </summary>
public static class StandingPins
{
    /// <summary>
    /// The file's text: each pin on its own line, in the order given, ending with a newline. Every pin must parse as
    /// ValheimCLI parses a file line (a plugin GUID, name or file name with an MD5, <c>any</c> or <c>absent</c>; <c>world</c>,
    /// <c>seed</c>, <c>worlduid</c> or <c>worldfiles</c>), and each key appears once.
    /// </summary>
    public static string Format(IEnumerable<KeyValuePair<string, string>> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var lines = pins.Select(pin => pin.Key + "=" + pin.Value).ToList();
        if (lines.Count == 0) throw new ArgumentException("A standing expectations file needs at least one pin.", nameof(pins));
        foreach (string line in lines)
            if (line.IndexOfAny(['\r', '\n', '#']) >= 0) throw new ArgumentException($"A pin is one key=value without line breaks or #: \"{line}\".", nameof(pins));
        _ = Parse(lines, "the pins");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Writes <see cref="Format"/>'s text to <paramref name="path"/> (UTF-8 without a byte order mark).</summary>
    public static void Write(string path, IEnumerable<KeyValuePair<string, string>> pins) =>
        File.WriteAllText(path, Format(pins), new System.Text.UTF8Encoding(false));

    /// <summary><see cref="Parse"/> of the file at <paramref name="path"/>.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Read(string path) => Parse(File.ReadAllLines(path), path);

    /// <summary>
    /// The pins in a file's lines, as ValheimCLI reads them (blank lines and <c># comments</c> skipped). Refuses the whole file,
    /// naming every bad line from <paramref name="source"/>, when a line is not one <c>key=value</c>, a value does not fit its
    /// key, a key repeats, or it holds no pin: ValheimCLI refuses every command while any of these holds.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(IEnumerable<string> lines, string source)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var all = lines.ToList();
        var errors = new List<string>();
        var parsed = Expectations.ParseLines(all, errors);
        for (int i = 0; i < errors.Count; i++)
            if (Number(errors[i]) is int number && number <= all.Count && Joined(all[number - 1]) is int count and > 1)
                errors[i] = $"line {number} holds {count} key=value pins separated by spaces, but ValheimCLI reads one pin per line (StandingPins.Format writes that)";
        if (errors.Count == 0 && parsed.Count == 0) errors.Add("no pin");
        if (errors.Count != 0) throw new ArgumentException($"{source}: {string.Join("; ", errors)}.");
        return parsed.Select(pin => new KeyValuePair<string, string>(pin.Key, pin.Value)).ToList();
    }

    // ParseLines names a line as "line <n>: ...".
    private static int? Number(string error) =>
        error.StartsWith("line ", StringComparison.Ordinal) && int.TryParse(error.AsSpan(5, Math.Max(0, error.IndexOf(':') - 5)), out int number) ? number : null;
    // How many key=value tokens a line holds once its comment is gone, as ValheimCLI strips it (# at the start or after whitespace).
    private static int Joined(string line)
    {
        for (int i = 0; i < line.Length; i++)
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) { line = line[..i]; break; }
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(token => token.IndexOf('=') > 0) ? tokens.Length : 0;
    }
}
