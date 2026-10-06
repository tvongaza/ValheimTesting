using System.Security.Cryptography;

namespace Valheim.Testing.Game;

/// <summary>
/// A disposable dedicated-server runtime containing only the deliberately selected mods, their resolved dependencies,
/// one coherent ValheimCLI build and the test-only session adapter. The source runtime is read and pinned before copying;
/// the returned <see cref="WorldFixture"/> owns only the copy, which must outlive an owned server process.
/// </summary>
public sealed class NativeServerRuntime : IDisposable
{
    public const string SessionAdapterPluginGuid = "valheim.testing.native-smoke";
    public const string SessionCapability = "valheim.testing.native-smoke/session";
    public const string SessionTokenVariable = "VT_NATIVE_SMOKE_SESSION_TOKEN";
    public const string SelectedGuidsVariable = "VT_NATIVE_SMOKE_PLUGIN_GUIDS";
    public WorldFixture Copy { get; }
    public string RuntimeDirectory => Copy.DirectoryPath;
    public IReadOnlyDictionary<string, string> Pins { get; }
    public IReadOnlyList<string> SelectedGuids { get; }
    private readonly int _cliPort;

    private NativeServerRuntime(WorldFixture copy, Dictionary<string, string> pins, List<string> selectedGuids, int cliPort)
    { Copy = copy; Pins = pins; SelectedGuids = selectedGuids; _cliPort = cliPort; }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="outputParent"/> and replaces only the copy's plugin,
    /// script, config and patcher folders. Refuses duplicate staged file names or plugin GUIDs before copying.
    /// The adapter must declare exactly one BepInEx plugin. Explicit config files are copied by filename into the
    /// disposable runtime only. Explicit plugin sidecars (non-DLL files and directories such as an asset manifest and
    /// bundles) are copied beside the selected DLLs. An optional reviewed loader package replaces loader files in the
    /// copy after its old plugin and config folders are cleared. The returned manifest pins every staged file.
    /// </summary>
    public static NativeServerRuntime Prepare(string source, string outputParent, NativeDependencyLock dependencies,
        string adapter, int cliPort, IReadOnlyList<string>? configFiles = null,
        IReadOnlyList<string>? pluginFiles = null, IReadOnlyList<string>? pluginDirectories = null,
        BepInExLoaderPackage? loaderPackage = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        if (!dependencies.Ready || dependencies.Mods.Count == 0)
            throw new InvalidDataException("The native dependency lock must be ready and contain selected server mods.");
        if (cliPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(cliPort));
        source = Path.GetFullPath(source); adapter = Path.GetFullPath(adapter);
        loaderPackage?.Validate();
        if (loaderPackage != null && RegressionInputs.Inside(loaderPackage.Root, source))
            throw new InvalidOperationException("The loader package must be an extracted, reviewed set outside the source server install.");
        var configs = (configFiles ?? []).Select(Path.GetFullPath).ToList();
        var sidecarFiles = (pluginFiles ?? []).Select(Path.GetFullPath).ToList();
        var sidecarDirectories = (pluginDirectories ?? []).Select(Path.GetFullPath).ToList();
        foreach (string config in configs)
            if (!File.Exists(config)) throw new FileNotFoundException("An explicitly selected server config is missing.", config);
        foreach (string file in sidecarFiles)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("An explicitly selected plugin sidecar is missing.", file);
            if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin DLLs belong in --mod or a dependency search root, not a sidecar: " + file);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A plugin sidecar cannot be a link: " + file);
        }
        foreach (string directory in sidecarDirectories)
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("An explicitly selected plugin sidecar directory is missing: " + directory);
        var duplicateConfig = configs.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateConfig != null) throw new InvalidDataException("Two selected server configs share filename " + duplicateConfig.Key + ".");
        if (configs.Any(config => Path.GetFileName(config).Equals("valheimCLI.valheimCLI.cfg", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("ValheimCLI config is owned by the smoke; do not supply it as a mod config.");
        var configHashes = configs.ToDictionary(config => config, FileHash.Sha256, StringComparer.Ordinal);
        var sidecarHashes = sidecarFiles.ToDictionary(file => file, FileHash.Sha256, StringComparer.Ordinal);
        var directoryHashes = sidecarDirectories.ToDictionary(directory => directory, WorldFixture.Manifest, StringComparer.Ordinal);
        if (directoryHashes.Any(entry => entry.Value.Keys.Any(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Plugin sidecar directories cannot contain DLLs; select each plugin or library through dependency resolution.");
        GameLaunch.DetectServer(source);
        if (!File.Exists(adapter)) throw new FileNotFoundException("The test-only session adapter is missing.", adapter);
        var files = dependencies.CliFiles.Concat(dependencies.Mods).Concat(dependencies.Plugins)
            .Select(file => file.File).Append(adapter).ToList();
        var duplicateName = files.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateName != null) throw new InvalidDataException("Two selected server files share the plugin filename " + duplicateName.Key + ".");
        var sidecarNames = sidecarFiles.Select(Path.GetFileName).Concat(sidecarDirectories.Select(Path.GetFileName)).ToList();
        var duplicateSidecar = sidecarNames.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateSidecar != null || sidecarNames.Any(name => files.Any(file => Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Plugin sidecars must have distinct names and cannot replace a selected DLL.");
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = new List<string>();
        var selectedFiles = dependencies.Mods.Select(file => file.File).ToHashSet(StringComparer.Ordinal);
        foreach (string file in files)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("A selected server file is missing.", file);
            var pinned = dependencies.CliFiles.Concat(dependencies.Mods).Concat(dependencies.Plugins).FirstOrDefault(item => item.File == file);
            if (pinned != null && !FileHash.Sha256(file).Equals(pinned.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A selected server file changed after dependency resolution: " + file);
            var metadata = PluginMetadata.Read(file);
            if (file == adapter && (metadata.Plugins.Count != 1 || metadata.Plugins[0].Guid != SessionAdapterPluginGuid))
                throw new InvalidDataException("The test-only session adapter must declare exactly one " + SessionAdapterPluginGuid + " BepInEx plugin.");
            foreach (var plugin in metadata.Plugins)
            {
                if (!pins.TryAdd(plugin.Guid, FileHash.Md5(file)))
                    throw new InvalidDataException("Two selected server files declare plugin " + plugin.Guid + ".");
                if (selectedFiles.Contains(file))
                {
                    if (plugin.Guid.Contains(';')) throw new InvalidDataException("A selected server plugin GUID contains the session list separator: " + plugin.Guid);
                    selected.Add(plugin.Guid);
                }
            }
        }
        if (selected.Count == 0) throw new InvalidDataException("No deliberately selected server plugin GUID was found.");

        var copy = WorldFixture.Copy(source, outputParent, WorldFixture.Manifest(source));
        try
        {
            string bep = Path.Combine(copy.DirectoryPath, "BepInEx");
            foreach (string name in new[] { "plugins", "scripts", "config", "patchers" })
            {
                string folder = Path.Combine(bep, name);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.CreateDirectory(folder);
            }
            loaderPackage?.Apply(copy.DirectoryPath);
            string plugins = Path.Combine(bep, "plugins");
            foreach (string file in files)
            {
                string target = Path.Combine(plugins, Path.GetFileName(file));
                File.Copy(file, target);
                if (FileHash.Sha256(target) != FileHash.Sha256(file)) throw new IOException("Staged server file changed while copying: " + file);
            }
            foreach (string file in sidecarFiles)
            {
                string target = Path.Combine(plugins, Path.GetFileName(file));
                File.Copy(file, target);
                if (!FileHash.Sha256(target).Equals(sidecarHashes[file], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A staged plugin sidecar changed while copying: " + file);
            }
            foreach (string directory in sidecarDirectories)
            {
                string target = Path.Combine(plugins, Path.GetFileName(directory));
                foreach (var (relative, hash) in directoryHashes[directory])
                {
                    string destination = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(Path.Combine(directory, relative), destination);
                    if (!FileHash.Sha256(destination).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A staged plugin sidecar changed while copying: " + relative);
                }
                WorldFixture.Verify(directory, directoryHashes[directory]);
            }
            File.WriteAllText(Path.Combine(bep, "config", "valheimCLI.valheimCLI.cfg"),
                "[Server]\nEnabled = true\nPort = " + cliPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            foreach (string config in configs)
            {
                if (!FileHash.Sha256(config).Equals(configHashes[config], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A selected server config changed while staging: " + config);
                string target = Path.Combine(bep, "config", Path.GetFileName(config));
                File.Copy(config, target);
                if (!FileHash.Sha256(target).Equals(configHashes[config], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A staged server config changed while copying: " + config);
            }
            return new NativeServerRuntime(copy, pins, selected, cliPort);
        }
        catch { copy.Dispose(); throw; }
    }

    /// <summary>The strict SHA256 manifest of the staged copy, for a following <c>PinnedServerRun</c>.</summary>
    public IReadOnlyDictionary<string, string> Manifest() => WorldFixture.Manifest(RuntimeDirectory);

    /// <summary>
    /// Creates a fully pinned, private dedicated-server plan for the packaged smoke world. The server is unlisted,
    /// its password is generated for this run and its test-only session token is supplied only by
    /// <c>PinnedServerRun</c>. The caller must keep the staged runtime alive through the run.
    /// </summary>
    public ServerRunPlan Plan(string worldRoot, int cliPort, int gamePort = 2456, string? password = null)
    {
        if (cliPort is < 1024 or > 65535 || gamePort is < 1024 or > 65533)
            throw new ArgumentOutOfRangeException(nameof(cliPort), "Choose available CLI and game ports in their supported ranges.");
        if (cliPort != _cliPort)
            throw new ArgumentException("The server plan's CLI port must match the port staged in its disposable config.", nameof(cliPort));
        worldRoot = Path.GetFullPath(worldRoot);
        string world = Path.Combine(worldRoot, "worlds_local", DefaultSmokeWorld.Name);
        if (!Directory.Exists(world) || !File.Exists(Path.Combine(world, "_main.1.fwl2")))
            throw new InvalidDataException("The dedicated smoke world is not in worlds_local/" + DefaultSmokeWorld.Name + ".");
        var identity = WorldIdentity.Read(Path.Combine(worldRoot, "worlds_local"));
        if (identity.Name != DefaultSmokeWorld.Name || identity.UidText != DefaultSmokeWorld.Uid || identity.SeedName != DefaultSmokeWorld.SeedName)
            throw new InvalidDataException("The dedicated smoke world metadata differs from the packaged fixture.");
        password ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        if (password.Length < 5 || password.Any(char.IsWhiteSpace))
            throw new ArgumentException("The private server password needs at least five non-space characters.", nameof(password));
        var plan = new ServerRunPlan
        {
            Scenario = "native-smoke-server-load",
            Runtime = new PinnedDirectory { Source = RuntimeDirectory, Sha256 = Manifest().ToDictionary(entry => entry.Key, entry => entry.Value) },
            World = new PinnedDirectory { Source = worldRoot, Sha256 = WorldFixture.Manifest(worldRoot).ToDictionary(entry => entry.Key, entry => entry.Value) },
            RuntimePins = InstallPins.Of(RuntimeDirectory),
            Executable = ServerRunPlan.ExecutableFor(GameLaunch.DetectServer(RuntimeDirectory)),
            Arguments = ["-batchmode", "-nographics", "-name", DefaultSmokeWorld.Name,
                "-port", gamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-world", DefaultSmokeWorld.Name, "-password", password, "-public", "0",
                "-savedir", "{world}", "-logFile", "{runtime}/toolkit-unity.log"],
            Environment = new Dictionary<string, string> { [SelectedGuidsVariable] = string.Join(";", SelectedGuids) },
            Pins = Pins.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            Port = cliPort,
            // This is a disposable plugin-load smoke: it asserts no save-on-quit or crossplay retirement. Keep the
            // bounded kill fallback prompt when a host cannot deliver a console quit (the general runner keeps 120 s).
            QuitSeconds = 20,
        };
        plan.Pins["worlduid"] = DefaultSmokeWorld.Uid;
        plan.ValidateServerPlan(Pins.Keys, SessionTokenVariable);
        return plan;
    }

    public void Dispose() => Copy.Dispose();
}
