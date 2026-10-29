using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>
/// <c>valheim-test save-world</c>: saves the world a run held on this machine hosts in its owned client, and copies that save out
/// as a one-world fixture a later <c>start --world-fixture</c> (or <c>server-load --world-fixture</c>) stages in place of the
/// bundled one. The run stays live: the game saves through its own session command, and the copy only reads the client's worlds.
/// </summary>
internal static class WorldSave
{
    internal const string Usage = "valheim-test save-world --run ID --output NEW_DIR";

    /// <summary>The hosted world a run journalled as placed in a client's worlds and not yet moved out.</summary>
    internal sealed record Placement(string World, string Name, string WorldUid);

    /// <summary>The save copied out: its fixture folder, the world and its save number, and how many files it holds.</summary>
    internal sealed record Saved(string Output, WorldIdentity Identity, uint SaveNumber, int Files);

    internal static int Run(string[] args, TextWriter? output = null, TextWriter? error = null, string? holds = null, string? journal = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
            if (args[i] is "--run" or "--output" && !string.IsNullOrWhiteSpace(args[i + 1])) values.TryAdd(args[i], args[i + 1]);
        if (args.Length != 4 || values.Count != 2) { error.WriteLine("Usage: " + Usage); return 2; }
        string run = values["--run"], destination = values["--output"];
        bool sent = false;
        try
        {
            string evidence = ForegroundHold.HeldEvidence(run, holds);
            var placement = FindPlacement(journal ?? RunJournal.LocalDirectory, run);
            destination = ProtectedPaths.Resolve(Path.GetFullPath(destination)); // write where the checks looked
            if (Path.Exists(destination)) throw new IOException("The saved world's output must be new: " + destination);
            foreach (string path in new[] { Path.GetDirectoryName(placement.World)!, evidence })
                if (ProtectedPaths.Contains(path, destination) || ProtectedPaths.Contains(destination, path))
                    throw new IOException("The saved world's output must be outside the client's worlds and the run's evidence: " + destination);
            // Everything the manifest records is read before the game is asked to save, so a refusal here sends nothing.
            var provenance = Provenance(run, evidence, File.ReadAllLines(Path.Combine(evidence, OwnedClientCommandLease.WorldPins)));
            using var actor = OwnedCliCommand.Connect(evidence, OwnedClientCommandLease.ClientRole, "world", null, TimeSpan.FromSeconds(15),
                out _, out string commandLog);
            sent = true;
            uint confirmed = new SessionControl(actor).Save(placement.WorldUid, TimeSpan.FromSeconds(120)); // Exactly once.
            var saved = Export(placement, confirmed, destination, provenance);
            output.WriteLine($"SAVED world {saved.Identity.Name} (UID {saved.Identity.UidText}) save {saved.SaveNumber}, {saved.Files} files, to {saved.Output}");
            output.WriteLine("Start a run from it: valheim-test start --world-fixture " + saved.Output + " [start options]");
            output.WriteLine("Private command evidence: " + commandLog);
            return 0;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException or
            TimeoutException or UnauthorizedAccessException or KeyNotFoundException or FormatException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine("REFUSED: " + failure.Message);
            // Before the save was sent nothing changed: refused. After, the run's world may have saved without a copy: failed.
            return sent ? 1 : 3;
        }
    }

    /// <summary>
    /// The one hosted world run <paramref name="run"/> journalled in this machine's journal as placed in a client's worlds
    /// (<c>holds=hosted-world</c>, then <c>placementState=transferred</c>) and not yet moved out (no <c>copy-retired</c> for it).
    /// A run with none hosts no world on this machine; several are refused, never guessed between.
    /// </summary>
    internal static Placement FindPlacement(string journal, string run)
    {
        if (!RunJournal.SafeName(run) || run == "-") throw new ArgumentException("Invalid run ID.", nameof(run));
        string directory = Path.Combine(Path.GetFullPath(journal), run);
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"This machine's journal has no run {run} ({directory}); save-world saves a run held on this machine.");
        var records = new List<JournalRecord>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            string text;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
            foreach (string line in text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length != 0))
            {
                try { records.Add(RunJournal.ParseLine(line)); }
                catch (Exception bad) when (bad is JsonException or FormatException)
                { throw new InvalidDataException($"Run {run}'s journal {Path.GetFileName(file)} has a line that cannot be read ({bad.Message}); inspect it with valheim-test env status.", bad); }
            }
        }
        // Each world's latest word decides: placed (transferred) after its last move-out, or not. A run can place the same path again.
        records = records.OrderBy(record => record.Utc).ToList(); // stable: one actor's lines keep their order
        var placed = new List<Placement>();
        foreach (string world in records.Where(record => record.Entry.Kind == JournalEntry.CopyIntended && record.Entry.Fields.GetValueOrDefault("holds") == "hosted-world")
            .Select(record => record.Entry.Fields["runtime"]).Distinct(StringComparer.Ordinal))
        {
            var about = records.Where(record => record.Entry.Fields.GetValueOrDefault("runtime") == world).ToList();
            int transferred = about.FindLastIndex(record => record.Entry.Kind == JournalEntry.CopyDone && record.Entry.Fields.GetValueOrDefault("placementState") == "transferred");
            int retired = about.FindLastIndex(record => record.Entry.Kind is JournalEntry.CopyRetired or JournalEntry.CopyKept);
            if (transferred <= retired) continue;
            var intended = about.Last(record => record.Entry.Kind == JournalEntry.CopyIntended && record.Entry.Fields.ContainsKey("worldUid"));
            placed.Add(new Placement(world, intended.Entry.Fields["world"], intended.Entry.Fields["worldUid"]));
        }
        return placed.Count switch
        {
            1 => placed[0],
            0 => throw new InvalidOperationException($"Run {run} has no hosted world in a client's worlds on this machine. save-world saves the world a held " +
                "valheim-test start --hold run's owned client hosts; a dedicated server's world is baked with server-load --bake-fixture."),
            _ => throw new InvalidOperationException($"Run {run} journals {placed.Count} hosted worlds in place at once ({string.Join(", ", placed.Select(place => place.World))}); none is chosen."),
        };
    }

    /// <summary>
    /// Copies the newest complete save of <paramref name="placement"/>'s live world, which must be save <paramref name="confirmed"/>
    /// or later, into <c>&lt;destination&gt;/worlds_local/&lt;name&gt;/</c> with a <see cref="FixtureBake.ManifestFile"/> (the schema
    /// <c>server-load --bake-fixture</c> writes, plus <paramref name="provenance"/>): the four <c>_main.&lt;n&gt;</c> files and the chunk files
    /// that save lists, nothing else (the game's older chunk files and minimap cache stay behind). Every copied file is hashed as it
    /// is read and read again after; a file the game changed or removed meanwhile refuses the copy. The destination appears complete
    /// or not at all.
    /// </summary>
    internal static Saved Export(Placement placement, uint confirmed, string destination, IReadOnlyDictionary<string, object?> provenance)
    {
        destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("The saved world's output must be new: " + destination);
        var (number, files) = NewestSave(placement.World, placement.Name);
        if (number < confirmed)
            throw new InvalidDataException($"The client's world {placement.World} holds save {number}, older than the save {confirmed} the game confirmed.");
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("The saved world needs an output parent.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, "." + Path.GetFileName(destination) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            string worlds = Path.Combine(temporary, "worlds_local");
            string copy = Path.Combine(worlds, placement.Name);
            Directory.CreateDirectory(copy);
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in files)
            {
                string source = Path.Combine(placement.World, file);
                string hash;
                try { hash = CopyHashed(source, Path.Combine(copy, file)); }
                catch (Exception gone) when (gone is FileNotFoundException or DirectoryNotFoundException)
                { throw new IOException($"{placement.Name}/{file} was removed during the copy: the game saved again, or the run is finishing and moved its world out. Run save-world again while the run is held.", gone); }
                hashes[placement.Name + "/" + file] = hash;
            }
            foreach (string file in files)
                if (!File.Exists(Path.Combine(placement.World, file)) || !HashShared(Path.Combine(placement.World, file)).Equals(hashes[placement.Name + "/" + file], StringComparison.Ordinal))
                    throw new IOException($"The game changed {placement.Name}/{file} during the copy (it saved again); run save-world again.");
            var identity = WorldIdentity.Read(worlds);
            if (identity.UidText != placement.WorldUid || identity.Name != placement.Name)
                throw new InvalidDataException($"The saved world is {identity.Name} with UID {identity.UidText}, not the run's {placement.Name} with UID {placement.WorldUid}.");
            if (!WorldFixture.Manifest(worlds).ToDictionary(pair => pair.Key.Replace('\\', '/'), pair => pair.Value, StringComparer.Ordinal)
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(hashes))
                throw new InvalidDataException("The saved world's files differ from the ones copied.");
            var manifest = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = 1,
                ["world"] = new { identity.Name, identity.SeedName, identity.Seed, identity.Uid, identity.WorldVersion },
                ["savedFrom"] = new Dictionary<string, object?>(provenance, StringComparer.Ordinal) { ["confirmedSaveNumber"] = confirmed, ["saveNumber"] = number },
                ["filesSha256"] = hashes,
            };
            File.WriteAllText(Path.Combine(temporary, FixtureBake.ManifestFile), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            if (Path.Exists(destination)) throw new IOException("The saved world's output must be new: " + destination);
            Directory.Move(temporary, destination); // same parent: a complete fixture appears at once
            return new Saved(destination, identity, number, hashes.Count);
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    // The newest complete save set in a live world folder and the chunk files it lists. A live folder can still hold an older set
    // and older chunk files (the game removes those on its next load); only the newest set and its listed chunks are the world.
    private static (uint Number, IReadOnlyList<string> Files) NewestSave(string world, string name)
    {
        if (!Directory.Exists(world)) throw new DirectoryNotFoundException($"The client's world {name} is not at {world}, where the run placed it.");
        var present = Directory.EnumerateFiles(world).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var complete = present.Select(file => WorldIdentity.SaveFile.Match(file)).Where(match => match.Success)
            .GroupBy(match => uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(set => set.Select(match => match.Groups[2].Value).ToHashSet(StringComparer.Ordinal).SetEquals(WorldIdentity.SetParts))
            .Select(set => set.Key).ToList();
        if (complete.Count == 0)
            throw new InvalidDataException($"The client's world {name} at {world} holds no complete save (_main.<n>.fwl2, .db2, .chunks and .ok).");
        uint number = complete.Max();
        string list = $"_main.{number}.chunks";
        var chunks = WorldIdentity.ChunkFiles(ReadShared(Path.Combine(world, list)), name + "/" + list).Order(StringComparer.Ordinal).ToList();
        var missing = chunks.Where(chunk => !present.Contains(chunk)).ToList();
        if (missing.Count != 0)
            throw new InvalidDataException($"The client's world {name} save {number} lists {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? $" and {missing.Count - 3} more" : "")}, which the folder does not hold; run save-world again.");
        return (number, [.. WorldIdentity.SetParts.Select(part => $"_main.{number}.{part}"), .. chunks]);
    }

    // The game may hold a save file open: read it sharing every access, and hash what was written.
    private static string CopyHashed(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 16];
        for (int read; (read = input.Read(buffer)) > 0;)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string HashShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return FileHash.Sha256(stream);
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Where the save came from: the run, when, the game and plugin pins its client verified in the world, and the run's own
    /// inputs (<c>regression.json</c> two folders above the arm's evidence): the mod arms with their SHA-256 and source commit, the
    /// fixed plugins and the ValheimCLI core, and the fixture the run started from.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> Provenance(string run, string evidence, IReadOnlyList<string> worldPins)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = run,
            ["savedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["pins"] = new SortedDictionary<string, string>(worldPins.Where(line => line.Contains('=', StringComparison.Ordinal))
                .Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal), StringComparer.Ordinal),
        };
        string inputs = Path.GetFullPath(Path.Combine(evidence, "..", "..", "regression.json"));
        if (!File.Exists(inputs)) return result;
        var regression = RegressionInputs.Read(inputs);
        if (regression.NoMod) result["noMod"] = true;
        result["mods"] = regression.Mod.Arms.ToDictionary(arm => arm.Key, arm => (object)new { file = Path.GetFileName(arm.Value.File), sha256 = arm.Value.Sha256, commit = arm.Value.Commit }, StringComparer.Ordinal);
        result["plugins"] = regression.Plugins.Select(plugin => new { file = Path.GetFileName(plugin.File), sha256 = plugin.Sha256 }).ToList();
        result["cliCore"] = new { file = Path.GetFileName(regression.Cli.Core.File), sha256 = regression.Cli.Core.Sha256 };
        if (regression.Fixture.Create is { } request)
        {
            string created = Path.Combine(evidence, "world-created.json");
            using var proof = JsonDocument.Parse(File.ReadAllText(created));
            result["startedFrom"] = new { requested = request, worldUid = proof.RootElement.GetProperty("worldUid").GetString(),
                creationProofSha256 = FileHash.Sha256(created) };
            return result;
        }
        string? startedFrom = null;
        string fixtureManifest = Path.Combine(Path.GetDirectoryName(regression.Fixture.Root)!, FixtureBake.ManifestFile);
        if (File.Exists(fixtureManifest))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fixtureManifest));
            if (document.RootElement.TryGetProperty("savedFrom", out var from) && from.TryGetProperty("runId", out var earlier)) startedFrom = earlier.GetString();
        }
        result["startedFrom"] = new { worldUid = regression.Fixture.WorldUid, savedRun = startedFrom };
        return result;
    }
}
