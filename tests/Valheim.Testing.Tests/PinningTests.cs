using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml.Linq;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

/// <summary>Fake install trees: a game assembly in the platform's data folder and a BepInEx core (preloader and BepInEx.dll), no game. Existing files are kept.</summary>
internal static class FakeInstalls
{
    public static void Server(string root) => Write(root, Path.Combine("valheim_server_Data", "Managed"));
    /// <summary>A world fixture directory with one .fwl header (version 41, the name, seed and UID the campaign reviews) and a .db.</summary>
    public static string World(string directory, string name = "Campaign", long uid = 4242L, string seed = "AbCdEf1234")
    {
        Directory.CreateDirectory(directory);
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        { writer.Write(41); writer.Write(name); writer.Write(seed); writer.Write(1234); writer.Write(uid); }
        File.WriteAllBytes(Path.Combine(directory, name + ".fwl"), [.. BitConverter.GetBytes((int)payload.Length), .. payload.ToArray()]);
        File.WriteAllText(Path.Combine(directory, name + ".db"), "fixture");
        return directory;
    }
    public static void Client(string root) => Write(root, Path.Combine("valheim_Data", "Managed"));
    private static void Write(string root, string managed)
    {
        Keep(Path.Combine(root, managed, InstallPins.GameAssemblyName), "game build 1");
        Keep(Path.Combine(root, managed, "assembly_utils.dll"), "utils build 1");
        Keep(Path.Combine(root, managed, "UnityEngine.dll"), "unity");
        Keep(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"), "bepinex 5.4.23");
        Keep(Path.Combine(root, "BepInEx", "core", "BepInEx.Preloader.dll"), "bepinex preloader 5.4.23");
    }
    /// <summary>A Linux install's Doorstop library, which a BepInEx launch needs beside the core.</summary>
    public static void LinuxLoader(string root) => Keep(Path.Combine(root, "doorstop_libs", "libdoorstop_x64.so"), "doorstop 4");
    /// <summary>A macOS install's Doorstop library at its root.</summary>
    public static void MacLoader(string root) => Keep(Path.Combine(root, "libdoorstop.dylib"), "doorstop 4");
    private static void Keep(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, text);
    }
}

// Install pins (game build, loader, patchers) and the explicit, reported opt-out. Tests that read the warning
// capture only their own stderr writes (StderrCapture): other classes run at the same time and warn about their own actors.
public sealed class PinningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pinning-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string Install => Path.Combine(_root, "install");

    private static string Stderr(Action action) => StderrCapture.Of(action);

    // Reproduces #142: another test class's unpinned actor warns while a pinned run's stderr is captured. The foreign
    // write is on its own execution flow, as a test of another class is, and is made deterministically mid-capture.
    [Fact] public void ACaptureHoldsOnlyItsOwnWarningsWhileAnotherTestWarns()
    {
        // This test fails with the previous global-swap Stderr helper, as the separate negative-control run confirmed.
        // Do not install that helper here: even a brief process-global swap can capture another parallel test's output.
        string own = WhileAnotherTestWarns(Stderr);
        Assert.DoesNotContain("not pinned", own);
        // What the action writes, from threads and tasks it starts too, is still its own.
        Assert.Equal(new[] { "own line", "own thread", "own task" }, own.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string WhileAnotherTestWarns(Func<Action, string> capture)
    {
        using var capturing = new ManualResetEventSlim(); using var written = new ManualResetEventSlim();
        Thread other;
        using (ExecutionContext.SuppressFlow())
        {
            other = new Thread(() =>
            {
                capturing.Wait();
                new GameActor("other test", new ScriptedTransport()).VerifyEnvironment(EnvironmentPinning.None);
                written.Set();
            });
            other.Start();
        }
        try
        {
            return capture(() =>
            {
                capturing.Set(); Assert.True(written.Wait(TimeSpan.FromSeconds(30)));
                Console.Error.WriteLine("own line");
                var started = new Thread(() => Console.Error.WriteLine("own thread")); started.Start(); started.Join();
                Task.Run(() => Console.Error.WriteLine("own task")).GetAwaiter().GetResult();
            });
        }
        finally { capturing.Set(); other.Join(); }
    }

    // ---- install pins ----

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

    // The loader identity covers every file a launch requires (#364's one list), so no required file can change unpinned.
    [Fact] public void TheLoaderPinCoversEveryFileALaunchRequires()
    {
        foreach (string file in BepInExLoader.LoaderFiles(ClientPlatform.Windows).Concat(BepInExLoader.LoaderFiles(ClientPlatform.Linux)).Concat(GameLaunch.MacDoorstopFiles))
            Assert.True(InstallPins.IsLoaderFile(file), file);
        Assert.Equal(["winhttp.dll", "doorstop_config.ini", "libdoorstop.dylib", "BepInEx/core", "doorstop_libs"], InstallPins.LoaderEntries);
    }

    // A plan written before #295 names bepinexCore; it is refused with what to do, wherever the pins sit.
    [Theory]
    [InlineData("runtimePins")]
    [InlineData("client")]
    public void APlanThatStillNamesBepInExCoreIsRefusedNamingTheLoaderPin(string where)
    {
        string pins = $$"""{ "game": "{{new string('a', 64)}}", "bepinexCore": "{{new string('b', 64)}}", "patchers": "{{new string('c', 64)}}" }""";
        string path = Path.Combine(_root, "old-plan.json");
        File.WriteAllText(path, where == "client" ? $$"""{ "client": { "mode": "owned", "installPins": {{pins}} } }""" : $$"""{ "runtimePins": {{pins}} }""");
        var error = Assert.ThrowsAny<Exception>(() => ServerRunPlan.Read<CrossplayPlanTests.ClientPlan>(path));
        string message = error.Message + " " + error.InnerException?.Message;
        Assert.Contains("The pins' bepinexCore was removed (ValheimTesting #295)", message);
        Assert.Contains("Replace bepinexCore with loader, computed by InstallPins.Of(<install>)", message);
        // The control: the same pins naming loader are read.
        File.WriteAllText(path, where == "client" ? $$"""{ "client": { "mode": "owned", "installPins": {{pins.Replace("bepinexCore", "loader")}} } }"""
            : $$"""{ "runtimePins": {{pins.Replace("bepinexCore", "loader")}} }""");
        var read = ServerRunPlan.Read<CrossplayPlanTests.ClientPlan>(path);
        Assert.Equal(new string('b', 64), (where == "client" ? read.Client!.InstallPins : read.RuntimePins)!.Loader);
        Assert.DoesNotContain("bepinexCore", System.Text.Json.JsonSerializer.Serialize(read.RuntimePins ?? read.Client!.InstallPins), StringComparison.OrdinalIgnoreCase);
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

    private static ServerRunPlan Plan(string? pinning = null)
    {
        var plan = new ServerRunPlan
        {
            Runtime = new() { Source = Path.GetTempPath() }, World = new() { Source = Path.GetTempPath() },
            Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"],
        };
        if (pinning != null) plan.Pinning = pinning;
        return plan;
    }
    private static void Pin(ServerRunPlan plan)
    {
        plan.Runtime.Sha256["a"] = new string('a', 64); plan.World.Sha256["b"] = new string('b', 64);
        plan.Pins["worlduid"] = "1"; plan.Pins["my.mod"] = new string('1', 32);
        plan.RuntimePins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) };
    }

    [Fact] public void AStrictPlanNeedsRuntimePins()
    {
        var plan = Plan(); Pin(plan);
        plan.ValidateServerPlan(["my.mod"], "TOKEN");
        Assert.StartsWith("cli_expect", plan.ExpectCommand);
        plan.RuntimePins = null;
        Assert.Contains("runtimePins", Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan(["my.mod"], "TOKEN")).Message);
        Assert.Throws<ArgumentException>(() => plan.CheckRuntimePins(_root));
    }

    [Fact] public void AnUnpinnedPlanNeedsNoPinsAndMayListNone()
    {
        var plan = Plan("none");
        plan.ValidateServerPlan(["my.mod"], "TOKEN"); // No worlduid, no MD5s, no manifests, no runtime pins.
        Assert.False(plan.Pinned); Assert.Equal(EnvironmentPinning.None, plan.ExpectCommand);
        // Pins beside the opt-out would look enforced while nothing checks them.
        plan.Pins["worlduid"] = "1"; Assert.Contains("lists no pins", Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")).Message); plan.Pins.Clear();
        plan.RuntimePins = new() { Game = new string('c', 64), Loader = new string('d', 64), Patchers = new string('e', 64) };
        Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); plan.RuntimePins = null;
        // The other plan rules still apply.
        plan.Arguments = ["-batchmode"]; Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN"));
    }

    // Negative control: the opt-out is never a default, a near spelling or an environment setting.
    [Theory]
    [InlineData("None")] [InlineData("")] [InlineData("off")] [InlineData("strict ")]
    public void OnlyTheExactOptOutUnpins(string value)
    {
        var plan = Plan(value);
        Assert.Contains("\"strict\"", Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")).Message);
        Assert.Throws<ArgumentException>(() => new ClientRunPlan { Pinning = value }.Pinned);
    }

    [Fact] public void AnOmittedPinningIsStrictWhateverTheEnvironmentSays()
    {
        string path = Path.Combine(_root, "plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            runtime = new { source = _root, sha256 = new Dictionary<string, string>() }, world = new { source = _root, sha256 = new Dictionary<string, string>() },
            arguments = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            environment = new Dictionary<string, string> { ["VALHEIM_TESTING_PINNING"] = "none", ["PINNING"] = "none" },
        }));
        var plan = ServerRunPlan.Read<ServerRunPlan>(path);
        Assert.Equal(EnvironmentPinning.Strict, plan.Pinning); Assert.True(plan.Pinned);
        Assert.Throws<ArgumentException>(() => plan.ValidateServerPlan([], "TOKEN")); // Missing manifests and pins.
        File.WriteAllText(path, """{"pinning": null}""");
        Assert.Throws<ArgumentException>(() => ServerRunPlan.Read<ServerRunPlan>(path).Pinned);
    }

    // ---- the runner ----

    private string Runtime => Path.Combine(_root, "runtime");
    private string World => Path.Combine(_root, "world");
    private string WritePlan(object? pinning = null, InstallPins? runtimePins = null, bool manifests = true)
    {
        Directory.CreateDirectory(Runtime); Directory.CreateDirectory(Path.Combine(World, "worlds_local"));
        string server = Path.Combine(Runtime, OperatingSystem.IsWindows() ? GameLaunch.ServerWindowsExecutable : GameLaunch.ServerLinuxExecutable);
        File.WriteAllText(server, "server");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.UserExecute);
        FakeInstalls.Server(Runtime);
        File.WriteAllText(Path.Combine(World, "worlds_local", "Test.db"), "world");
        var plan = new Dictionary<string, object>
        {
            ["runtime"] = new { source = Runtime, sha256 = manifests ? WorldFixture.Manifest(Runtime) : new Dictionary<string, string>() },
            ["world"] = new { source = World, sha256 = manifests ? WorldFixture.Manifest(World) : new Dictionary<string, string>() },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}" },
            ["port"] = FreePort(),
        };
        if (pinning != null) plan["pinning"] = pinning;
        else { plan["pins"] = new Dictionary<string, string> { ["worlduid"] = "1" }; plan["runtimePins"] = runtimePins ?? InstallPins.Of(Runtime); }
        string path = Path.Combine(_root, "plan-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        return path;
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        return port < 1024 ? 5577 : port;
    }
    private static PinnedServerRunOptions<ServerRunPlan> Options(FakeOwnedServer? server = null) => new()
    {
        Name = "pinning-smoke",
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        Mod = new("test.mod/session", "TEST_SESSION_TOKEN"),
        Scenario = (session, _) => { session.Server!.Game.Execute("cli_extension test.mod/session"); return Task.CompletedTask; },
        // The plan's own expectations: strict pins, or EnvironmentPinning.None for an unpinned plan.
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
        Assert.Equal(1, await PinnedServerRun.MainAsync(["validate", plan, output], Options()));
        var step = Step(Result(output), "copied runtime is the pinned game build, loader and patchers");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains(message, step.GetProperty("Error").GetString());
        // The control: the same runtime pinned as it now is passes.
        Assert.Equal(0, await PinnedServerRun.MainAsync(["validate", WritePlan(), output + "-repinned"], Options()));
    }

    [Fact] public async Task AnUnpinnedRunRunsWarnsAndMarksEveryReportAndEvidenceFile()
    {
        string plan = WritePlan(pinning: "none", manifests: false);
        string output = Path.Combine(_root, "out");
        // This fake Windows or Linux runtime cannot run on macOS; validate still reads, copies and reports the unpinned plan there.
        string mode = OperatingSystem.IsMacOS() ? "validate" : "run";
        var server = new FakeOwnedServer("test.mod");
        int code = 0;
        string stderr = Stderr(() => code = PinnedServerRun.MainAsync([mode, plan, output], Options(server)).GetAwaiter().GetResult());
        Assert.Equal(0, code);
        Assert.Contains("WARNING: environment not pinned: pinning-smoke", stderr);
        var result = Result(output);
        Assert.True(result.GetProperty("Passed").GetBoolean());
        Assert.Equal("none", result.GetProperty("Pinning").GetString());
        Assert.StartsWith("environment not pinned", result.GetProperty("Provenance").GetProperty("environment").GetString());
        Assert.Equal(InstallPins.Of(Runtime).Loader, result.GetProperty("Provenance").GetProperty("runtimeLoaderSha256").GetString());
        var names = result.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Name").GetString()).ToList();
        Assert.Contains("copy unpinned runtime as found", names); Assert.Contains("copy unpinned world as found", names);
        Assert.Contains("record the unpinned runtime's game build, loader and patchers", names);
        if (mode == "run") Assert.DoesNotContain(server.Events, e => e.StartsWith("pins", StringComparison.Ordinal)); // No cli_expect was sent.
        // The marker sits in the Preflight suite: the pinning decision is made before anything is copied.
        var junit = XDocument.Load(Path.Combine(output, "junit.xml")).Root!.Elements("testsuite").Single(s => s.Attribute("name")!.Value.EndsWith(" / preflight", StringComparison.Ordinal));
        Assert.Equal("1", junit.Attribute("skipped")!.Value);
        Assert.Contains(junit.Elements("properties").Elements("property"), p => p.Attribute("name")!.Value == "environment" && p.Attribute("value")!.Value.StartsWith("environment not pinned"));
        Assert.Contains(junit.Elements("testcase"), c => c.Attribute("name")!.Value == "environment not pinned" && c.Element("skipped") != null);
        // Every evidence file the runner wrote carries the marker.
        var evidence = Directory.GetFiles(output).Where(f => Path.GetExtension(f) is ".json" or ".jsonl" or ".xml").ToList();
        Assert.Contains(Path.Combine(output, "input-hashes.json"), evidence);
        Assert.All(evidence, file => Assert.Contains("environment not pinned", File.ReadAllText(file)));
    }

    [Fact] public void APinnedRunCarriesNoMarker()
    {
        string output = Path.Combine(_root, "out"), plan = WritePlan();
        int code = 0;
        string stderr = Stderr(() => code = PinnedServerRun.MainAsync(["validate", plan, output], Options()).GetAwaiter().GetResult());
        Assert.Equal(0, code);
        Assert.DoesNotContain("not pinned", stderr);
        Assert.Equal("strict", Result(output).GetProperty("Pinning").GetString());
        Assert.All(Directory.GetFiles(output), file => Assert.DoesNotContain("environment not pinned", File.ReadAllText(file)));
    }

    // ---- actors and clients ----

    [Fact] public void AnUnpinnedActorRunsWithoutPinChecksWarnsOnceAndCanBePinnedAgain()
    {
        var transport = new ScriptedTransport().On("look", _ => ScriptedTransport.Ok("OK"));
        var actor = new GameActor("observer", transport);
        Assert.Throws<InvalidOperationException>(() => actor.Execute("look")); // Still refused until verified or explicitly unpinned.
        string stderr = Stderr(() =>
        {
            actor.VerifyEnvironment(EnvironmentPinning.None); actor.Execute("look");
            actor.InvalidateEnvironment(); actor.VerifyEnvironment(EnvironmentPinning.None); actor.Execute("look");
        });
        Assert.False(actor.Pinned);
        Assert.Equal(new[] { "look", "look" }, transport.Commands);
        Assert.Single(stderr.Split('\n'), line => line.Contains("WARNING: environment not pinned: game actor \"observer\""));
        // A transition still needs an explicit decision.
        actor.InvalidateEnvironment(); Assert.Throws<InvalidOperationException>(() => actor.Execute("look"));
        actor.VerifyEnvironment("cli_expect worlduid=1"); actor.Execute("look");
        Assert.True(actor.Pinned);
        Assert.Equal(new[] { "look", "look", "cli_expect --strict worlduid=1", "cli_expect --strict worlduid=1", "look" }, transport.Commands.Select(c => c.StartsWith("cli_expect") ? "cli_expect --strict worlduid=1" : c));
        // Nothing but the exact value opts out.
        Assert.Throws<ArgumentException>(() => actor.VerifyEnvironment("None"));
        Assert.Throws<ArgumentException>(() => actor.VerifyEnvironment(""));
    }

    private static ClientRunPlan Client(string mode) => new()
    {
        Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    [Fact] public void AnOwnedStrictClientNeedsInstallPinsAndAnAttachedOneMayNotHaveThem()
    {
        var owned = Client("owned");
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

    [Fact] public void AnUnpinnedClientIsWarnedAndItsRecordsAreMarked()
    {
        var plan = Client("attach"); plan.Pinning = EnvironmentPinning.None;
        Assert.Contains("lists no pins", Assert.Throws<ArgumentException>(() => plan.Validate("my.mod")).Message);
        plan.Pins.Clear(); plan.Validate("my.mod");
        Assert.Equal(EnvironmentPinning.None, plan.MenuExpectations); Assert.Equal(EnvironmentPinning.None, plan.WorldExpectations("7"));
        var output = Directory.CreateDirectory(Path.Combine(_root, "client-out")).FullName;
        var transport = new ScriptedTransport();
        string stderr = Stderr(() => { using var session = ClientSession.Attach(plan, output, transport); Assert.False(session.Actor.Pinned); });
        Assert.Contains("WARNING: environment not pinned", stderr);
        Assert.DoesNotContain(transport.Commands, c => c.StartsWith("cli_expect", StringComparison.Ordinal));
        using var first = JsonDocument.Parse(File.ReadLines(Path.Combine(output, "client-commands.jsonl")).First());
        Assert.Equal("environment not pinned", first.RootElement.GetProperty("environment").GetString());
        // An owned unpinned client skips the install pins too, but keeps the patcher-names check.
        var owned = Client("owned"); owned.Pinning = EnvironmentPinning.None; owned.Pins.Clear(); owned.Validate("my.mod");
    }
}
