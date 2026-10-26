using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

/// <summary>One-phase fixture input and export for an owned, server-only smoke run.</summary>
internal static class FixtureBake
{
    /// <summary>A baked or saved fixture's manifest, beside its <c>worlds_local/</c>.</summary>
    internal const string ManifestFile = "fixture-manifest.json";
    internal sealed record Input(string Source, string Origin, WorldIdentity Identity, IReadOnlyDictionary<string, string> Files)
    {
        internal WorldIdentity.Admission? CopyAdmission { get; init; }
    }
    internal sealed record Admission(WorldIdentity.Admission World, string ManifestFile, string? ManifestSha256)
    {
        internal string Source => World.Source;
        internal WorldIdentity Identity => World.Identity;
        internal IReadOnlyDictionary<string, string> Files => World.Files;
        internal GameBuild Target => World.Target;
    }

    internal static Admission Admit(string source, GameBuild target)
    {
        var (worlds, manifestFile) = Paths(source);
        string? manifestHash = File.Exists(manifestFile) ? Sha256(manifestFile) : null;
        var world = WorldIdentity.Capture(worlds, target);
        var files = world.Files;
        var identity = world.Identity;
        if (manifestHash != null) VerifyBakedManifest(manifestFile, identity, files);
        if (manifestHash != (File.Exists(manifestFile) ? Sha256(manifestFile) : null))
            throw new InvalidDataException("The baked fixture manifest changed during admission.");
        return new(world, manifestFile, manifestHash);
    }

    internal static void VerifyAdmission(Admission admission)
    {
        admission.World.Verify();
        VerifyManifestAdmission(admission);
    }

    internal static void VerifyManifestAdmission(Admission admission)
    {
        if (admission.ManifestSha256 != (File.Exists(admission.ManifestFile) ? Sha256(admission.ManifestFile) : null))
            throw new InvalidDataException("The baked fixture manifest changed after admission: " + admission.ManifestFile);
        if (admission.ManifestSha256 != null) VerifyBakedManifest(admission.ManifestFile, admission.Identity, admission.Files);
    }

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
        if (Path.Exists(destination)) throw new IOException("The baked fixture output must be new: " + destination);
        foreach (string path in protectedPaths)
        {
            if (ProtectedPaths.Contains(path, destination) || ProtectedPaths.Contains(destination, path))
                throw new IOException("The baked fixture output must be separate from the run output, world source and game install: " + destination);
        }
    }

    /// <summary>
    /// A <c>--world-fixture</c> as every route reads it before anything is staged: the world directory (the fixture itself, or its
    /// <c>worlds_local/</c>), its baked manifest's path, and the one complete world in it (<see cref="WorldIdentity.Read"/>).
    /// </summary>
    internal static (string Worlds, string ManifestFile, WorldIdentity Identity) Inspect(string source)
    {
        var (worlds, manifestFile) = Paths(source);
        return (worlds, manifestFile, WorldIdentity.Read(worlds));
    }

    internal static (string Worlds, string ManifestFile) Paths(string source)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); // C:\x\worlds_local\ is worlds_local too
        string manifestFile = Path.Combine(Path.GetFileName(source) == "worlds_local" ? Path.GetDirectoryName(source)! : source, ManifestFile);
        if (Directory.Exists(Path.Combine(source, "worlds_local"))) source = Path.Combine(source, "worlds_local");
        return (source, manifestFile);
    }

    /// <summary>
    /// <see cref="Inspect"/>, and when the fixture has a <see cref="ManifestFile"/>, its files checked against it: a chosen world
    /// refused before a run builds or stages anything. <see cref="Prepare"/> checks again as it copies.
    /// </summary>
    internal static WorldIdentity Check(string source)
    {
        var (worlds, manifestFile) = Paths(source);
        if (!File.Exists(manifestFile)) return WorldIdentity.Read(worlds);
        var files = WorldFixture.Manifest(worlds);
        var identity = WorldIdentity.ReadObserved(worlds, files);
        VerifyBakedManifest(manifestFile, identity, files);
        return identity;
    }

    internal static Input Prepare(string? source, string outputRoot, Admission? admission = null, GameBuild? targetBuild = null,
        Action<string, string>? copyFile = null, Action<string>? afterSourceVerification = null)
    {
        string target = Path.Combine(outputRoot, "world-source", "worlds_local");
        if (source == null)
        {
            DefaultSmokeWorld.PrepareServerSaveRoot(Path.Combine(outputRoot, "world-source"));
            var packaged = targetBuild == null ? null : DefaultSmokeWorld.AdmitPrepared(target, targetBuild);
            return new Input(target, "packaged VTDefaultSmoke", packaged?.Identity ?? WorldIdentity.Read(target), packaged?.Files ?? WorldFixture.Manifest(target))
            { CopyAdmission = packaged };
        }
        if (admission != null) VerifyAdmission(admission);
        (source, string manifestFile) = Paths(source);
        var manifest = admission?.Files ?? WorldFixture.Manifest(source);
        var identity = admission?.Identity ?? WorldIdentity.ReadObserved(source, manifest);
        if (ProtectedPaths.Contains(source, target) || ProtectedPaths.Contains(target, source))
            throw new IOException("The world fixture source and disposable run copy must be separate.");
        var worldAdmission = admission?.World ?? (targetBuild == null ? null : WorldIdentity.Capture(source, targetBuild, manifest));
        if (File.Exists(manifestFile)) VerifyBakedManifest(manifestFile, identity, manifest);
        Directory.CreateDirectory(target);
        try
        {
            CopyFiles(source, target, manifest, copyFile: copyFile);
            if (admission != null) VerifyManifestAdmission(admission);
            // Keep manifest copying inside the final source/destination boundary, before either final payload observation.
            string? copiedManifest = null;
            if (File.Exists(manifestFile))
            {
                copiedManifest = Path.Combine(outputRoot, "world-source", ManifestFile);
                File.Copy(manifestFile, copiedManifest);
                if (admission != null && Sha256(copiedManifest) != admission.ManifestSha256)
                    throw new InvalidDataException("The baked fixture manifest changed while copying.");
                VerifyBakedManifest(copiedManifest, identity, manifest);
            }
            WorldFixture.Verify(source, manifest); // a file already copied may change while later files are copied
            if (admission != null) VerifyManifestAdmission(admission);
            afterSourceVerification?.Invoke(target);
            var observedCopy = WorldFixture.VerifyObserved(target, manifest);
            var copyAdmission = worldAdmission?.ForObservedCopy(target, observedCopy);
            var copied = copyAdmission?.Identity ?? WorldIdentity.ReadObserved(target, observedCopy);
            if (copied != identity)
                throw new InvalidDataException("The disposable world differs from its admitted source fixture identity.");
            if (copiedManifest != null) VerifyBakedManifest(copiedManifest, copied, observedCopy);
            string label = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (label == "worlds_local")
                label = Path.GetFileName(Path.GetDirectoryName(source)!);
            return new Input(source, label, identity, manifest) { CopyAdmission = copyAdmission };
        }
        catch
        {
            Directory.Delete(Path.Combine(outputRoot, "world-source"), recursive: true);
            throw;
        }
    }

    internal static void VerifyBakedManifest(string path, WorldIdentity identity, IReadOnlyDictionary<string, string> actual)
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

    internal static void Export(string evidence, string destination, Input input, BuildInputs build, params string[] protectedPaths) =>
        Export(evidence, destination, input, build, CancellationToken.None, protectedPaths);

    internal static void Export(string evidence, string destination, Input input, BuildInputs build, CancellationToken cancellation,
        params string[] protectedPaths)
    {
        cancellation.ThrowIfCancellationRequested();
        destination = ProtectedPaths.Resolve(destination); // write where the checks looked
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
            CopyFiles(world, copy, files, cancellation);
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
            File.WriteAllText(Path.Combine(temporary, ManifestFile),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            cancellation.ThrowIfCancellationRequested();
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

    private static void CopyFiles(string source, string target, IReadOnlyDictionary<string, string> expected,
        CancellationToken cancellation = default, Action<string, string>? copyFile = null)
    {
        foreach (var (relative, hash) in expected.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            (copyFile ?? ((from, to) => File.Copy(from, to)))(Path.Combine(source, relative), destination);
        }
    }
}
