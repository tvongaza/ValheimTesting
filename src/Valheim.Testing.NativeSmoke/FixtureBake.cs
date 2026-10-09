using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>One-phase fixture input and export for an owned, server-only smoke run.</summary>
internal static class FixtureBake
{
    internal sealed record Input(string Source, string Origin, WorldIdentity Identity, IReadOnlyDictionary<string, string> Files);

    internal static void RefuseOutput(string destination, params string[] protectedPaths)
    {
        destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("The baked fixture output must be new: " + destination);
        foreach (string path in protectedPaths)
        {
            string source = Path.GetFullPath(path);
            if (Contains(source, destination) || Contains(destination, source))
                throw new IOException("The baked fixture output must be separate from the run output, world source and game install: " + destination);
        }
    }

    private static bool Contains(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        return relative == "." || relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    internal static Input Prepare(string? source, string outputRoot)
    {
        string target = Path.Combine(outputRoot, "world-source", "worlds_local");
        if (source == null)
        {
            DefaultSmokeWorld.PrepareServerSaveRoot(Path.Combine(outputRoot, "world-source"));
            return new Input(target, "packaged VTDefaultSmoke", WorldIdentity.Read(target), WorldFixture.Manifest(target));
        }
        source = Path.GetFullPath(source);
        if (Directory.Exists(Path.Combine(source, "worlds_local"))) source = Path.Combine(source, "worlds_local");
        if (Contains(source, target) || Contains(target, source))
            throw new IOException("The world fixture source and disposable run copy must be separate.");
        var identity = WorldIdentity.Read(source);
        var manifest = WorldFixture.Manifest(source);
        Directory.CreateDirectory(target);
        try
        {
            CopyFiles(source, target, manifest);
            WorldFixture.Verify(source, manifest); // source may have changed while being copied
            WorldFixture.Verify(target, manifest);
            if (WorldIdentity.Read(target).Uid != identity.Uid)
                throw new InvalidDataException("The disposable world has a different UID from its source fixture.");
            string label = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (label == "worlds_local")
                label = Path.GetFileName(Path.GetDirectoryName(source)!);
            return new Input(source, label, identity, manifest);
        }
        catch
        {
            Directory.Delete(Path.Combine(outputRoot, "world-source"), recursive: true);
            throw;
        }
    }

    internal static void Export(string evidence, string destination, Input input, IReadOnlyList<string> mods, string dependencyLock)
    {
        destination = Path.GetFullPath(destination);
        RefuseOutput(destination, input.Source, evidence);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "result.json")));
        var root = result.RootElement;
        if (!root.GetProperty("Passed").GetBoolean() || !root.GetProperty("CleanupVerified").GetBoolean())
            throw new InvalidOperationException("The run or its cleanup failed; no baked fixture was exported.");
        var provenance = root.GetProperty("Provenance");
        if (!provenance.TryGetProperty("bakeSaveNumber", out var saved) ||
            !uint.TryParse(saved.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint saveNumber) || saveNumber == 0)
            throw new InvalidOperationException("The server did not confirm an advancing world save; no baked fixture was exported.");
        if (!provenance.TryGetProperty("serverStopsClean", out var stopped) || stopped.GetString() != "true")
            throw new InvalidOperationException("The server did not stop cleanly; no baked fixture was exported.");
        WorldFixture.Verify(input.Source, input.Files);

        string ordinary = Path.Combine(evidence, "host-world");
        string mac = Path.Combine(evidence, "mac-world-input", "host-world");
        if (Directory.Exists(ordinary) == Directory.Exists(mac))
            throw new InvalidDataException("Expected exactly one fetched server world copy after the clean stop.");
        string world = Directory.Exists(ordinary) ? ordinary : mac;
        if (Directory.Exists(Path.Combine(world, "worlds_local"))) world = Path.Combine(world, "worlds_local");
        var identity = WorldIdentity.Read(world);
        if (identity.Uid != input.Identity.Uid || identity.Name != input.Identity.Name)
            throw new InvalidDataException("The saved world does not match the source fixture's name and UID.");
        var files = WorldFixture.Manifest(world);
        if (files.Count == input.Files.Count && files.All(file => input.Files.TryGetValue(file.Key, out var hash) &&
                hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The fetched world is byte-identical to the source fixture despite the reported save; no baked fixture was exported.");
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("A baked fixture needs an output parent.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, "." + Path.GetFileName(destination) + ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string copy = Path.Combine(temporary, "worlds_local");
            Directory.CreateDirectory(copy);
            CopyFiles(world, copy, files);
            WorldFixture.Verify(world, files);
            WorldFixture.Verify(copy, files);
            var outputIdentity = WorldIdentity.Read(copy);
            if (outputIdentity.Uid != identity.Uid) throw new InvalidDataException("The exported world UID changed during copying.");
            var selected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string mod in mods)
            {
                string name = Path.GetFileName(mod);
                if (!selected.TryAdd(name, Sha256(mod)))
                    throw new InvalidDataException("Two selected mods have the same file name; the fixture manifest cannot distinguish them: " + name);
            }
            var manifest = new
            {
                schemaVersion = 1,
                world = new { outputIdentity.Name, outputIdentity.SeedName, outputIdentity.Seed, outputIdentity.Uid, outputIdentity.WorldVersion },
                source = new { input.Origin, input.Identity.Uid, sha256 = Sorted(input.Files) },
                build = new { selectedModsSha256 = selected, dependencyLockSha256 = Sha256(dependencyLock),
                    runId = provenance.GetProperty("runId").GetString(), saveNumber },
                filesSha256 = Sorted(files),
            };
            File.WriteAllText(Path.Combine(temporary, "fixture-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            RefuseOutput(destination, input.Source, evidence);
            Directory.Move(temporary, destination); // same parent: a complete fixture appears at once
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> files) =>
        new(files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Sha256(string file)
    {
        using var input = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static void CopyFiles(string source, string target, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var (relative, hash) in expected.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination);
            if (!Sha256(destination).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("World fixture changed while copying: " + relative);
        }
    }
}
