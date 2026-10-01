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

    private NativeServerRuntime(WorldFixture copy, Dictionary<string, string> pins, List<string> selectedGuids)
    { Copy = copy; Pins = pins; SelectedGuids = selectedGuids; }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="outputParent"/> and replaces only the copy's plugin,
    /// script, config and patcher folders. Refuses duplicate staged file names or plugin GUIDs before copying.
    /// The adapter must declare exactly one BepInEx plugin; its own SHA256 joins the returned copy's manifest.
    /// </summary>
    public static NativeServerRuntime Prepare(string source, string outputParent, NativeDependencyLock dependencies,
        string adapter, int cliPort)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        if (!dependencies.Ready || dependencies.Mods.Count == 0)
            throw new InvalidDataException("The native dependency lock must be ready and contain selected server mods.");
        if (cliPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(cliPort));
        source = Path.GetFullPath(source); adapter = Path.GetFullPath(adapter);
        ServerLaunch.Detect(source);
        if (!File.Exists(adapter)) throw new FileNotFoundException("The test-only session adapter is missing.", adapter);
        var files = dependencies.CliFiles.Concat(dependencies.Mods).Concat(dependencies.Plugins)
            .Select(file => file.File).Append(adapter).ToList();
        var duplicateName = files.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateName != null) throw new InvalidDataException("Two selected server files share the plugin filename " + duplicateName.Key + ".");
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = new List<string>();
        var selectedFiles = dependencies.Mods.Select(file => file.File).ToHashSet(StringComparer.Ordinal);
        foreach (string file in files)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("A selected server file is missing.", file);
            var pinned = dependencies.CliFiles.Concat(dependencies.Mods).Concat(dependencies.Plugins).FirstOrDefault(item => item.File == file);
            if (pinned != null && !WorldFixture.Hash(file).Equals(pinned.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A selected server file changed after dependency resolution: " + file);
            var metadata = PluginMetadata.Read(file);
            if (file == adapter && (metadata.Plugins.Count != 1 || metadata.Plugins[0].Guid != SessionAdapterPluginGuid))
                throw new InvalidDataException("The test-only session adapter must declare exactly one " + SessionAdapterPluginGuid + " BepInEx plugin.");
            foreach (var plugin in metadata.Plugins)
            {
                if (!pins.TryAdd(plugin.Guid, PluginPins.Md5(file)))
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
            string plugins = Path.Combine(bep, "plugins");
            foreach (string file in files)
            {
                string target = Path.Combine(plugins, Path.GetFileName(file));
                File.Copy(file, target);
                if (WorldFixture.Hash(target) != WorldFixture.Hash(file)) throw new IOException("Staged server file changed while copying: " + file);
            }
            File.WriteAllText(Path.Combine(bep, "config", "valheimCLI.valheimCLI.cfg"),
                "[Server]\nEnabled = true\nPort = " + cliPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            return new NativeServerRuntime(copy, pins, selected);
        }
        catch { copy.Dispose(); throw; }
    }

    /// <summary>The strict SHA256 manifest of the staged copy, for a following <see cref="PinnedServerRun"/>.</summary>
    public IReadOnlyDictionary<string, string> Manifest() => WorldFixture.Manifest(RuntimeDirectory);

    /// <summary>
    /// Creates a fully pinned, private dedicated-server plan for the packaged smoke world. The server is unlisted,
    /// its password is generated for this run and its test-only session token is supplied only by
    /// <see cref="PinnedServerRun"/>. The caller must keep the staged runtime alive through the run.
    /// </summary>
    public ServerRunPlan Plan(string worldRoot, int cliPort, int gamePort = 2456)
    {
        if (cliPort is < 1024 or > 65535 || gamePort is < 1024 or > 65533)
            throw new ArgumentOutOfRangeException(nameof(cliPort), "Choose available CLI and game ports in their supported ranges.");
        worldRoot = Path.GetFullPath(worldRoot);
        string world = Path.Combine(worldRoot, "worlds_local", DefaultSmokeWorld.Name);
        if (!Directory.Exists(world) || !File.Exists(Path.Combine(world, "_main.1.fwl2")))
            throw new InvalidDataException("The dedicated smoke world is not in worlds_local/" + DefaultSmokeWorld.Name + ".");
        var identity = WorldIdentity.Read(Path.Combine(worldRoot, "worlds_local"));
        if (identity.Name != DefaultSmokeWorld.Name || identity.UidText != DefaultSmokeWorld.Uid || identity.SeedName != DefaultSmokeWorld.SeedName)
            throw new InvalidDataException("The dedicated smoke world metadata differs from the packaged fixture.");
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var plan = new ServerRunPlan
        {
            Scenario = "native-smoke-server-load",
            Runtime = new PinnedDirectory { Source = RuntimeDirectory, Sha256 = Manifest().ToDictionary(entry => entry.Key, entry => entry.Value) },
            World = new PinnedDirectory { Source = worldRoot, Sha256 = WorldFixture.Manifest(worldRoot).ToDictionary(entry => entry.Key, entry => entry.Value) },
            RuntimePins = InstallPins.Of(RuntimeDirectory),
            Executable = ServerRunPlan.ExecutableFor(ServerLaunch.Detect(RuntimeDirectory)),
            Arguments = ["-batchmode", "-nographics", "-name", DefaultSmokeWorld.Name,
                "-port", gamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-world", DefaultSmokeWorld.Name, "-password", password, "-public", "0",
                "-savedir", "{world}", "-logFile", "{runtime}/toolkit-unity.log"],
            Environment = new Dictionary<string, string> { [SelectedGuidsVariable] = string.Join(";", SelectedGuids) },
            Pins = Pins.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            Port = cliPort,
        };
        plan.Pins["worlduid"] = DefaultSmokeWorld.Uid;
        plan.ValidateServerPlan(Pins.Keys, SessionTokenVariable);
        return plan;
    }

    public void Dispose() => Copy.Dispose();
}
