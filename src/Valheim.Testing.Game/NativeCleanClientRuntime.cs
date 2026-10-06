namespace Valheim.Testing.Game;

/// <summary>
/// An owned client with only the pinned ValheimCLI core and packs. It is the client half of a server-only native smoke:
/// selected server plugins and their dependencies are pinned absent when it joins. The source game is never edited.
/// </summary>
public sealed class NativeCleanClientRuntime : IDisposable
{
    public const string PasswordVariable = "VT_NATIVE_SMOKE_JOIN_PASSWORD";
    public WorldFixture Copy { get; }
    public string RuntimeDirectory => Copy.DirectoryPath;
    public string CliManifestFile { get; }
    public IReadOnlyDictionary<string, string> CliPins { get; }
    private readonly int _cliPort;

    private NativeCleanClientRuntime(WorldFixture copy, string manifest, Dictionary<string, string> pins, int cliPort)
    { Copy = copy; CliManifestFile = manifest; CliPins = pins; _cliPort = cliPort; }

    /// <summary>Copies a client install, clearing plugins, scripts, config and patchers only in the copy.
    /// An optional reviewed loader package supplies the copy's loader files and BepInEx config.</summary>
    public static NativeCleanClientRuntime Prepare(string source, string outputParent, NativeDependencyLock dependencies,
        int cliPort, BepInExLoaderPackage? loaderPackage = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        if (!dependencies.Ready || dependencies.CliFiles.Count == 0)
            throw new InvalidDataException("A ready dependency lock with a pinned ValheimCLI build is required.");
        if (cliPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(cliPort));
        source = Path.GetFullPath(source);
        _ = GameLaunch.DetectClient(source);
        loaderPackage?.Validate();
        if (loaderPackage != null && RegressionInputs.Inside(loaderPackage.Root, source))
            throw new InvalidOperationException("The client loader package must be an extracted, reviewed set outside the source game install.");
        dependencies.RequireExactCliSet(); // the one static check of the set: each manifest file once, one core
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in dependencies.CliFiles)
            foreach (var plugin in PluginMetadata.Read(file.File).Plugins) pins[plugin.Guid] = FileHash.Md5(file.File);

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
            foreach (string file in dependencies.CliFiles.Select(pinned => pinned.File))
            {
                string target = Path.Combine(bep, "plugins", Path.GetFileName(file));
                File.Copy(file, target);
                if (FileHash.Sha256(target) != FileHash.Sha256(file))
                    throw new IOException("A staged ValheimCLI file changed: " + file);
            }
            File.WriteAllText(Path.Combine(bep, "config", "valheimCLI.valheimCLI.cfg"),
                "[Server]\nEnabled = true\nAllowOnServerClients = true\nPort = " +
                cliPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            string manifest = Path.Combine(copy.DirectoryPath, "native-smoke-cli-manifest.json");
            dependencies.CliManifest.Write(manifest);
            return new NativeCleanClientRuntime(copy, manifest, pins, cliPort);
        }
        catch { copy.Dispose(); throw; }
    }

    /// <summary>A strict owned-client join plan. Every named server plugin is required absent.</summary>
    public ClientRunPlan Plan(int cliPort, int gamePort, IEnumerable<string> absentServerGuids)
    {
        ArgumentNullException.ThrowIfNull(absentServerGuids);
        if (cliPort != _cliPort)
            throw new ArgumentException("The client plan's CLI port must match the port staged in its disposable config.", nameof(cliPort));
        var pins = CliPins.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (string guid in absentServerGuids)
            if (!pins.TryAdd(guid, "absent"))
                throw new InvalidDataException("The clean client would load server plugin " + guid + ".");
        var plan = new ClientRunPlan
        {
            Mode = "owned", Install = RuntimeDirectory, Port = cliPort, Join = "127.0.0.1:" + gamePort,
            Character = DefaultSmokeCharacter.Name, PasswordVariable = PasswordVariable, Pins = pins,
            InstallPins = InstallPins.Of(RuntimeDirectory), CliManifest = CliManifestFile, Prepared = true,
            Capabilities = ["valheim.session/state", "valheim.session/join", "valheim.session/leave"],
        };
        plan.Validate(absentServerGuids.ToArray());
        plan.Preflight();
        return plan;
    }

    public void Dispose() => Copy.Dispose();
}
