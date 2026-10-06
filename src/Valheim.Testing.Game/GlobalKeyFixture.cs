using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>How a client's global keys differ from the server's: keys it lacks and keys only it has.</summary>
[ResultShape]
public sealed record GlobalKeyDifference(IReadOnlyList<string> MissingOnClient, IReadOnlyList<string> ExtraOnClient)
{
    public bool Same => MissingOnClient.Count == 0 && ExtraOnClient.Count == 0;
    public override string ToString() => Same ? "the same keys" :
        $"client lacks [{string.Join(", ", MissingOnClient)}]; client has extra [{string.Join(", ", ExtraOnClient)}]";
}

/// <summary>
/// Puts a world into a known progression state: sets and removes global keys (boss progress, events, world modifiers) on
/// the server and waits until a joined client reports the same set, through an adapter's <c>GlobalKeyCommands</c>
/// (Valheim.Testing.Adapter): <c>GlobalKeyCommands.List()</c> on both sides and <c>GlobalKeyCommands.Change(...)</c> on the
/// server. In Valheim 1.0.16 the server sends its whole list to every client after each change and the client replaces
/// its own list with it, so the client's set must equal the server's exactly. Keys are lower case, <c>name</c> or
/// <c>name value</c>. Each change is issued once; only the read-only lists are re-read. ValheimCLI's
/// <c>cli_check_global_key</c> answers for one key at a time, so it cannot show a key the client has and the server does
/// not; the listing can.
/// </summary>
public static class GlobalKeyFixture
{
    public const string Source = "global-keys", ChangeSource = "global-key-change";

    /// <summary>The keys <paramref name="actor"/>'s process holds, read through <paramref name="listPath"/> (for example <c>mymod.testing/globalkeys</c>).</summary>
    public static IReadOnlyList<string> Read(GameActor actor, string listPath)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Keys(actor.ObserveComplete(actor.RequireCapability(listPath), Source).Data);
    }

    /// <summary>
    /// Sets <paramref name="name"/> (with <paramref name="value"/>, if given) on the server, once, and returns the server's
    /// keys after it. Throws if the server's reply does not hold the key with that value.
    /// </summary>
    public static IReadOnlyList<string> Set(GameActor server, string changePath, string name, string? value = null)
    {
        string key = KeyName(name);
        if (value != null && (value.Length == 0 || value.Any(char.IsWhiteSpace))) throw new ArgumentException("A key's value is one word.", nameof(value));
        var keys = Change(server, changePath, value == null ? new[] { "set", key } : new[] { "set", key, value });
        string line = value == null ? key : key + " " + value.ToLowerInvariant(); // The game lower-cases the whole line.
        if (!keys.Contains(line, StringComparer.Ordinal))
            throw new InvalidOperationException($"The server did not keep global key \"{line}\"; its keys: [{string.Join(", ", keys)}].");
        return keys;
    }

    /// <summary>Removes <paramref name="name"/>, whatever its value, on the server, once, and returns the server's keys after it.</summary>
    public static IReadOnlyList<string> Remove(GameActor server, string changePath, string name)
    {
        string key = KeyName(name);
        var keys = Change(server, changePath, new[] { "remove", key });
        if (keys.Any(line => NameOf(line) == key))
            throw new InvalidOperationException($"The server still has global key \"{key}\"; its keys: [{string.Join(", ", keys)}].");
        return keys;
    }

    /// <summary>Compares two key sets exactly (ordinal; the game stores keys lower case).</summary>
    public static GlobalKeyDifference Compare(IEnumerable<string> server, IEnumerable<string> client)
    {
        var s = server.ToHashSet(StringComparer.Ordinal);
        var c = client.ToHashSet(StringComparer.Ordinal);
        return new([.. s.Except(c).Order(StringComparer.Ordinal)], [.. c.Except(s).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Waits until the client lists exactly the server's keys and returns them. Both lists are re-read each time, at most
    /// <paramref name="interval"/> apart, or sooner when <paramref name="changed"/> completes (for example the client's
    /// "client got keys" log line through a <see cref="LogWait"/> opened before the change). Expiry throws
    /// <see cref="WaitTimeoutException"/> naming the keys that still differ.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WaitForClient(GameActor server, GameActor client, string listPath, TimeSpan timeout, TimeSpan interval,
        Func<TimeSpan, CancellationToken, Task>? changed = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        var both = await ObservedWait.UntilAsync("the client's global keys to equal the server's", () => (Server: Read(server, listPath), Client: Read(client, listPath)),
            keys => Compare(keys.Server, keys.Client).Same, timeout, interval, cancellation,
            describe: keys => Compare(keys.Server, keys.Client).ToString(), changed: changed).ConfigureAwait(false);
        return both.Server;
    }

    /// <summary>
    /// The fixture action: sets each of <paramref name="set"/> (<c>name</c> or <c>name value</c>) and removes each of
    /// <paramref name="remove"/> on the server, each once and in that order, then waits until the client reports the
    /// server's set (<see cref="WaitForClient"/>). Returns the keys both sides hold.
    /// </summary>
    public static async Task<IReadOnlyList<string>> Apply(GameActor server, GameActor client, string changePath, string listPath,
        IEnumerable<string> set, IEnumerable<string> remove, TimeSpan timeout, TimeSpan interval,
        Func<TimeSpan, CancellationToken, Task>? changed = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(remove);
        string[] adding = [.. set], removing = [.. remove];
        if (adding.Length + removing.Length == 0) throw new ArgumentException("Name at least one key to set or remove.");
        foreach (string line in adding)
        {
            string[] words = (line ?? "").Split(' ');
            if (words.Length > 2) throw new ArgumentException($"\"{line}\" is not a key: use \"name\" or \"name value\".", nameof(set));
            Set(server, changePath, words[0], words.Length == 2 ? words[1] : null);
        }
        foreach (string name in removing) Remove(server, changePath, name);
        return await WaitForClient(server, client, listPath, timeout, interval, changed, cancellation).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Change(GameActor server, string changePath, string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(server);
        var capability = server.RequireCapability(changePath);
        if (capability.ReadOnly) throw new InvalidOperationException(changePath + " is read-only; name the adapter's GlobalKeyCommands.Change command.");
        var data = server.Invoke(capability, arguments);
        if (!data.TryGetProperty("source", out var source) || source.GetString() != ChangeSource ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True ||
            data.GetProperty("action").GetString() != arguments[0] || data.GetProperty("name").GetString() != arguments[1])
            throw new InvalidOperationException("Not a complete global key change for " + string.Join(" ", arguments) + ".");
        return Keys(data);
    }

    private static IReadOnlyList<string> Keys(JsonElement data)
    {
        if (!data.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("A global key list without keys.");
        return [.. keys.EnumerateArray().Select(key => key.GetString() is { Length: > 0 } text ? text : throw new InvalidOperationException("A global key list with an empty or non-string key."))];
    }

    private static string KeyName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Any(char.IsWhiteSpace)) throw new ArgumentException("A key's name is one word.", nameof(name));
        return name.ToLowerInvariant();
    }

    private static string NameOf(string line) => line.IndexOf(' ') is var space and > 0 ? line[..space] : line;
}
