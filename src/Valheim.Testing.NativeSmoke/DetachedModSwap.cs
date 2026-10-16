using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

// One selected DLL changes; the original dependency choice and detached input snapshots remain immutable.
internal sealed class DetachedModSwap
{
    internal sealed record Input(string Mod, string Sha256, string BuildInputs, string Dependencies);
    internal const long MaximumBytes = 512L << 20;
    private readonly ServerActor _server;
    private readonly NativeDependencyLock _original;
    private readonly string _evidence;
    private Action? _verifyActivated;

    internal DetachedModSwap(ServerActor server, NativeDependencyLock original, string evidence)
    {
        _server = server; _evidence = evidence;
        _original = JsonSerializer.Deserialize<NativeDependencyLock>(JsonSerializer.Serialize(original))!;
        DetachedCommandQueue.WriteNew(Path.Combine(evidence, "detached-original-build.json"), _original, bounded: false);
    }

    internal void VerifyActivated() => _verifyActivated?.Invoke();

    internal Action Prepare(Input input, string operation, Action admission, CancellationToken cancellation)
    {
        Validate(input);
        cancellation.ThrowIfCancellationRequested();
        string sourceRoot = Path.GetDirectoryName(input.Mod)!;
        foreach (string destination in new[] { _server.CheckpointLoaderDirectory, _server.CheckpointGameDirectory, _evidence })
            if (ProtectedPaths.Contains(sourceRoot, destination) || ProtectedPaths.Contains(destination, sourceRoot))
                throw new InvalidDataException("Swap source inputs must be separate from the owned profile and evidence.");
        var sourceHashes = BoundedManifest(sourceRoot);
        RequireHash(input.Mod, input.Sha256);
        RequireRegular(input.BuildInputs, DetachedCommandQueue.MaxBytes);
        RequireRegular(input.Dependencies, DetachedCommandQueue.MaxBytes);
        string buildHash = FileHash.Sha256(input.BuildInputs), lockHash = FileHash.Sha256(input.Dependencies);
        var proposed = NativeDependencyLock.ReadReady(input.Dependencies);
        _original.RequireSameFixedInputs(proposed);
        if (proposed.Mods.Count == 0 || Path.GetFullPath(proposed.Mods[0].File) != input.Mod ||
            !proposed.Mods[0].Sha256.Equals(input.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The swap lock must select the requested pinned DLL first.");
        var files = proposed.Mods.Concat(proposed.Plugins).Concat(proposed.CliFiles).ToArray();
        if (files.Length > 256 || files.Select(file => Path.GetFullPath(file.File)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("The swap dependency lock has duplicate or excessive files.");
        foreach (var file in files) RequireRegular(file.File, MaximumBytes);
        if (files.Sum(file => new FileInfo(file.File).Length) > MaximumBytes)
            throw new InvalidDataException("The selected swap files exceed their total size limit.");
        var originalMod = _original.Mods[0];
        RequireHash(originalMod.File, originalMod.Sha256);
        var oldMetadata = PluginMetadata.Read(originalMod.File);
        var newMetadata = PluginMetadata.Read(input.Mod);
        if (oldMetadata.Plugins.Count != 1 || newMetadata.Plugins.Count != 1 || oldMetadata.Plugins[0].Guid != newMetadata.Plugins[0].Guid)
            throw new InvalidDataException("A detached swap must preserve the original single plugin GUID.");
        string guid = oldMetadata.Plugins[0].Guid;
        string game = _server.CheckpointGameDirectory, core = Path.Combine(_server.CheckpointLoaderDirectory, "BepInEx", "core");
        var suppliedBuild = JsonSerializer.Deserialize<ModBuild.BuildRecord>(File.ReadAllText(input.BuildInputs),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing build inputs.");
        var buildFiles = (suppliedBuild.Sources ?? []).Concat(suppliedBuild.Publicized ?? []).Concat(suppliedBuild.Dependencies ?? []).ToArray();
        if (buildFiles.Length > 256 || buildFiles.Select(file => Path.GetFullPath(file.File)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != buildFiles.Length)
            throw new InvalidDataException("Build inputs have duplicate or excessive files.");
        foreach (var file in buildFiles) RequireRegular(file.File, MaximumBytes);
        var build = ModBuild.Verify(input.BuildInputs, game, input.Mod);
        ModBuild.RequireLoaderMatches(build, core);
        ModBuild.RequireResolvedDependencies(build, proposed);
        // Resolve again from the proposed exact dependency choices, so a lock with omitted new hard dependencies cannot pass.
        // Bound and reject links in all directories the resolver scans before handing them to it.
        foreach (string directory in proposed.Mods.Select(file => Path.GetDirectoryName(file.File)!).Distinct(StringComparer.OrdinalIgnoreCase))
            BoundedManifest(directory);
        string manifest = Path.Combine(_evidence, "detached-swap-" + operation + ".cli.json");
        // The caller's intent is already durable. This is private evidence, never a runtime or dependency change.
        DetachedCommandQueue.WriteNew(manifest, proposed.CliManifest);
        var resolved = NativeDependencyResolver.Resolve(new()
        {
            Mods = proposed.Mods.Select(file => file.File).ToList(),
            SearchRoots = proposed.Plugins.Select(file => file.File).ToList(),
            GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(game))!, BepInExCore = core,
            CliFiles = Path.GetDirectoryName(proposed.CliFiles.Single().File)!, CliManifest = manifest,
            OptionalReferences = proposed.OptionalReferences,
        });
        proposed.RequireSameFixedInputs(resolved);
        ModBuild.RequireResolvedDependencies(build, resolved);
        string oldMd5 = FileHash.Md5(originalMod.File), newMd5 = FileHash.Md5(input.Mod);
        void Verify()
        {
            WorldFixture.Verify(sourceRoot, sourceHashes);
            RequireHash(input.BuildInputs, buildHash); RequireHash(input.Dependencies, lockHash);
            RequireHash(input.Mod, input.Sha256);
            foreach (var file in files.Concat(_original.Mods).Concat(_original.Plugins).Concat(_original.CliFiles)) RequireHash(file.File, file.Sha256);
            foreach (var file in buildFiles) RequireHash(file.File, file.Sha256);
        }
        Verify(); admission(); cancellation.ThrowIfCancellationRequested();
        return () =>
        {
            Verify(); admission(); cancellation.ThrowIfCancellationRequested();
            DetachedCommandQueue.WriteNew(Path.Combine(_evidence, "detached-swap-" + operation + ".json"),
                new { Input = input, Original = originalMod, Build = build, Dependencies = proposed, SourceHashes = sourceHashes,
                    Guid = guid, OriginalMd5 = oldMd5, ReplacementMd5 = newMd5 }, bounded: false);
            _server.SwapCheckpointPlugin(operation, input.Mod, originalMod.Sha256, input.Sha256.ToLowerInvariant(),
                guid, oldMd5, newMd5, () => { Verify(); admission(); }, cancellation);
            Verify(); _verifyActivated = Verify;
        };
    }

    internal static void Validate(Input input)
    {
        if (input == null || input.Sha256 is not { Length: 64 } || !input.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("A swap requires the reviewed DLL's full SHA256.");
        foreach (string path in new[] { input.Mod, input.BuildInputs, input.Dependencies })
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path) ||
                path.IndexOfAny(['\r', '\n', '\0']) >= 0 || path.Split(['/', '\\']).Any(part => part is "." or ".."))
                throw new InvalidDataException("Swap inputs require absolute paths without traversal.");
        if (!Path.GetExtension(input.Mod).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A swap selects one plugin DLL.");
    }

    internal static void RequireRegular(string path, long maximum)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw new InvalidDataException("Swap file pins require absolute paths without traversal.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > maximum || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Swap input is missing, linked or too large: " + path);
        for (var parent = info.Directory; parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Swap input passes through a linked directory: " + path);
    }

    private static void RequireHash(string path, string hash)
    {
        RequireRegular(path, MaximumBytes);
        if (!FileHash.Sha256(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Swap input SHA256 changed: " + path);
    }

    internal static IReadOnlyDictionary<string, string> BoundedManifest(string root)
    {
        int count = 0; long size = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A swap input contains a link: " + path);
                if (++count > 10000) throw new InvalidDataException("A swap input contains too many entries.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else
                {
                    RequireRegular(path, MaximumBytes); size = checked(size + new FileInfo(path).Length);
                    if (size > MaximumBytes) throw new InvalidDataException("A swap input exceeds its size limit.");
                }
            }
        return WorldFixture.Manifest(root);
    }
}
