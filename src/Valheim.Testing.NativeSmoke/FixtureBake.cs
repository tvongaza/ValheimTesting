using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

/// <summary>One-phase fixture input and export for an owned, server-only smoke run.</summary>
internal static class FixtureBake
{
    internal sealed record Input(string Source, string Origin, WorldIdentity Identity, IReadOnlyDictionary<string, string> Files);
    internal sealed record SelectedMod(string Path, string Name, string Sha256);
    internal sealed record BuildInputs(IReadOnlyList<SelectedMod> Mods, string DependencyLock, string DependencyLockSha256);

    // Capture the build the game is about to load, not whatever happens to be on disk after it stops.
    internal static BuildInputs CaptureBuild(IReadOnlyList<string> mods, string dependencyLock, NativeDependencyLock? resolved = null)
    {
        var selected = new List<SelectedMod>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string mod in mods)
        {
            string name = Path.GetFileName(mod);
            if (!names.Add(name))
                throw new InvalidDataException("Two selected mods have the same file name; the fixture manifest cannot distinguish them: " + name);
            selected.Add(new SelectedMod(Path.GetFullPath(mod), name, Sha256(mod)));
        }
        if (resolved != null && (selected.Count != resolved.Mods.Count || selected.Where((mod, index) =>
                !mod.Path.Equals(Path.GetFullPath(resolved.Mods[index].File), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !mod.Sha256.Equals(resolved.Mods[index].Sha256, StringComparison.OrdinalIgnoreCase)).Any()))
            throw new InvalidDataException("A selected mod changed after the dependency lock was resolved.");
        return new BuildInputs(selected, Path.GetFullPath(dependencyLock), Sha256(dependencyLock));
    }

    private static void VerifyBuild(BuildInputs build)
    {
        foreach (var mod in build.Mods)
            if (!Sha256(mod.Path).Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A selected mod changed during the game run: " + mod.Name);
        if (!Sha256(build.DependencyLock).Equals(build.DependencyLockSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The dependency lock changed during the game run.");
    }

    internal static void RefuseOutput(string destination, params string[] protectedPaths)
    {
        destination = PhysicalPath(destination);
        if (Path.Exists(destination)) throw new IOException("The baked fixture output must be new: " + destination);
        foreach (string path in protectedPaths)
        {
            string source = PhysicalPath(path);
            if (Contains(source, destination) || Contains(destination, source))
                throw new IOException("The baked fixture output must be separate from the run output, world source and game install: " + destination);
        }
    }

    // Resolve every existing ancestor, including /tmp -> /private/tmp and Windows junctions.
    // The final destination need not exist yet.
    internal static string PhysicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        // A link can point through another link (on macOS, /var -> /private/var).
        for (int i = 0; i < 16; i++)
        {
            string resolved = ResolveExistingLinks(full);
            if (resolved == full) return resolved;
            full = resolved;
        }
        throw new IOException("The output path has too many symlink levels: " + path);
    }

    private static string ResolveExistingLinks(string full)
    {
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Path.Exists(current))
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }
        return Path.GetFullPath(current);
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
        string manifestFile = Path.Combine(Path.GetFileName(source) == "worlds_local" ? Path.GetDirectoryName(source)! : source, "fixture-manifest.json");
        if (Directory.Exists(Path.Combine(source, "worlds_local"))) source = Path.Combine(source, "worlds_local");
        if (Contains(PhysicalPath(source), PhysicalPath(target)) || Contains(PhysicalPath(target), PhysicalPath(source)))
            throw new IOException("The world fixture source and disposable run copy must be separate.");
        var identity = WorldIdentity.Read(source);
        var manifest = WorldFixture.Manifest(source);
        if (File.Exists(manifestFile)) VerifyBakedManifest(manifestFile, identity, manifest);
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

    private static void VerifyBakedManifest(string path, WorldIdentity identity, IReadOnlyDictionary<string, string> actual)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("world").GetProperty("Uid").GetInt64() != identity.Uid ||
                root.GetProperty("world").GetProperty("Name").GetString() != identity.Name)
                throw new InvalidDataException("The baked fixture manifest names another world or schema: " + path);
            var pinned = root.GetProperty("filesSha256").EnumerateObject()
                .ToDictionary(file => PortablePath(file.Name), file => file.Value.GetString() ??
                    throw new InvalidDataException("The baked fixture manifest has a null file hash."), StringComparer.Ordinal);
            var present = Sorted(actual);
            if (pinned.Count != present.Count || present.Any(file => !pinned.TryGetValue(file.Key, out string? hash) ||
                    !hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The baked fixture's world files differ from its manifest: " + path);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("The baked fixture manifest is malformed: " + path, error);
        }
    }

    internal static void Export(string evidence, string destination, Input input, BuildInputs build, params string[] protectedPaths)
    {
        destination = PhysicalPath(destination);
        RefuseOutput(destination, new[] { input.Source, evidence }.Concat(protectedPaths).ToArray());
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
        VerifyBuild(build);

        string ordinary = Path.Combine(evidence, "host-world");
        string mac = Path.Combine(evidence, "mac-world-input", "host-world");
        if (Directory.Exists(ordinary) == Directory.Exists(mac))
            throw new InvalidDataException("Expected exactly one fetched server world copy after the clean stop.");
        string world = Directory.Exists(ordinary) ? ordinary : mac;
        if (Directory.Exists(Path.Combine(world, "worlds_local"))) world = Path.Combine(world, "worlds_local");
        var identity = WorldIdentity.Read(world);
        if (identity.Uid != input.Identity.Uid || identity.Name != input.Identity.Name)
            throw new InvalidDataException("The saved world does not match the source fixture's name and UID.");
        var finalSave = Regex.Match(identity.File.Replace('\\', '/'), @"/_main\.(\d+)\.fwl2$", RegexOptions.CultureInvariant);
        if (!finalSave.Success || !uint.TryParse(finalSave.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint exportedSaveNumber))
            throw new InvalidDataException("The exported world has no numbered chunked save; its final save cannot be identified.");
        if (exportedSaveNumber < saveNumber)
            throw new InvalidDataException($"The fetched world is save {exportedSaveNumber}, older than the confirmed save {saveNumber}.");
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
            VerifyBuild(build);
            var selected = new SortedDictionary<string, string>(build.Mods.ToDictionary(mod => mod.Name, mod => mod.Sha256), StringComparer.Ordinal);
            var manifest = new
            {
                schemaVersion = 1,
                world = new { outputIdentity.Name, outputIdentity.SeedName, outputIdentity.Seed, outputIdentity.Uid, outputIdentity.WorldVersion },
                source = new { input.Origin, input.Identity.Uid, sha256 = Sorted(input.Files) },
                build = new { selectedModsSha256 = selected, dependencyLockSha256 = build.DependencyLockSha256,
                    runId = provenance.GetProperty("runId").GetString(), confirmedSaveNumber = saveNumber, saveNumber = exportedSaveNumber },
                filesSha256 = Sorted(files),
            };
            File.WriteAllText(Path.Combine(temporary, "fixture-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            RefuseOutput(destination, new[] { input.Source, evidence }.Concat(protectedPaths).ToArray());
            Directory.Move(temporary, destination); // same parent: a complete fixture appears at once
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> files) =>
        new(files.ToDictionary(pair => PortablePath(pair.Key), pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string PortablePath(string relative) => relative.Replace('\\', '/');

    private static string Sha256(string file)
        => FileHash.Sha256(file);

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
