using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using valheimCLI;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one owner of "a ValheimCLI world dump becomes a pinned offline input". <see cref="CaptureAsync"/> runs the dump
/// through a pinned <see cref="GameActor"/>, checks the game's reply against the file it fetched, and writes
/// <c>&lt;name&gt;.csv</c> and its <see cref="WorldDumpManifest"/> as <c>&lt;name&gt;.json</c>. Offline tests load the pair
/// with <see cref="ReadManifest"/> and <see cref="WorldDumpManifest.Verify"/> or <see cref="LayeredDumpTerrain.Load"/>,
/// which check the file against the manifest; the reply grammar is read only here.
/// </summary>
public static class WorldDump
{
    private static readonly Regex AbsoluteHostPath = new(@"^([A-Za-z]:[\\/]|/)", RegexOptions.CultureInvariant);
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" }; // the same bytes on every OS

    /// <summary>
    /// Captures one dump. The actor must be pinned and allowed to run the cheat-gated dump command (see
    /// <see cref="TestAccess"/>); the dump runs within the actor's <see cref="GameActor.CommandTimeout"/>, so raise it for
    /// a large dump. The whole host directory is fetched, so give each capture a new, absolute one. Reads <c>cli_world</c> before and after the dump and refuses a change of world;
    /// runs <c>cli_world_dump &lt;step&gt; &lt;hostDirectory&gt;</c>, with <c>--window x,z,radius</c> when
    /// <paramref name="window"/> is given; requires one complete <c>OK: WORLD_DUMP</c> reply whose step, sample count,
    /// window (to the metre) and extent agree with the fetched file and whose file lies in <paramref name="hostDirectory"/>; then
    /// writes the CSV and manifest into <paramref name="localDirectory"/> without replacing either. On any failure
    /// neither file is left behind. Returns the manifest with <see cref="WorldDumpManifest.Path"/> set to the local CSV.
    /// </summary>
    /// <param name="gameBuild">The game build the caller pinned for this host (the dump does not report it).</param>
    public static Task<WorldDumpManifest> CaptureAsync(GameActor actor, IGameHost host, string hostDirectory, string name, int step,
        (float X, float Z, float Radius)? window, string gameBuild, string localDirectory, TimeSpan fetchTimeout,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return CaptureCoreAsync(actor, (from, to, token) => host.FetchDirectoryAsync(from, to, fetchTimeout, token),
            hostDirectory, name, step, window, gameBuild, localDirectory, cancellation);
    }

    internal static async Task<WorldDumpManifest> CaptureCoreAsync(GameActor actor, Func<string, string, CancellationToken, Task<FetchedDirectory>> fetch,
        string hostDirectory, string name, int step, (float X, float Z, float Radius)? window, string gameBuild, string localDirectory,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(fetch);
        if (!actor.Pinned) throw new InvalidOperationException("A pinned world dump needs strict pins on the actor that makes it.");
        if (string.IsNullOrWhiteSpace(hostDirectory) || hostDirectory.Any(char.IsWhiteSpace) || !AbsoluteHostPath.IsMatch(hostDirectory))
            throw new ArgumentException("The host directory must be an absolute path without whitespace (the CLI splits arguments on spaces).", nameof(hostDirectory));
        if (name == null || !SafeName.IsMatch(name)) throw new ArgumentException("Name the dump with letters, digits, '.', '_' or '-'.", nameof(name));
        if (step < 5 || step > 1000) throw new ArgumentOutOfRangeException(nameof(step), "The dump step must be 5..1000 metres.");
        if (window is { } w && (!float.IsFinite(w.X) || !float.IsFinite(w.Z) || !float.IsFinite(w.Radius) || w.Radius <= 0))
            throw new ArgumentOutOfRangeException(nameof(window), "A window needs a finite centre and a positive radius.");
        if (string.IsNullOrWhiteSpace(gameBuild)) throw new ArgumentException("Record the pinned game build.", nameof(gameBuild));
        ArgumentException.ThrowIfNullOrEmpty(localDirectory);
        string local = Path.GetFullPath(localDirectory);
        string csv = Path.Combine(local, name + ".csv"), json = Path.Combine(local, name + ".json");
        if (File.Exists(csv) || File.Exists(json)) throw new IOException($"A dump named {name} already exists in {local}.");

        cancellation.ThrowIfCancellationRequested();
        var before = World(actor);
        string command = $"cli_world_dump {step} {hostDirectory}" +
            (window is { } c ? string.Format(CultureInfo.InvariantCulture, " --window {0:R},{1:R},{2:R}", c.X, c.Z, c.Radius) : "");
        var lines = actor.Execute(command).Output.Where(line => line.StartsWith("OK: WORLD_DUMP ", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1) throw new InvalidDataException($"{command} did not answer exactly one OK: WORLD_DUMP line.");
        string reply = lines[0];
        var after = World(actor);
        if (after.Uid != before.Uid || after.Seed != before.Seed || after.Name != before.Name)
            throw new InvalidDataException($"The world changed during the dump: {before.Name} ({before.Uid}) before, {after.Name} ({after.Uid}) after.");

        var fields = Fields(reply);
        if (Field(fields, "step") != step.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException($"The reply's step differs from the requested {step}: {reply}");
        if (fields.ContainsKey("window") != window.HasValue)
            throw new InvalidDataException($"The command and reply disagree about a window: {reply}");
        if (window is { } asked)
        {
            // ValheimCLI reports the window it used, rounded to whole metres.
            string[] used = Field(fields, "window").Split(',');
            float[] wanted = [asked.X, asked.Z, asked.Radius];
            if (used.Length != 3 || used.Where((value, i) => !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ||
                Math.Abs(v - wanted[i]) > 1).Any())
                throw new InvalidDataException($"The reply's window {Field(fields, "window")} is not the requested {asked.X},{asked.Z},{asked.Radius}: {reply}");
        }
        string hostFile = Field(fields, "world");
        int cut = hostFile.LastIndexOfAny(['/', '\\']);
        if (cut < 0 || !SameDirectory(hostFile[..cut], hostDirectory))
            throw new InvalidDataException($"The reply's file {hostFile} is not in {hostDirectory}.");
        string fileName = hostFile[(cut + 1)..];

        Directory.CreateDirectory(local);
        string staging = Path.Combine(local, "." + name + ".fetch-" + Guid.NewGuid().ToString("N"));
        bool written = false;
        try
        {
            await fetch(hostDirectory, staging, cancellation).ConfigureAwait(false);
            string fetched = Path.Combine(staging, fileName);
            if (!File.Exists(fetched)) throw new FileNotFoundException($"The fetched directory has no {fileName}.", fetched);
            File.Move(fetched, csv);
            written = true;
            var grid = GridDumpTerrain.Load(csv);
            long samples = (long)grid.CountX * grid.CountZ;
            if (Field(fields, "samples") != samples.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException($"The reply reports samples={Field(fields, "samples")}; {fileName} holds {samples}.");
            if (window.HasValue || fields.ContainsKey("extent"))
            {
                string extent = string.Format(CultureInfo.InvariantCulture, "{0:F0},{1:F0}..{2:F0},{3:F0}", grid.OriginX, grid.OriginZ, grid.MaxX, grid.MaxZ);
                if (Field(fields, "extent") != extent)
                    throw new InvalidDataException($"The reply's extent {Field(fields, "extent")} differs from {fileName}'s {extent}.");
            }
            var manifest = new WorldDumpManifest
            {
                Name = name, Path = csv, WorldUid = before.Uid, Seed = before.Seed, GameBuild = gameBuild,
                Command = command, Reply = reply, Sha256 = FileHash.Sha256(csv), Step = step,
                MinX = grid.OriginX, MinZ = grid.OriginZ, MaxX = grid.MaxX, MaxZ = grid.MaxZ,
            };
            manifest.Verify();
            WriteManifest(manifest, json);
            return manifest;
        }
        catch
        {
            if (written) File.Delete(csv);
            throw;
        }
        finally
        {
            // Best effort: a lingering staging copy (a scanner holding a file, say) must not fail a capture already written.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Reads a manifest <see cref="CaptureAsync"/> wrote. A relative <see cref="WorldDumpManifest.Path"/> resolves against
    /// the manifest's own directory, so a CSV and manifest can move together. Nothing is verified until
    /// <see cref="WorldDumpManifest.Verify"/> or <see cref="LayeredDumpTerrain.Load"/>.
    /// </summary>
    public static WorldDumpManifest ReadManifest(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var manifest = JsonSerializer.Deserialize<WorldDumpManifest>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{path} holds no dump manifest.");
        if (!string.IsNullOrEmpty(manifest.Path) && !Path.IsPathRooted(manifest.Path))
            manifest.Path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, manifest.Path);
        return manifest;
    }

    /// <summary>Writes <paramref name="manifest"/> with its CSV named relative to the manifest (the file name only). Never replaces a file.</summary>
    internal static void WriteManifest(WorldDumpManifest manifest, string path)
    {
        var copy = JsonSerializer.Deserialize<WorldDumpManifest>(JsonSerializer.Serialize(manifest))!;
        copy.Path = Path.GetFileName(manifest.Path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { JsonSerializer.Serialize(file, copy, Indented); }
        catch { file.Dispose(); File.Delete(path); throw; } // only the file this call created
    }

    private static WorldFacts World(GameActor actor)
    {
        var worlds = actor.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
        if (worlds.Length != 1 || worlds[0].Uid.Length == 0 || worlds[0].Seed.Length == 0)
            throw new InvalidDataException("cli_world did not report exactly one loaded world with a seed and uid.");
        return worlds[0];
    }

    private static Dictionary<string, string> Fields(string reply)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string token in reply["OK: WORLD_DUMP ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException($"Unexpected token '{token}' in the dump reply: {reply}");
            if (!fields.TryAdd(token[..equals], token[(equals + 1)..])) throw new InvalidDataException($"The dump reply repeats {token[..equals]}=: {reply}");
        }
        return fields;
    }

    private static string Field(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && value.Length > 0 ? value : throw new InvalidDataException($"The dump reply lacks {key}=.");

    private static bool SameDirectory(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

}
