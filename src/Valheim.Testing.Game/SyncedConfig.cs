using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// One plugin config entry as a process holds it now. <see cref="Value"/> and <see cref="DefaultValue"/> are written as
/// BepInEx writes them to the config file (<c>true</c>, <c>5</c>, <c>1.5</c>). <see cref="Installed"/> is false when the
/// plugin is not loaded in that process, <see cref="Found"/> when it has no entry with that section and key.
/// </summary>
[ResultShape]
public sealed record ConfigValue(string Guid, string Section, string Key, bool Server, bool Installed, bool Found, string? Type, string? Value, string? DefaultValue)
{
    public override string ToString() =>
        !Installed ? $"{Guid} is not loaded" : !Found ? $"{Guid} has no entry [{Section}] {Key}" : $"[{Section}] {Key} = {Value} ({Type}, default {DefaultValue})";
}

/// <summary>
/// Reads a plugin's live config entry on a server or a client through an adapter's <c>ConfigEntryCommand</c>
/// (ValheimCLI Observe pack), so a test can compare the value each side uses after a join or an admin change. The
/// observation reads the <c>ConfigEntry</c> itself on each side; how the mod syncs it (ServerSync embedded in the mod,
/// Jötunn, its own RPC) is the mod's business, and a synced mod sets the client's entry. Values compare as the text BepInEx
/// would write to the file, whatever the entry's type. Nothing here writes config: the change under test is the mod's own
/// (an admin command, a server-side file edit).
/// </summary>
public static class SyncedConfig
{
    public const string Source = "bepinex-config";

    /// <summary>
    /// The entry <paramref name="section"/>/<paramref name="key"/> of plugin <paramref name="guid"/> in
    /// <paramref name="actor"/>'s process, through <paramref name="capabilityPath"/> (for example <c>valheim.observe/config</c>).
    /// A reply about another entry, or an incomplete one, throws.
    /// </summary>
    public static ConfigValue Read(GameActor actor, string capabilityPath, string guid, string section, string key)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var observation = actor.ObserveComplete(actor.RequireCapability(capabilityPath), Source, Token(guid), Token(section), Token(key));
        var value = Parse(observation.Data);
        if (value.Guid != guid || value.Section != section || value.Key != key)
            throw new InvalidOperationException($"Asked for {guid} [{section}] {key}; the reply is about {value.Guid} [{value.Section}] {value.Key}.");
        return value;
    }

    /// <summary>
    /// Percent-encodes one argument (<c>Enable Mod</c> becomes <c>Enable%20Mod</c>): extension arguments are single
    /// tokens, and BepInEx sections and keys may hold spaces. The adapter decodes it.
    /// </summary>
    public static string Token(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return Uri.EscapeDataString(text);
    }

    /// <summary>Parses a reply; anything malformed or incomplete throws rather than reading as a missing entry.</summary>
    public static ConfigValue Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != Source ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Not a complete config observation.");
        var value = new ConfigValue(Text(data, "guid")!, Text(data, "section")!, Text(data, "key")!, Flag(data, "server"), Flag(data, "installed"), Flag(data, "found"),
            Text(data, "type", optional: true), Text(data, "value", optional: true), Text(data, "defaultValue", optional: true));
        if (value.Found && (!value.Installed || value.Type == null || value.Value == null))
            throw new InvalidOperationException("A config observation that found an entry must name its plugin, type and value.");
        return value;
    }

    /// <summary>
    /// Waits until the entry reads <paramref name="expected"/> (the file's text form) in <paramref name="actor"/>'s process
    /// and returns it, re-reading at most <paramref name="interval"/> apart, or sooner when <paramref name="changed"/>
    /// completes. A plugin that is not loaded or an entry that does not exist fails at once (<see cref="WaitFailedException"/>):
    /// that value can never arrive. Expiry throws <see cref="WaitTimeoutException"/> with the last value read.
    /// </summary>
    public static Task<ConfigValue> WaitForValue(GameActor actor, string capabilityPath, string guid, string section, string key, string expected,
        TimeSpan timeout, TimeSpan interval, Func<TimeSpan, CancellationToken, Task>? changed = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ObservedWait.UntilAsync($"{actor.Name}'s {guid} [{section}] {key} to read {expected}", () => Read(actor, capabilityPath, guid, section, key),
            value => value.Value == expected, timeout, interval, cancellation,
            fails: value => !value.Installed ? $"{guid} is not loaded in {actor.Name}" : !value.Found ? $"{guid} has no config entry [{section}] {key} in {actor.Name}" : null,
            changed: changed);
    }

    /// <summary>
    /// Throws unless both sides have the entry with the same value and type, naming both: for example after a join, where
    /// a synced mod's client must use the server's value.
    /// </summary>
    public static void RequireSame(ConfigValue server, ConfigValue client)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        if (!server.Found || !client.Found || server.Value != client.Value || server.Type != client.Type)
            throw new InvalidOperationException($"Server and client differ: server {server}; client {client}.");
    }

    private static bool Flag(JsonElement data, string name) =>
        data.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.GetBoolean() : throw new InvalidOperationException("A config observation without " + name + ".");
    private static string? Text(JsonElement data, string name, bool optional = false) =>
        data.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()
        : optional && (!data.TryGetProperty(name, out text) || text.ValueKind == JsonValueKind.Null) ? null
        : throw new InvalidOperationException("A config observation without " + name + ".");
}
