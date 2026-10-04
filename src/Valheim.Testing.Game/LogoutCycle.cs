using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// The local player's custom data and profile, read through the adapter's <c>PlayerCustomData.Command()</c> (Valheim.Testing.Adapter).
/// <see cref="Character"/> is the profile's name, <see cref="ProfileFile"/> its file name without <c>.fch</c> and
/// <see cref="FileSource"/> where the game saves it (<c>Local</c> or <c>Cloud</c>). <see cref="Data"/> is the reading as returned.
/// </summary>
public sealed record CustomDataReading(string Character, string ProfileFile, string FileSource, string ProfilePath, IReadOnlyDictionary<string, string> Values, JsonElement Data)
{
    public const string Source = "local-player-custom-data";

    /// <summary>Reads the custom data through <paramref name="capabilityPath"/>, only the keys starting with <paramref name="keyPrefix"/> if given.</summary>
    public static CustomDataReading Read(GameActor client, string capabilityPath, string? keyPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (keyPrefix != null && (keyPrefix.Length == 0 || keyPrefix.Any(char.IsWhiteSpace))) throw new ArgumentException("A key prefix is one token.", nameof(keyPrefix));
        var capability = client.RequireCapability(capabilityPath);
        var observation = keyPrefix == null ? client.ObserveComplete(capability, Source) : client.ObserveComplete(capability, Source, keyPrefix);
        return Parse(observation.Data);
    }

    /// <summary>Parses a reading; an incomplete one, or one listing a key twice, throws rather than reading as "no data".</summary>
    public static CustomDataReading Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != Source ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Not a complete custom data reading: the local player has not spawned, or this is another observation.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in data.GetProperty("entries").EnumerateArray())
        {
            string key = entry.GetProperty("key").GetString() ?? throw new InvalidOperationException("A custom data entry without a key.");
            if (!values.TryAdd(key, entry.GetProperty("value").GetString() ?? "")) throw new InvalidOperationException("The custom data reading lists " + key + " twice.");
        }
        return new(Text(data, "character"), Text(data, "profileFile"), Text(data, "fileSource"), Text(data, "profilePath"), values, data.Clone());
    }

    private static string Text(JsonElement data, string name) =>
        data.GetProperty(name).GetString() is { Length: > 0 } text ? text : throw new InvalidOperationException("Custom data reading without " + name + ".");
}

/// <summary>A profile file as the runner sees it: whether it exists, its length, SHA256 and last write time.</summary>
public sealed record ProfileFileState(string Path, bool Exists, long Length, string? Sha256, DateTime? LastWriteUtc)
{
    public static ProfileFileState Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists) return new(path, false, 0, null, null);
        // The game replaces the file by renaming a new one over it, so a read can meet the moment it is absent.
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            info.Refresh();
            return new(path, true, info.Length, hash, info.LastWriteTimeUtc);
        }
        catch (FileNotFoundException) { return new(path, false, 0, null, null); }
    }
    public override string ToString() => Exists ? $"{Path} ({Length} bytes, SHA256 {Sha256}, written {LastWriteUtc:O})" : Path + " (absent)";
}

/// <summary>What <see cref="LogoutCycle"/> observed, in order.</summary>
/// <param name="OldAfter">The game's backup of the previous file (<c>.fch.old</c>) after the logout: the replaced copy.</param>
public sealed record LogoutResult(CustomDataReading Before, ProfileFileState FileBefore, ProfileFileState FileAfter, ProfileFileState OldAfter, TimeSpan WriteSeen, CustomDataReading After)
{
    /// <summary>Whether the backup the logout left is the file as it was before: one save replaced it, not several.</summary>
    public bool OldMatchesBefore => OldAfter.Exists && OldAfter.Sha256 == FileBefore.Sha256;
}

/// <summary>
/// A logout and the next login, observed: the character file is rewritten when the player leaves to the menu, and the
/// mod's custom data comes back when the character joins again. Mods that keep state in the player profile
/// (<c>Player.m_customData</c>) lose it when the file is not written, for example when another mod removes every Harmony
/// patch at quit or a server-character mod has no time to finish.
/// <para>
/// In 1.0.16 the save happens inside the logout itself, before the menu loads: <c>Game.Logout</c> shuts the game session
/// down, which saves the player into its profile and writes the profile (<c>PlayerProfile.Save</c>): a new file
/// <c>&lt;name&gt;.fch.new</c>, the previous file moved to <c>&lt;name&gt;.fch.old</c> and the new one renamed to
/// <c>&lt;name&gt;.fch</c>. ValheimCLI's session leave issues that logout and replies once the menu is up, so the new file is
/// normally there when the leave returns; the runner still waits up to <see cref="WriteTimeout"/> for its hash to change,
/// woken by file events, because it may see the client's disk late. The game skips the save entirely when its save system
/// blocks character saves (logged "Character save blocked") and when the disk is full.
/// </para>
/// <para>
/// <see cref="Run(ClientRound, ClientRunPlan, string, CancellationToken, string)"/> reads the <see cref="Keys"/> (each must be set),
/// hashes the profile file, has the client leave to its menu, waits for a different hash, joins again with the plan's
/// character (protected, as <see cref="ClientRounds"/> joins) and requires every key back with its value. Only a disposable
/// local character: the game must report it saved <c>Local</c>, and <see cref="CharactersDirectory"/> must be the game's
/// local character folder, <c>characters_local</c>, never a cloud copy. The client is joined again when it returns.
/// </para>
/// <para>
/// An unchanged hash is all the runner can see of a save that rewrote identical bytes; a moved player, a longer play time
/// or a changed value makes the file differ. Another save in the window (a world save asks every client to save its
/// profile) is indistinguishable from the logout's own; <see cref="LogoutResult.OldMatchesBefore"/> is false then.
/// </para>
/// </summary>
public sealed class LogoutCycle
{
    /// <summary>The adapter's custom data observation, for example <c>mymod.testing/custom-data</c>.</summary>
    public required string Capability { get; init; }
    /// <summary>The custom data keys that must come back: at least one, all set before the logout.</summary>
    public required IReadOnlyList<string> Keys { get; init; }
    /// <summary>
    /// The client's local character folder as this runner reads it (the game's <c>characters_local</c> under its save
    /// root), for example the owned client's save root plus <c>characters_local</c>. The file is its <c>&lt;profile file&gt;.fch</c>.
    /// </summary>
    public required string CharactersDirectory { get; init; }
    /// <summary>How long after the leave returns the file's hash may take to change.</summary>
    public required TimeSpan WriteTimeout { get; init; }
    /// <summary>Only the custom data keys starting with this are read (one token); null reads them all.</summary>
    public string? KeyPrefix { get; init; }
    /// <summary>How often the file is re-read when no file event arrives (some filesystems raise none).</summary>
    public TimeSpan RereadInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the cycle within a <see cref="ClientRounds"/> measurement: each part is a <c>{round}: ...</c> step and the
    /// readings are written to <c>{round}-{evidence}.json</c> (default <c>logout</c>) whether it passes or fails.
    /// <paramref name="plan"/> and <paramref name="worldUid"/> are the ones the rounds use, for the join.
    /// </summary>
    public LogoutResult Run(ClientRound round, ClientRunPlan plan, string worldUid, CancellationToken cancellation = default, string evidence = "logout")
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(plan);
        if (!ScenarioReport.ValidKind(evidence)) throw new ArgumentException("Name the evidence with 1-40 lower-case letters, digits or hyphens.", nameof(evidence));
        Validate();
        CustomDataReading? before = null, after = null;
        ProfileFileState? fileBefore = null, fileAfter = null, oldAfter = null;
        TimeSpan? writeSeen = null;
        void Write() => round.Write(evidence, new
        {
            keys = Keys, keyPrefix = KeyPrefix, before = before?.Data, fileBefore, fileAfter, oldAfter, writeSeenSeconds = writeSeen?.TotalSeconds,
            oldMatchesBefore = oldAfter == null || fileBefore == null ? (bool?)null : oldAfter.Exists && oldAfter.Sha256 == fileBefore.Sha256, after = after?.Data,
        });
        try
        {
            round.Step("the custom data is set and the profile file hashed before the logout", () => (before, fileBefore) = Prepare(round.Client, plan));
            round.Step("the client leaves to its menu and the profile file is rewritten", () =>
            {
                Leave(round.Client, plan);
                (fileAfter, oldAfter, writeSeen) = WaitForWrite(fileBefore!, cancellation);
            });
            round.Step("join again with the same character, protected", () => Rejoin(round.Client, plan, worldUid, cancellation));
            round.Step("the custom data came back", () => after = Reloaded(round.Client, before!));
        }
        catch
        {
            try { Write(); } catch (IOException) { } // The failure on its way out is the one to report.
            throw;
        }
        Write();
        return new(before!, fileBefore!, fileAfter!, oldAfter!, writeSeen!.Value, after!);
    }

    /// <summary>The same cycle outside <see cref="ClientRounds"/>: <paramref name="client"/> is joined to <paramref name="worldUid"/> with <paramref name="plan"/>'s character.</summary>
    public LogoutResult Run(GameActor client, ClientRunPlan plan, string worldUid, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);
        Validate();
        var (before, fileBefore) = Prepare(client, plan);
        Leave(client, plan);
        var (fileAfter, oldAfter, writeSeen) = WaitForWrite(fileBefore, cancellation);
        Rejoin(client, plan, worldUid, cancellation);
        return new(before, fileBefore, fileAfter, oldAfter, writeSeen, Reloaded(client, before));
    }

    /// <summary>The profile file of <paramref name="reading"/>'s character in <see cref="CharactersDirectory"/>.</summary>
    public string ProfilePath(CustomDataReading reading) => Path.Combine(CharactersDirectory, (reading ?? throw new ArgumentNullException(nameof(reading))).ProfileFile + ".fch");

    private (CustomDataReading, ProfileFileState) Prepare(GameActor client, ClientRunPlan plan)
    {
        var reading = CustomDataReading.Read(client, Capability, KeyPrefix);
        if (reading.FileSource != "Local")
            throw new InvalidOperationException($"The joined character {reading.Character} is saved to {reading.FileSource}, not Local: use a disposable local character, never a cloud one. Nothing was changed.");
        if (!string.Equals(reading.Character, plan.Character, StringComparison.OrdinalIgnoreCase) && !string.Equals(reading.ProfileFile, plan.Character, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The joined character is {reading.Character} ({reading.ProfileFile}.fch), not the plan's {plan.Character}. Nothing was changed.");
        var unset = Keys.Where(key => !reading.Values.ContainsKey(key)).ToArray();
        if (unset.Length != 0)
            throw new InvalidOperationException($"Custom data key(s) {string.Join(", ", unset)} are not set before the logout, so their return would prove nothing. Set them first. Nothing was changed.");
        var file = ProfileFileState.Read(ProfilePath(reading));
        if (!file.Exists) throw new InvalidOperationException($"The character's profile file {file.Path} does not exist; check CharactersDirectory. Nothing was changed.");
        return (reading, file);
    }

    private static void Leave(GameActor client, ClientRunPlan plan)
    {
        new SessionControl(client).Leave(); // Exactly once; the game writes the profile inside the logout.
        client.VerifyEnvironment(plan.MenuExpectations); // A transition always needs fresh pins.
    }

    private (ProfileFileState After, ProfileFileState Old, TimeSpan Seen) WaitForWrite(ProfileFileState before, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        string name = Path.GetFileName(before.Path);
        using var changed = new ChangeSignal(Path.GetDirectoryName(before.Path)!);
        var last = ProfileFileState.Read(before.Path);
        while (!(last.Exists && last.Sha256 != before.Sha256))
        {
            var remaining = WriteTimeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new WaitTimeoutException($"the character file {name} to be rewritten by the logout (its SHA256 to differ from {before.Sha256})", clock.Elapsed,
                    last + ". The game did not save the character on logout: a mod may have blocked or broken the save (look for \"Character save blocked\" or an exception at logout in the client's log)");
            changed.Wait(remaining < RereadInterval ? remaining : RereadInterval, cancellation);
            last = ProfileFileState.Read(before.Path);
        }
        return (last, ProfileFileState.Read(before.Path + ".old"), clock.Elapsed);
    }

    private static void Rejoin(GameActor client, ClientRunPlan plan, string worldUid, CancellationToken cancellation)
    {
        var session = new SessionControl(client);
        session.Join(plan.Join, plan.Character, plan.PasswordVariable); // Turns devcommands on first. Exactly once.
        client.VerifyEnvironment(plan.WorldExpectations(worldUid));
        session.WaitForWorld(worldUid, TimeSpan.FromSeconds(plan.JoinSeconds), cancellation); // Protects the player.
    }

    private CustomDataReading Reloaded(GameActor client, CustomDataReading before)
    {
        var after = CustomDataReading.Read(client, Capability, KeyPrefix);
        var lost = Keys.Where(key => !after.Values.TryGetValue(key, out var value) || value != before.Values[key])
            .Select(key => after.Values.TryGetValue(key, out var value) ? $"{key} is \"{value}\", was \"{before.Values[key]}\"" : $"{key} is gone (was \"{before.Values[key]}\")").ToArray();
        if (lost.Length != 0)
            throw new InvalidOperationException($"{lost.Length} of {Keys.Count} custom data key(s) did not come back after the logout and login of {after.Character}: {string.Join("; ", lost)}.");
        return after;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Capability) || Capability.Split('/').Length != 2) throw new ArgumentException("Capability: name the adapter's custom data observation, owner/name.");
        if (Keys is not { Count: > 0 } || Keys.Any(string.IsNullOrEmpty)) throw new ArgumentException("Keys: name at least one custom data key.");
        if (Keys.Distinct(StringComparer.Ordinal).Count() != Keys.Count) throw new ArgumentException("Keys: each key once.");
        if (KeyPrefix != null && Keys.Any(key => !key.StartsWith(KeyPrefix, StringComparison.Ordinal))) throw new ArgumentException("Keys: every key must start with KeyPrefix, or it is never read.");
        if (!Path.IsPathFullyQualified(CharactersDirectory ?? "")) throw new ArgumentException("CharactersDirectory: give the full path of the client's local character folder.");
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(CharactersDirectory!)), "characters_local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CharactersDirectory: the game keeps local characters in characters_local; a characters folder holds cloud characters, which a test must never use.");
        if (WriteTimeout <= TimeSpan.Zero) throw new ArgumentException("WriteTimeout: give the wait an explicit, positive timeout.");
        if (RereadInterval <= TimeSpan.Zero) throw new ArgumentException("RereadInterval: give a positive interval.");
    }
}

// Wakes a wait when anything in one directory is created, written or renamed. The semaphore is never disposed: on
// Windows a watcher callback already in flight still runs after the watcher's Dispose, and releasing a disposed
// semaphore throws on a thread-pool thread, which ends the test host. It holds no unmanaged resource while its
// wait handle is never asked for.
internal sealed class ChangeSignal : IDisposable
{
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly FileSystemWatcher _watcher;

    public ChangeSignal(string directory)
    {
        _watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Changed += Wake; _watcher.Created += Wake; _watcher.Renamed += Wake;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Waits for a change after the last wait, or for <paramref name="timeout"/>; true when a change woke it.</summary>
    public bool Wait(TimeSpan timeout, CancellationToken cancellation) => _changed.Wait(timeout, cancellation);

    // Also runs after Dispose, from a callback that was already in flight.
    internal void Wake(object? sender, FileSystemEventArgs e)
    {
        try { _changed.Release(); }
        catch (SemaphoreFullException) { /* A wake is already pending. */ }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= Wake; _watcher.Created -= Wake; _watcher.Renamed -= Wake;
        _watcher.Dispose();
    }
}
