using valheimCLI;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>Uses ValheimCLI's parser and always emits its strict expectation command.</summary>
public static class StrictExpectations
{
    public static string Load(string path)
    {
        if (!PlanExpectations.TryLoad(path, strict: true, out string command, out string error))
            throw new ArgumentException(error);
        return Normalize(command);
    }
    public static string Normalize(string command) => Expectations.ExpectCommand(Parse(command), strict: true);

    /// <summary>Explicit expected plugin transition; never learns an expected hash from the running game.</summary>
    public static string WithPlugin(string command, string guid, string hashOrAbsent)
    {
        if (Expectations.IsWorldKey(guid) || !Expectations.TryParse(guid + "=" + hashOrAbsent, 0, out var replacement, out var error) ||
            replacement.Value == Expectations.Any)
            throw new ArgumentException("Supply a plugin GUID and hash or absent.");
        var entries = Parse(command).Where(e => !string.Equals(e.Key, guid, StringComparison.OrdinalIgnoreCase)).ToList();
        entries.Add(replacement);
        return Expectations.ExpectCommand(entries, strict: true);
    }
    private static List<Expectation> Parse(string command)
    {
        if (command == null || command.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("Supply one cli_expect command.");
        string[] tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || tokens[0] != "cli_expect") throw new ArgumentException("Supply explicit cli_expect pins.");
        var pins = tokens.Skip(1).Where(t => t != "--strict").ToArray();
        if (pins.Length == 0) throw new ArgumentException("At least one expectation is required.");
        foreach (string token in pins)
            if (!Expectations.TryParse(token, 0, out _, out string error)) throw new ArgumentException(error);
        var errors = new List<string>(); var entries = Expectations.ParseLines(pins, errors);
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors));
        return entries;
    }
}
