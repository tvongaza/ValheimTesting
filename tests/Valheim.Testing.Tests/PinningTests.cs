using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml.Linq;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

/// <summary>Fake install trees: a game assembly in the platform's data folder and a BepInEx core (preloader and BepInEx.dll), no game. Existing files are kept.</summary>
internal static class FakeInstalls
{
    public static void Server(string root) => Write(root, Path.Combine("valheim_server_Data", "Managed"));
    /// <summary>
    /// A complete Valheim 1.0 world <c>&lt;name&gt;/</c> in <paramref name="directory"/>, as the game saves one: save
    /// <paramref name="save"/>'s <c>_main.&lt;n&gt;.fwl2</c> (version 41, the name, seed and UID the campaign reviews), <c>.db2</c>,
    /// <c>.chunks</c> listing one chunk, <c>.ok</c> and that chunk file.
    /// </summary>
    public static string World(string directory, string name = "Campaign", long uid = 4242L, string seed = "AbCdEf1234", int save = 1)
    {
        string world = Path.Combine(directory, name);
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, $"_main.{save}.fwl2"), Metadata(name, uid, seed));
        File.WriteAllText(Path.Combine(world, $"_main.{save}.db2"), "fixture");
        File.WriteAllBytes(Path.Combine(world, $"_main.{save}.chunks"), ChunkList((0, 0, 1)));
        File.WriteAllBytes(Path.Combine(world, $"_main.{save}.ok"), BitConverter.GetBytes(41));
        File.WriteAllText(Path.Combine(world, ChunkFile), "chunk");
        return directory;
    }
    /// <summary>The game's automatic backup of the world <see cref="World"/> wrote, beside it: <c>&lt;name&gt;_backup_auto-&lt;stamp&gt;/</c> holding save 0.</summary>
    public static string Backup(string directory, string name = "Campaign", long uid = 4242L, string stamp = "20261020-120000")
    {
        string backup = Path.Combine(directory, name + "_backup_auto-" + stamp);
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "_main.0.fwl2"), Metadata(name, uid));
        File.WriteAllText(Path.Combine(backup, "_main.0.db2"), "backup");
        File.WriteAllBytes(Path.Combine(backup, "_main.0.chunks"), ChunkList());
        File.WriteAllBytes(Path.Combine(backup, "_main.0.ok"), BitConverter.GetBytes(41));
        return directory;
    }
    /// <summary>The chunk file <see cref="World"/> writes and its <c>.chunks</c> lists.</summary>
    public const string ChunkFile = "00_00__0_1.chunk";
    /// <summary>
    /// World metadata (<c>.fwl2</c>) as the game's package writes it (BinaryWriter): version 41, name, seed name, seed, UID, the
    /// world-generation version and whether the world has saved data (false only for a world never saved).
    /// </summary>
    public static byte[] Metadata(string name, long uid, string seed = "AbCdEf1234", bool needsDb = true)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        { writer.Write(41); writer.Write(name); writer.Write(seed); writer.Write(1234); writer.Write(uid); writer.Write(2); writer.Write(needsDb); writer.Write(0); writer.Write(0); }
        return [.. BitConverter.GetBytes((int)payload.Length), .. payload.ToArray()];
    }
    /// <summary>A <c>_main.&lt;n&gt;.chunks</c> listing these chunks (index, size, version), as the game writes one.</summary>
    public static byte[] ChunkList(params (ushort Chunk, byte Size, uint Version)[] chunks)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload))
        {
            writer.Write((short)41); writer.Write(chunks.Length * 3); writer.Write(chunks.Length);
            foreach (var (chunk, size, version) in chunks) { writer.Write(chunk); writer.Write(size); writer.Write(version); writer.Write(3); }
        }
        return payload.ToArray();
    }
    public static void Client(string root) => Write(root, Path.Combine("valheim_Data", "Managed"));
    private static void Write(string root, string managed)
    {
        Keep(Path.Combine(root, managed, InstallPins.GameAssemblyName), GameAssembly());
        Keep(Path.Combine(root, managed, "assembly_utils.dll"), "utils build 1");
        Keep(Path.Combine(root, managed, "UnityEngine.dll"), "unity");
        Keep(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"), "bepinex 5.4.23");
        Keep(Path.Combine(root, "BepInEx", "core", "BepInEx.Preloader.dll"), "bepinex preloader 5.4.23");
    }
    /// <summary>A Linux dedicated-server runtime's Doorstop library, which a BepInEx launch needs beside the core.</summary>
    public static void LinuxLoader(string root) => Keep(Path.Combine(root, "doorstop_libs", "libdoorstop_x64.so"), "doorstop 4");
    /// <summary>A macOS install's Doorstop library at its root.</summary>
    public static void MacLoader(string root) => Keep(Path.Combine(root, "libdoorstop.dylib"), "doorstop 4");
    /// <summary>A macOS client install: its bundle's game and managed folder, BepInEx's core and a Doorstop library at the root.</summary>
    public static void MacClient(string root)
    {
        Keep(Path.Combine(root, GameLaunch.ClientMacBundle, "Contents", "MacOS", "Valheim"), "client");
        Write(root, Path.Combine(GameLaunch.ClientMacBundle, "Contents", "Resources", "Data", "Managed"));
        MacLoader(root);
    }
    private static void Keep(string path, string text) => Keep(path, System.Text.Encoding.UTF8.GetBytes(text));
    private static void Keep(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllBytes(path, content);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Assemblies = new();
    /// <summary>
    /// A game assembly as Valheim's declares its build, and nothing else: a global <c>Version</c> with <c>const uint c_networkVersion</c>
    /// and a static constructor that stores a decoy <c>GameVersion(0, 214, 301)</c> before <paramref name="gameVersion"/> in
    /// <c>CurrentVersion</c>'s backing field, as the game's does, beside a nested decoy type also named <c>Version</c>.
    /// Defaults to Valheim 1.0.17 (network version 40).
    /// </summary>
    public static byte[] GameAssembly(string gameVersion = "1.0.17", uint networkVersion = 40) => Emit(gameVersion, networkVersion);
    /// <summary>A Valheim 1.0.17 game assembly whose <c>c_networkVersion</c> is <paramref name="networkConstant"/> (any const type), or a plain static field for null.</summary>
    public static byte[] GameAssembly(object? networkConstant) => Emit("1.0.17", networkConstant);
    private static byte[] Emit(string gameVersion, object? networkConstant) => Assemblies.GetOrAdd($"{gameVersion}|{networkConstant?.GetType().Name}:{networkConstant}", _ =>
    {
        var assembly = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("assembly_valheim"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("assembly_valheim");
        var gameVersionType = module.DefineType("GameVersion", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed |
            System.Reflection.TypeAttributes.SequentialLayout, typeof(ValueType));
        var constructor = gameVersionType.DefineConstructor(System.Reflection.MethodAttributes.Public, System.Reflection.CallingConventions.Standard, [typeof(int), typeof(int), typeof(int)]);
        constructor.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        var version = module.DefineType("Version", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Abstract);
        if (networkConstant == null) version.DefineField("c_networkVersion", typeof(uint), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        else version.DefineField("c_networkVersion", networkConstant.GetType(), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static |
            System.Reflection.FieldAttributes.Literal).SetConstant(networkConstant);
        var first = version.DefineField("FirstVersionWithNetworkVersion", gameVersionType, System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var current = version.DefineField("<CurrentVersion>k__BackingField", gameVersionType, System.Reflection.FieldAttributes.Private | System.Reflection.FieldAttributes.Static);
        var il = version.DefineTypeInitializer().GetILGenerator();
        foreach (var (field, parts) in new[] { (first, new[] { 0, 214, 301 }), (current, gameVersion.Split('.').Select(int.Parse).ToArray()) })
        {
            foreach (int part in parts) il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, part);
            il.Emit(System.Reflection.Emit.OpCodes.Newobj, constructor);
            il.Emit(System.Reflection.Emit.OpCodes.Stsfld, field);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        var outer = module.DefineType("Decoys", System.Reflection.TypeAttributes.Public);
        outer.DefineNestedType("Version", System.Reflection.TypeAttributes.NestedPublic).CreateType();
        outer.CreateType();
        gameVersionType.CreateType();
        version.CreateType();
        using var image = new MemoryStream();
        assembly.Save(image);
        return image.ToArray();
    });
}

// Install, plugin and world pins.
public sealed class PinsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pins-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string Install => Path.Combine(_root, "install");

    // ---- install pins ----

    [Fact] public void AProfilePinChecksGameCodeInTheSourceAndLoaderInTheProfile()
    {
        FakeInstalls.Client(Install);
        string profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(profile, "BepInEx", "core", "BepInEx.Preloader.dll"), "profile preloader");
        File.WriteAllText(Path.Combine(profile, "libdoorstop.dylib"), "profile doorstop");
        var expected = InstallPins.Of(Install, profile);
        Assert.Equal(InstallPins.GameHash(Install), expected.Game);
        Assert.Equal(InstallPins.LoaderHash(profile), expected.Loader);
        expected.Check(Install, profile, "client profile");

        // Neither a different game build nor a different profile loader can pass the same pin.
        File.WriteAllText(Path.Combine(Install, "valheim_Data", "Managed", InstallPins.GameAssemblyName), "changed game");
        Assert.Contains("game build differs", Assert.Throws<InvalidOperationException>(() => expected.Check(Install, profile, "client profile")).Message);
        File.WriteAllBytes(Path.Combine(Install, "valheim_Data", "Managed", InstallPins.GameAssemblyName), FakeInstalls.GameAssembly());
        File.WriteAllText(Path.Combine(profile, "libdoorstop.dylib"), "changed doorstop");
        Assert.Contains("loader differs", Assert.Throws<InvalidOperationException>(() => expected.Check(Install, profile, "client profile")).Message);
    }

    [Fact] public void AFolderHashIsTheSha256OfItsSha256sumListing()
    {
        string core = Path.Combine(_root, "core");
        Directory.CreateDirectory(Path.Combine(core, "sub"));
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "core"); File.WriteAllText(Path.Combine(core, "sub", "Harmony.dll"), "harmony");
        // Independently computed: `find . -type f ! -name .DS_Store ! -name '._*' | sed 's|^./||' | LC_ALL=C sort |
        // while read f; do printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"; done | sha256sum`.
        const string expected = "9e6d27b3afaa350014b2dd4471bac1be23e7c393918834d56f396f7d2db3fadf";
        Assert.Equal(expected, InstallPins.DirectoryHash(core));
        // Finder and AppleDouble files that a copy through macOS adds are not part of the listing.
        File.WriteAllText(Path.Combine(core, ".DS_Store"), "finder"); File.WriteAllText(Path.Combine(core, "sub", "._Harmony.dll"), "appledouble");
        Assert.Equal(expected, InstallPins.DirectoryHash(core));
        // The control: any other added file changes it.
        File.WriteAllText(Path.Combine(core, "sub", "_Harmony.dll"), "x");
        Assert.NotEqual(expected, InstallPins.DirectoryHash(core));
        // Empty and absent folders both hash the empty listing.
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", InstallPins.DirectoryHash(Path.Combine(_root, "empty")));
        Assert.Equal(InstallPins.DirectoryHash(Path.Combine(_root, "empty")), InstallPins.DirectoryHash(Path.Combine(_root, "absent")));
    }

    [Fact] public void TheGamePinIsTheListingOfEveryGameAssembly()
    {
        string managed = Path.Combine(Install, "valheim_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "assembly_valheim.dll"), "valheim"); File.WriteAllText(Path.Combine(managed, "assembly_utils.dll"), "utils");
        File.WriteAllText(Path.Combine(managed, "assembly_guiutils.dll"), "guiutils"); File.WriteAllText(Path.Combine(managed, "UnityEngine.dll"), "engine");
        File.WriteAllText(Path.Combine(managed, ".DS_Store"), "finder"); File.WriteAllText(Path.Combine(managed, "._assembly_valheim.dll"), "appledouble");
        // Independently computed: `ls assembly_*.dll | LC_ALL=C sort | while read f; do printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"; done | sha256sum`.
        Assert.Equal("b747c41472cc84979b104fd21d041a2fe6ffc93578d1560cbc621ef698c28c07", InstallPins.GameHash(Install));
        // Unity's own assemblies are not the game's build.
        File.WriteAllText(Path.Combine(managed, "UnityEngine.dll"), "engine 2");
        Assert.Equal("b747c41472cc84979b104fd21d041a2fe6ffc93578d1560cbc621ef698c28c07", InstallPins.GameHash(Install));
    }

    [Fact] public void TheSameInstallPassesAndMacMetadataIsIgnored()
    {
        FakeInstalls.Client(Install);
        Directory.CreateDirectory(Path.Combine(Install, "BepInEx", "patchers"));
        var pins = InstallPins.Of(Install);
        pins.Validate("client install");
        Assert.Equal(pins.Game, pins.Check(Install, "client install").Game);
        // A copy through macOS adds Finder and AppleDouble files everywhere; none of them changes a pin.
        foreach (string folder in new[] { "", Path.Combine("valheim_Data", "Managed"), Path.Combine("BepInEx", "core"), Path.Combine("BepInEx", "patchers") })
        {
            File.WriteAllText(Path.Combine(Install, folder, ".DS_Store"), "finder");
            File.WriteAllText(Path.Combine(Install, folder, "._BepInEx.dll"), "appledouble");
        }
        File.WriteAllText(Path.Combine(Install, "valheim_Data", "Managed", "._assembly_valheim.dll"), "appledouble");
        pins.Check(Install, "client install");
    }

    // Negative controls: each changed part is refused and named, and only it.
    [Theory]
    [InlineData("game", "the game build differs")]
    [InlineData("game-utils", "the game build differs")]
    [InlineData("core", "the loader differs")]
    [InlineData("core-added", "the loader differs")]
    [InlineData("proxy", "the loader differs")]
    [InlineData("proxy-removed", "the loader differs")]
    [InlineData("doorstop-config", "the loader differs")]
    [InlineData("doorstop-library", "the loader differs")]
    [InlineData("mac-doorstop-added", "the loader differs")]
    [InlineData("patcher", "the patchers differ")]
    public void AChangedGameBuildLoaderOrPatcherIsRefused(string change, string message)
    {
        FakeInstalls.Client(Install);
        Directory.CreateDirectory(Path.Combine(Install, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(Install, "BepInEx", "patchers", "Hooks.dll"), "patcher 1");
        WindowsLoader(Install);
        var pins = InstallPins.Of(Install);
        switch (change)
        {
            case "game": File.WriteAllText(Path.Combine(Install, "valheim_Data", "Managed", "assembly_valheim.dll"), "game build 2 (hotfix)"); break;
            case "game-utils": File.WriteAllText(Path.Combine(Install, "valheim_Data", "Managed", "assembly_utils.dll"), "utils build 2"); break;
            case "core": File.WriteAllText(Path.Combine(Install, "BepInEx", "core", "BepInEx.dll"), "bepinex 5.4.24"); break;
            case "core-added": File.WriteAllText(Path.Combine(Install, "BepInEx", "core", "Extra.dll"), "x"); break;
            // A mod manager's launch replaced the pack's Doorstop 3 proxy with its Doorstop 4 one: core unchanged, loader not.
            case "proxy": File.WriteAllText(Path.Combine(Install, "winhttp.dll"), "MZ target_assembly"); break;
            case "proxy-removed": File.Delete(Path.Combine(Install, "winhttp.dll")); break;
            case "doorstop-config": File.WriteAllText(Path.Combine(Install, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n"); break;
            case "doorstop-library": File.WriteAllText(Path.Combine(Install, "doorstop_libs", "libdoorstop_x64.so"), "doorstop 4"); break;
            case "mac-doorstop-added": File.WriteAllText(Path.Combine(Install, "libdoorstop.dylib"), "universal"); break;
            case "patcher": File.WriteAllText(Path.Combine(Install, "BepInEx", "patchers", "Hooks.dll"), "patcher 2"); break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => pins.Check(Install, "client install"));
        Assert.Contains(message, error.Message);
        Assert.Equal(1, new[] { "the game build differs", "the loader differs", "the patchers differ" }.Count(error.Message.Contains));
    }

    // BepInExPack's Doorstop 3 loader beside the core: the proxy, its configuration and its libraries.
    private static void WindowsLoader(string root)
    {
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "MZ targetAssembly");
        File.WriteAllText(Path.Combine(root, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
        Directory.CreateDirectory(Path.Combine(root, "doorstop_libs"));
        File.WriteAllText(Path.Combine(root, "doorstop_libs", "libdoorstop_x64.so"), "doorstop 3");
    }

    [Fact] public void TheLoaderPinIsTheListingOfTheLoaderFilesFromTheRoot()
    {
        FakeInstalls.Client(Install);
        WindowsLoader(Install);
        // What InstallPins documents and the shell recipe computes: sha256sum's lines for the loader files, relative to the root.
        string Line(string relative) => FileHash.Sha256(Path.Combine(Install, relative.Replace('/', Path.DirectorySeparatorChar))) + "  " + relative + "\n";
        string listing = string.Concat(new[] { "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll", "doorstop_config.ini", "doorstop_libs/libdoorstop_x64.so", "winhttp.dll" }
            .Order(StringComparer.Ordinal).Select(Line));
        string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(listing))).ToLowerInvariant();
        Assert.Equal(expected, InstallPins.Of(Install).Loader);
        Assert.Equal(expected, InstallPins.LoaderHash(Install));
        // Negative control: what is not loader leaves it alone (BepInEx rewrites BepInEx.cfg; the rest is the game's or a mod's).
        Directory.CreateDirectory(Path.Combine(Install, "BepInEx", "config"));
        File.WriteAllText(Path.Combine(Install, "BepInEx", "config", "BepInEx.cfg"), "[Logging.Console]\nEnabled = true\n");
        File.WriteAllText(Path.Combine(Install, ".doorstop_version"), "4.0.0");
        File.WriteAllText(Path.Combine(Install, "start_game_bepinex.sh"), "#!/bin/sh");
        Directory.CreateDirectory(Path.Combine(Install, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(Install, "BepInEx", "plugins", "Mod.dll"), "mod");
        Assert.Equal(expected, InstallPins.Of(Install).Loader);
    }

    // The loader identity covers every file a launch requires (#364's one list), so no required file can change unnoticed.
    [Fact] public void TheLoaderPinCoversEveryFileALaunchRequires()
    {
        foreach (string file in BepInExLoader.LoaderFiles(ClientPlatform.Windows).Concat(BepInExLoader.LoaderFiles(ClientPlatform.Linux)).Concat(GameLaunch.MacDoorstopFiles))
            Assert.True(InstallPins.IsLoaderFile(file), file);
        Assert.Equal(["winhttp.dll", "doorstop_config.ini", "libdoorstop.dylib", "BepInEx/core", "doorstop_libs"], InstallPins.LoaderEntries);
    }

    [Fact] public void TheGameAssemblyIsFoundInEachLayoutAndNeverGuessed()
    {
        string mac = Path.Combine(_root, "mac", "Valheim.app", "Contents", "Resources", "Data", "Managed");
        Directory.CreateDirectory(mac); File.WriteAllText(Path.Combine(mac, "assembly_valheim.dll"), "mac");
        Assert.Equal(Path.Combine(mac, "assembly_valheim.dll"), InstallPins.GameAssembly(Path.Combine(_root, "mac")));
        string macServer = Path.Combine(_root, "mac-server", "valheim_server", "Data", "Managed");
        Directory.CreateDirectory(macServer); File.WriteAllText(Path.Combine(macServer, "assembly_valheim.dll"), "mac server");
        Assert.Equal(Path.Combine(macServer, "assembly_valheim.dll"), InstallPins.GameAssembly(Path.Combine(_root, "mac-server")));
        FakeInstalls.Server(Install);
        Assert.EndsWith(Path.Combine("valheim_server_Data", "Managed", "assembly_valheim.dll"), InstallPins.GameAssembly(Install));
        FakeInstalls.Client(Install); // Now two data folders: refused rather than guessed.
        Assert.Throws<InvalidOperationException>(() => InstallPins.GameAssembly(Install));
        Directory.CreateDirectory(Path.Combine(_root, "none"));
        Assert.Throws<FileNotFoundException>(() => InstallPins.GameAssembly(Path.Combine(_root, "none")));
        Assert.Throws<ArgumentException>(() => new InstallPins { Game = new string('a', 64), Loader = "abc", Patchers = new string('a', 64) }.Validate("runtime"));
    }

    // ---- server plans ----

    private static ServerRunPlan Plan()
    {
        var plan = new ServerRunPlan
        {
            Runtime = new() { Source = Path.GetTempPath() }, World = new() { Source = Path.GetTempPath() },
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"],
        };
        return plan;
    }
    private static void Pin(ServerRunPlan plan)
    {
        plan.Runtime.Sha256["a"] = new string('a', 64); plan.World.Sha256["b"] = new string('b', 64);
        plan.Pins["worlduid"] = "1"; plan.Pins["my.mod"] = new string('1', 32);
        plan.RuntimePins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) };
    }

    [Fact] public void AStrictPlanAcceptsOnlyThePinsItIsGiven()
    {
        var plan = Plan(); Pin(plan);
        plan.ValidateServerPlan(["my.mod"], "TOKEN");
        Assert.StartsWith("cli_expect", plan.ExpectCommand);
        plan.RuntimePins = null;
        plan.Pins.Clear(); plan.Runtime.Sha256.Clear(); plan.World.Sha256.Clear();
        plan.ValidateServerPlan(["my.mod"], "TOKEN");
    }

    [Fact] public void ASlowNetworkGenerationHasOneBoundedServerStartupBudget()
    {
        var plan = Plan(); Pin(plan);
        plan.StartupSeconds = 3600;
        plan.ValidateServerPlan(["my.mod"], "TOKEN");
        plan.StartupSeconds = 3601;
        Assert.Contains("time budget", Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan(["my.mod"], "TOKEN")).Message);
    }

    [Fact] public void TheRemovedPlanFieldIsRefusedAsUnknown()
    {
        string path = Path.Combine(_root, "plan.json");
        string removed = "pin" + "ning";
        File.WriteAllText(path, "{\"" + removed + "\":\"none\"}");
        Assert.Throws<JsonException>(() => ServerRunPlan.Read<ServerRunPlan>(path));
        Assert.Throws<JsonException>(() => ClientPlanFile.Read<ClientRunPlan>(path));
    }

    // ---- the runner ----

    // The server install a standalone run on this machine stages its profile from (#730): on a fake Linux local host, any OS.
    private LocalServerMachine? _machine;
    private LocalServerMachine Machine => _machine ??= new LocalServerMachine(Path.Combine(_root, "local-host"));
    private string Runtime => Machine.InstallDirectory;
    private string World => Path.Combine(_root, "world");
    private static async Task<int> RunHere(string[] args, PinnedServerRunOptions<ServerRunPlan> options)
    {
        using var machine = LocalServerMachine.Use();
        return await PinnedServerRun.MainAsync(args, options);
    }
    private string WritePlan(InstallPins? runtimePins = null, bool manifests = true)
    {
        FakeInstalls.World(Path.Combine(World, "worlds_local"), "Test", 1);
        var plan = new Dictionary<string, object>
        {
            ["runtime"] = new { source = LocalServerMachine.Install, sha256 = manifests ? WorldFixture.Manifest(Runtime) : new Dictionary<string, string>() },
            ["world"] = new { source = World, sha256 = manifests ? WorldFixture.Manifest(World) : new Dictionary<string, string>() },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            ["port"] = FreePort(),
        };
        plan["pins"] = new Dictionary<string, string> { ["worlduid"] = "1" }; plan["runtimePins"] = runtimePins ?? InstallPins.Of(Runtime);
        string path = Path.Combine(_root, "plan-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        return path;
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        return port < 1024 ? 5577 : port;
    }
    private PinnedServerRunOptions<ServerRunPlan> Options(FakeOwnedServer? server = null) => new()
    {
        Name = "pins-smoke", Hooks = Machine.Hooks(),
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
        Scenario = (session, _) => { session.Server!.Game.Execute("cli_extension test.mod/session"); return Task.CompletedTask; },
        SessionOverride = server == null ? null : plan => server.Session(TimeSpan.FromSeconds(60), expectations: plan.ExpectCommand),
    };
    private static JsonElement Result(string output) => JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json"))).RootElement;
    private static JsonElement Step(JsonElement result, string name) => result.GetProperty("Steps").EnumerateArray().Single(s => s.GetProperty("Name").GetString() == name);

    // The runtime manifest was regenerated after a game update or a new BepInExPack, so the fixture copy verifies: the
    // runtime pins still refuse it, naming what changed.
    [Theory]
    [InlineData("game", "the game build differs")]
    [InlineData("core", "the loader differs")]
    [InlineData("patcher", "the patchers differ")]
    public async Task ARemanifestedRuntimeWithAnotherBuildIsRefused(string change, string message)
    {
        WritePlan(); var pinned = InstallPins.Of(Runtime);
        switch (change)
        {
            case "game": File.WriteAllText(Path.Combine(Runtime, "valheim_server_Data", "Managed", "assembly_valheim.dll"), "game build 2"); break;
            case "core": File.WriteAllText(Path.Combine(Runtime, "BepInEx", "core", "BepInEx.dll"), "bepinex 5.4.24"); break;
            case "patcher": Directory.CreateDirectory(Path.Combine(Runtime, "BepInEx", "patchers")); File.WriteAllText(Path.Combine(Runtime, "BepInEx", "patchers", "Hooks.dll"), "hooks"); break;
        }
        string plan = WritePlan(runtimePins: pinned);
        string output = Path.Combine(_root, "out");
        Assert.Equal(1, await RunHere(["validate", plan, output], Options()));
        // Checked on the install the plan names, before its profile is staged.
        var step = Step(Result(output), "check the server host's install against the pinned runtime and stage its profile");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains(message, step.GetProperty("Error").GetString());
        // The control: the same runtime pinned as it now is passes.
        Assert.Equal(0, await RunHere(["validate", WritePlan(), output + "-repinned"], Options()));
    }

    // ---- clients ----
    private static ClientRunPlan Client(string mode) => new()
    {
        Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    [Fact] public void AnOwnedDisposableClientMayObserveInstallPinsButAnInPlaceOneMustSupplyThem()
    {
        var owned = Client("owned");
        owned.Validate("my.mod");
        owned.InPlace = true;
        Assert.Contains("installPins", Assert.Throws<ArgumentException>(() => owned.Validate("my.mod")).Message);
        owned.InstallPins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) };
        owned.Validate("my.mod");
        var attached = Client("attach"); attached.Validate("my.mod");
        attached.InstallPins = owned.InstallPins;
        Assert.Contains("installPins", Assert.Throws<ArgumentException>(() => attached.Validate("my.mod")).Message);
    }

    [Fact] public void AnOwnedClientWithAnotherGameBuildIsRefusedBeforeLaunch()
    {
        FakeInstalls.Client(Install);
        var plan = Client("owned"); plan.Install = Install; plan.InstallPins = InstallPins.Of(Install); plan.InPlace = true; // Launch runs only a bound copy or the install in place.
        File.WriteAllText(Path.Combine(Install, "valheim_Data", "Managed", "assembly_valheim.dll"), "game build 2 (default_old)");
        var output = Directory.CreateDirectory(Path.Combine(_root, "client-out")).FullName;
        Assert.Contains("the game build differs", Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, output)).Message);
        Assert.False(File.Exists(Path.Combine(output, "client-process.json")));
    }

}
