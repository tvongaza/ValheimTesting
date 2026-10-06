using System.Text;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

// The owned-run preflight (#117): each failure a native setup attempt hit before its gameplay assertion, refused before the
// game starts or the fixture is copied where the fact is on disk, and named promptly where only the live game shows it.
// No game, Steam or network connection.
public sealed class OwnedRunPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("preflight-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- plugin builds and ScriptEngine (a hand-typed file or hash; ValheimCLI in scripts without LoadOnStart) ----

    [Fact] public void APassingInstallPassesThePreflight()
    {
        using var install = PreflightInstall.Create();
        var plan = install.Plan();
        plan.Validate("my.mod");
        plan.Preflight();
    }

    [Fact] public void APinnedBuildThatIsNotInstalledIsRefusedNamingTheFileThatIs()
    {
        using var install = PreflightInstall.Create();
        install.Add("BepInEx/plugins/Jotunn.dll", "the fix build");
        var plan = install.Plan();
        plan.Pins["com.jotunn.jotunn"] = new string('0', 32); // Typed by hand, or another build's hash.
        var error = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains("com.jotunn.jotunn=" + new string('0', 32) + " is in neither BepInEx/plugins nor BepInEx/scripts", error.Message);
        Assert.Contains("BepInEx/plugins/Jotunn.dll is " + Md5("the fix build"), error.Message);
        Assert.Contains("InstallPins.Plugins", error.Message);
        plan.Pins["com.jotunn.jotunn"] = Md5("the fix build"); // Negative control: the staged file's own hash passes.
        plan.Preflight();
    }

    [Fact] public void ABuildInstalledTwiceIsRefused()
    {
        using var install = PreflightInstall.Create();
        install.Add("BepInEx/scripts/valheimCLI.dll", PreflightInstall.CliBuild);
        var error = Assert.Throws<InvalidOperationException>(install.Plan().Preflight);
        Assert.Contains("installed 2 times", error.Message);
        Assert.Contains("BepInEx/scripts/valheimCLI.dll", error.Message);
    }

    [Fact] public void AMistypedCandidateFileIsRefusedWithWhatTheFolderHolds()
    {
        using var install = PreflightInstall.Create();
        install.Add("BepInEx/plugins/Jotunn.dll", "the fix build");
        var error = Assert.Throws<FileNotFoundException>(() => InstallPins.Plugins(install.Root, new Dictionary<string, string> { ["com.jotunn.jotunn"] = "BepInEx/plugins/Jotunn.candidate.dll" }));
        Assert.Contains("Jotunn.candidate.dll is not in the install", error.Message);
        Assert.Contains("Jotunn.dll", error.Message);
        var pins = InstallPins.Plugins(install.Root, new Dictionary<string, string> { ["com.jotunn.jotunn"] = "BepInEx/plugins/Jotunn.dll", ["ProceduralRoads"] = "absent" });
        Assert.Equal(Md5("the fix build"), pins["com.jotunn.jotunn"]);
        Assert.Equal("absent", pins["ProceduralRoads"]);
    }

    [Fact] public void APluginPinCannotReadAFileOutsideTheInstall()
    {
        using var install = PreflightInstall.Create();
        var outside = Path.Combine(Path.GetDirectoryName(install.Root)!, "outside.dll");
        File.WriteAllText(outside, "outside");
        try
        {
            var error = Assert.Throws<ArgumentException>(() => InstallPins.Plugins(install.Root,
                new Dictionary<string, string> { ["my.mod"] = Path.Combine("..", "outside.dll") }));
            Assert.Contains("leaves the install", error.Message);
        }
        finally { File.Delete(outside); }
    }

    [Fact] public void ACliInScriptsNeedsScriptEngineLoadingAtStart()
    {
        using var install = PreflightInstall.Create(cliIn: "scripts");
        install.Add("BepInEx/plugins/ScriptEngine.dll", "script engine");
        var plan = install.Plan();
        // ScriptEngine not pinned as loaded: nothing loads the scripts folder.
        Assert.Contains("does not pin ScriptEngine", Assert.Throws<InvalidOperationException>(plan.Preflight).Message);
        plan.Pins[OwnedClientPreflight.ScriptEngine] = Md5("script engine");
        // No config yet: ScriptEngine's own default is LoadOnStart = false.
        Assert.Contains("LoadOnStart defaults to false", Assert.Throws<InvalidOperationException>(plan.Preflight).Message);
        // The older config the native attempt found.
        install.Add("BepInEx/config/com.bepis.bepinex.scriptengine.cfg", "[General]\n\n## Load all plugins from the scripts folder when starting the application\n# Setting type: Boolean\n# Default value: false\nLoadOnStart = false\n\nReloadKey = F6\n");
        var error = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains("valheimCLI.valheimCLI (BepInEx/scripts/valheimCLI.dll)", error.Message);
        Assert.Contains("[General] LoadOnStart = false", error.Message);
        Assert.Contains("install the current ValheimCLI core and its packs in BepInEx/plugins", error.Message);
        install.Add("BepInEx/config/com.bepis.bepinex.scriptengine.cfg", "[General]\nLoadOnStart = true\n");
        plan.Preflight();
    }

    [Fact] public void ScriptEngineCannotLoadItselfFromScripts()
    {
        using var install = PreflightInstall.Create(cliIn: "scripts");
        install.Add("BepInEx/scripts/ScriptEngine.dll", "script engine");
        install.Add("BepInEx/config/com.bepis.bepinex.scriptengine.cfg", "[General]\nLoadOnStart = true\n");
        var plan = install.Plan();
        plan.Pins[OwnedClientPreflight.ScriptEngine] = Md5("script engine");
        var error = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains("must itself be installed in BepInEx/plugins", error.Message);
    }

    // ---- the standing expectations file (eight pins on one line; a strict file without a world) ----

    [Fact] public void StandingPinsAreWrittenOnePerLine()
    {
        var pins = new[] { Pin("valheimCLI.valheimCLI", new string('a', 32)), Pin("ProceduralRoads", "absent"), Pin("world", "any") };
        Assert.Equal($"valheimCLI.valheimCLI={new string('a', 32)}\nProceduralRoads=absent\nworld=any\n", StandingPins.Format(pins));
        string path = Path.Combine(_root, "expect.txt");
        StandingPins.Write(path, pins);
        Assert.Equal(pins.Select(p => p.Key), StandingPins.Read(path).Select(p => p.Key));
        Assert.Throws<ArgumentException>(() => StandingPins.Format([Pin("my.mod", "notahash")]));
        Assert.Throws<ArgumentException>(() => StandingPins.Format([Pin("my.mod", "absent"), Pin("MY.MOD", "any")]));
        Assert.Throws<ArgumentException>(() => StandingPins.Format([Pin("my.mod", "absent\nother=absent")]));
        Assert.Throws<ArgumentException>(() => StandingPins.Format([]));
    }

    [Fact] public void EightPinsOnOneLineAreNamedAsSuch()
    {
        // As a PowerShell array expression joined them: one space-separated line.
        string line = string.Join(" ", Enumerable.Range(0, 6).Select(i => $"plugin.{i}={new string((char)('a' + i), 32)}").Append("ProceduralRoads=absent").Append("Other=absent"));
        var error = Assert.Throws<ArgumentException>(() => StandingPins.Parse(["# pins", line], "expect.txt"));
        Assert.Contains("expect.txt: line 2 holds 8 key=value pins separated by spaces", error.Message);
        Assert.Contains("one pin per line", error.Message);
        // A line that is wrong in another way keeps ValheimCLI's own message.
        Assert.Contains("is not key=value", Assert.Throws<ArgumentException>(() => StandingPins.Parse(["just words"], "f")).Message);
        Assert.Contains("no pin", Assert.Throws<ArgumentException>(() => StandingPins.Parse(["# nothing"], "f")).Message);
        Assert.Equal(8, StandingPins.Parse(line.Split(' '), "f").Count); // The same pins, one per line, parse.
    }

    [Fact] public void AMalformedStandingFileRefusesTheLaunchBeforeAnythingStarts()
    {
        using var install = PreflightInstall.Create();
        var plan = install.Plan();
        install.Standing("expect.txt", $"valheimCLI.valheimCLI={install.CliMd5} my.mod=absent\n");
        var error = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains("standing expectations file", error.Message);
        Assert.Contains("line 1 holds 2 key=value pins", error.Message);
        // The launch runs the same checks first: nothing is started and no evidence is written.
        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        Assert.Contains("line 1 holds 2", Assert.Throws<InvalidOperationException>(() => ClientSession.Launch(plan, output)).Message);
        Assert.False(File.Exists(Path.Combine(output, "client-process.json")));
        install.Standing("expect.txt", StandingPins.Format([Pin("valheimCLI.valheimCLI", install.CliMd5), Pin("my.mod", "absent")]));
        plan.Preflight();
    }

    [Fact] public void AStandingFileThatWouldRefuseTheRunIsRefused()
    {
        using var install = PreflightInstall.Create();
        var plan = install.Plan();
        install.Standing("missing.txt", null);
        Assert.Contains("does not exist", Assert.Throws<InvalidOperationException>(plan.Preflight).Message);
        // Strict without a world: every command is refused once the world loads.
        install.Standing("expect.txt", $"valheimCLI.valheimCLI={install.CliMd5}\nmy.mod=absent\n", strict: true);
        Assert.Contains("names no world", Assert.Throws<InvalidOperationException>(plan.Preflight).Message);
        install.Standing("expect.txt", $"valheimCLI.valheimCLI={install.CliMd5}\nmy.mod=absent\nworld=any\n", strict: true);
        plan.Preflight();
        // Contradicting the plan's own pins: no game can meet both.
        install.Standing("expect.txt", $"valheimCLI.valheimCLI={new string('b', 32)}\nmy.mod=any\nworld=any\n", strict: true);
        var error = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains($"valheimCLI.valheimCLI={new string('b', 32)}, but the plan pins valheimCLI.valheimCLI={install.CliMd5}", error.Message);
        Assert.Contains("my.mod=any, but the plan pins my.mod=absent", error.Message);
        // A hash prefix of the plan's build agrees with it.
        install.Standing("expect.txt", $"valheimCLI.valheimCLI={install.CliMd5[..8]}\nworld=any\n");
        plan.Preflight();
        // An inherited positive pin for an unrelated plugin would reject every command after launch.
        install.Standing("expect.txt", $"valheimCLI.valheimCLI={install.CliMd5}\ncom.bepis.bepinex.scriptengine=any\nworld=any\n");
        Assert.Contains("plugin the plan does not pin", Assert.Throws<InvalidOperationException>(plan.Preflight).Message);
        install.Standing("expect.txt", $"valheimCLI.valheimCLI={install.CliMd5}\nworld=any\n");
        plan.Preflight();
    }

    // ---- the Doorstop proxy and its configuration ----

    [Fact] public void AProxysDoorstopVersionIsReadFromTheKeyItReads()
    {
        Assert.Equal(4, BepInExLoader.ProxyDoorstopMajor(Proxy(4)));
        Assert.Equal(3, BepInExLoader.ProxyDoorstopMajor(Proxy(3)));
        Assert.Equal(3, BepInExLoader.ProxyDoorstopMajor(Encoding.ASCII.GetBytes("MZ..targetAssembly..")));
        Assert.Null(BepInExLoader.ProxyDoorstopMajor(Encoding.ASCII.GetBytes("fake")));
        Assert.Equal("4.4.0", BepInExLoader.ProxyFileVersion(Proxy(4)));
        Assert.Null(BepInExLoader.ProxyFileVersion(Encoding.ASCII.GetBytes("fake")));
    }

    private const string Doorstop4 = "[General]\nenabled = true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n[UnityMono]\ndebug_enabled = false\n";
    private const string Doorstop3 = "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\nredirectOutputLog=false\n";

    [Theory]
    [InlineData(4, Doorstop3, "written for Doorstop 3 ([UnityDoorstop])")]
    [InlineData(4, Doorstop4 + Doorstop3, "written for Doorstop 3 ([UnityDoorstop])")] // A [General] section added to the Doorstop 3 file.
    [InlineData(3, Doorstop4, "written for Doorstop 4 ([General])")]
    public void AProxyAndConfigurationFromDifferentDoorstopVersionsAreRefused(int proxy, string config, string expected)
    {
        string install = Loader(Proxy(proxy), config);
        var error = Assert.Throws<DoorstopPairingException>(() => BepInExLoader.RequireWindowsLoader(install, "client install"));
        Assert.Contains($"winhttp.dll is Doorstop {proxy}", error.Message);
        Assert.Contains(expected, error.Message);
        Assert.Contains("from one BepInExPack", error.Message);
        if (proxy == 4) Assert.Contains("(file version 4.4.0)", error.Message);
    }

    [Theory]
    [InlineData(4, Doorstop4)] [InlineData(3, Doorstop3)] [InlineData(3, Doorstop3 + Doorstop4)]
    public void AProxyWithItsOwnVersionsConfigurationIsAccepted(int proxy, string config) =>
        BepInExLoader.RequireWindowsLoader(Loader(Proxy(proxy), config), "client install");

    // Every real Doorstop 3 and 4 proxy holds exactly one of the two keys; one holding neither or both is refused, never
    // passed with only its configuration checked (#256: an unrecognised proxy is a failure).
    [Theory]
    [InlineData("neither", Doorstop4)] [InlineData("neither", Doorstop3)] [InlineData("both", Doorstop4)]
    public void AProxyThatShowsNoDoorstopVersionIsRefused(string holds, string config)
    {
        byte[] proxy = Encoding.ASCII.GetBytes(holds == "both" ? "MZ target_assembly targetAssembly" : "MZ fake");
        var error = Assert.Throws<DoorstopPairingException>(() => BepInExLoader.RequireWindowsLoader(Loader(proxy, config), "client install"));
        Assert.Contains("winhttp.dll is not a Doorstop proxy this check recognises", error.Message);
    }

    // ---- BepInEx's fresh startup log ----

    [Fact] public async Task AGameWithoutAFreshBepInExLineWithinItsDeadlineFailsNamingTheLoader()
    {
        string log = Path.Combine(_root, "LogOutput.log");
        File.WriteAllText(log, "[Message:   BepInEx] BepInEx 5.4.23 - valheim (an earlier run)\n");
        using var wait = new LogWait(log) { SafetyInterval = TimeSpan.FromMilliseconds(50) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<WaitFailedException>(() => StartupEvents.WaitForBepInExLog(wait, TimeSpan.FromMilliseconds(300), "Player.log", default));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10)); // Its own short deadline, not the client's start deadline.
        Assert.Contains("BepInEx wrote nothing", error.Message);
        Assert.Contains("Doorstop did not start BepInEx", error.Message);
        Assert.Contains("winhttp.dll and doorstop_config.ini", error.Message);
    }

    [Fact] public async Task ThisLaunchsFirstBepInExLinePassesAndAFailureLineEndsTheWait()
    {
        string log = Path.Combine(_root, "LogOutput.log");
        using (var wait = new LogWait(log) { SafetyInterval = TimeSpan.FromMilliseconds(50) })
        {
            File.WriteAllText(log, "[Message:   BepInEx] BepInEx 5.4.23 - valheim\n");
            await StartupEvents.WaitForBepInExLog(wait, TimeSpan.FromSeconds(10), null, default);
        }
        File.Delete(log);
        using (var wait = new LogWait(log) { SafetyInterval = TimeSpan.FromMilliseconds(50) })
        {
            File.WriteAllText(log, "[Error  : BepInEx] Could not load [My Mod 1.0.0] because it has missing dependencies\n");
            var error = await Assert.ThrowsAsync<WaitFailedException>(() => StartupEvents.WaitForBepInExLog(wait, TimeSpan.FromSeconds(10), null, default));
            Assert.Contains("Could not load", error.Message);
        }
    }

    [Fact] public void TheBepInExDeadlineIsBounded()
    {
        using var install = PreflightInstall.Create();
        var plan = install.Plan();
        Assert.Equal(60, plan.BepInExSeconds);
        plan.BepInExSeconds = 4;
        Assert.Throws<ArgumentException>(() => plan.Validate());
        plan.BepInExSeconds = 30;
        plan.Validate();
    }

    // ---- ValheimCLI capabilities and packs, checked once the client answers ----

    [Fact] public void AMonolithicValheimCliWithoutExtensionsIsNamedAsOne()
    {
        var transport = new ScriptedTransport().On("cli_extensions", _ => new CommandResult
        {
            Ok = false, ErrorCode = "unknown_command", Message = "'cli_extensions' is not a recognized command. Type 'help' to see a list of valid commands.",
        });
        using var actor = transport.Actor("client");
        var error = Assert.Throws<InvalidOperationException>(() => CliCapabilities.Require(actor, CliCapabilities.HostedRounds));
        Assert.Contains("has no cli_extensions", error.Message);
        Assert.Contains("predates command packs", error.Message);
        Assert.Contains("Standard and World Tools packs", error.Message);
        // Every capability lookup says the same, not only the preflight.
        Assert.Contains("predates command packs", Assert.Throws<InvalidOperationException>(() => actor.RequireCapability("valheim.session/state")).Message);
    }

    [Fact] public void AMissingPackIsNamedWithEveryMissingCommand()
    {
        var transport = new ScriptedTransport().Extension("valheim.session", "state", _ => new { });
        using var actor = transport.Actor("client");
        var error = Assert.Throws<InvalidOperationException>(() => CliCapabilities.Require(actor, CliCapabilities.HostedRounds.Append("valheim.world/terrain")));
        Assert.Contains("lacks valheim.session/save, valheim.session/leave, valheim.world/terrain", error.Message);
        Assert.Contains("valheim.session comes from the Standard pack (Valheim.Cli.Standard.dll", error.Message);
        Assert.Contains("valheim.world comes from the World Tools pack (Valheim.Cli.WorldTools.dll", error.Message);
        Assert.Contains("the World Tools pack", Assert.Throws<InvalidOperationException>(() => actor.RequireCapability("valheim.world/terrain")).Message);
        transport.Extension("valheim.session", "save", _ => new { }, readOnly: false).Extension("valheim.session", "leave", _ => new { }, readOnly: false);
        CliCapabilities.Require(actor, CliCapabilities.HostedRounds);
    }

    // ---- the fixture's own world identity ----

    // Hand-derived: length 26, then version 41, "Abc", "s", seed 7, UID -2 and the world-gen version that follows.
    private static readonly byte[] SmallMetadata = Convert.FromHexString("1A000000" + "29000000" + "03416263" + "0173" + "07000000" + "FEFFFFFFFFFFFFFF" + "02000000");

    [Fact] public void AWorldsIdentityIsReadFromItsMetadata()
    {
        var identity = WorldIdentity.Parse(SmallMetadata, "Abc.fwl");
        Assert.Equal(new WorldIdentity("Abc", "s", 7, -2, 41, "Abc.fwl"), identity);
        Assert.Equal("-2", identity.UidText);
        foreach (var broken in new[] { SmallMetadata[..10], Encoding.ASCII.GetBytes("fixture metadata"), Array.Empty<byte>() })
            Assert.Contains("Cannot read the world's identity from Abc.fwl", Assert.Throws<InvalidDataException>(() => WorldIdentity.Parse(broken, "Abc.fwl")).Message);
    }

    [Fact] public void AChunkedSavesIdentityIsItsNewestMetadataAndAllMustAgree()
    {
        string fixture = Path.Combine(_root, "chunked");
        Directory.CreateDirectory(Path.Combine(fixture, "Fixture"));
        File.WriteAllBytes(Path.Combine(fixture, "Fixture", "_main.2.fwl2"), Metadata("Fixture", 77));
        File.WriteAllBytes(Path.Combine(fixture, "Fixture", "_main.10.fwl2"), Metadata("Fixture", 77, seed: 5));
        File.WriteAllText(Path.Combine(fixture, "Fixture", "_main.10.db2"), "world");
        var identity = WorldIdentity.Read(fixture);
        Assert.Equal(77, identity.Uid);
        Assert.Equal(5, identity.Seed);
        Assert.Equal(Path.Combine("Fixture", "_main.10.fwl2"), identity.File);
        File.WriteAllBytes(Path.Combine(fixture, "Fixture", "_main.2.fwl2"), Metadata("Fixture", 78));
        Assert.Contains("saves of different worlds", Assert.Throws<InvalidDataException>(() => WorldIdentity.Read(fixture)).Message);
    }

    [Fact] public void AFixtureHoldingAnotherUidThanThePlanIsTheWrongFixture()
    {
        string fixture = Path.Combine(_root, "fixture");
        Directory.CreateDirectory(fixture);
        File.WriteAllBytes(Path.Combine(fixture, "SealFixture.fwl"), Metadata("SealFixture", -123456789));
        File.WriteAllText(Path.Combine(fixture, "SealFixture.db"), "world");
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "450017353" };
        plan.Validate(pinned: true);
        var error = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains("hostWorld.worldUid is 450017353", error.Message);
        Assert.Contains("holds world SealFixture with UID -123456789 (SealFixture.fwl)", error.Message);
        Assert.Contains("the same name, another world", error.Message);
        plan.WorldUid = "-123456789";
        Assert.Equal("SealFixture", plan.Preflight().Name);
        // A pinned file that changed is named before anything is copied.
        File.WriteAllText(Path.Combine(fixture, "SealFixture.db"), "another snapshot");
        var changed = Assert.Throws<InvalidOperationException>(plan.Preflight);
        Assert.Contains("Fixture hash mismatch", changed.Message);
        Assert.Contains("SealFixture.db is ", changed.Message);
    }

    // The world loads between two session reads: the strict menu pins refuse it, and the exact world pins decide at once.
    [Fact] public void AWorldThatLoadsBetweenTwoReadsIsPinnedByItsUid()
    {
        foreach (var (loaded, passes) in new[] { ("4242", true), ("999", false) })
        {
            bool hosting = false;
            var transport = HostingTransport(() => hosting, () => hosting = true, loaded);
            var plan = new ClientRunPlan
            {
                Mode = "attach", Port = 5556, Character = "Tester", Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) },
                HostWorld = new() { World = new() { Source = Path.GetFullPath("fixture") }, WorldUid = "4242" },
            };
            using var actor = transport.Actor("host", plan.MenuExpectations);
            if (passes) Assert.Equal("4242", HostWorlds.Start(actor, plan, "HostFixture", TimeSpan.FromSeconds(10)).WorldUid);
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => HostWorlds.Start(actor, plan, "HostFixture", TimeSpan.FromSeconds(10)));
                Assert.Contains("not world UID 4242", error.Message);
                Assert.Contains("a wrong fixture, not a load failure", error.Message);
                Assert.Equal(0, transport.Count("cli_set_player_safety"));
            }
        }
    }

    // A game whose world is present from the first read after the start, as ValheimCLI's strict cli_expect judges pins.
    private static ScriptedTransport HostingTransport(Func<bool> hosting, Action start, string loaded)
    {
        return new ScriptedTransport()
            .OnPrefix("cli_expect", command =>
            {
                if (!hosting()) return ScriptedTransport.Ok("OK: EXPECT");
                if (command.Contains("worlduid=" + loaded, StringComparison.Ordinal)) return ScriptedTransport.Ok("OK: EXPECT");
                string problem = command.Contains("worlduid=", StringComparison.Ordinal)
                    ? $"MISMATCH worlduid: {loaded}, expected {command[(command.IndexOf("worlduid=", StringComparison.Ordinal) + 9)..]}"
                    : $"MISMATCH world: HostFixture (uid {loaded}) is loaded but not listed (strict); add world=, worlduid= or world=any";
                return new CommandResult { Ok = false, ErrorCode = "command_failed", Message = problem, Output = [problem, "ERROR: code=expectation_mismatch mismatches=1"] };
            })
            .ClientAccess(hosting, hosting)
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester, Local)"))
            .OnPrefix("cli_start_host_world ", command =>
            {
                start();
                return ScriptedTransport.Ok($"OK: Starting hosted world '{command.Split(' ')[1]}' using character 'Tester' (tester, Local); open=true, public=False, crossplay=False, backend=Steamworks, passwordSet=False");
            })
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = hosting() ? "world-present" : "menu", worldUid = hosting() ? loaded : null,
                worldPresent = hosting(), worldReady = hosting(), server = hosting(), dedicated = false, localPlayer = hosting(), playerReady = hosting(),
                saving = false, loadError = false, connectionStatus = hosting() ? "Connected" : "None",
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"));
    }

    // ---- helpers ----

    private static KeyValuePair<string, string> Pin(string key, string value) => new(key, value);
    internal static string Md5(string content) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>World metadata as the game's package writes it (BinaryWriter): version, name, seed name, seed, UID, then the world-gen version.</summary>
    internal static byte[] Metadata(string name, long uid, int seed = 1234)
    {
        using var package = new MemoryStream();
        using (var writer = new BinaryWriter(package, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(41); writer.Write(name); writer.Write("AbCdEf1234"); writer.Write(seed); writer.Write(uid); writer.Write(2);
        }
        var bytes = new byte[4 + package.Length];
        BitConverter.TryWriteBytes(bytes, (int)package.Length);
        package.ToArray().CopyTo(bytes, 4);
        return bytes;
    }

    // A winhttp.dll stand-in: the key its version's code reads, as UTF-16 text, and a version resource (VS_FIXEDFILEINFO).
    private static byte[] Proxy(int major)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("MZ fake proxy "));
        bytes.AddRange(Encoding.Unicode.GetBytes(major == 4 ? "General\0target_assembly\0" : "UnityDoorstop\0targetAssembly\0"));
        bytes.AddRange(new byte[] { 0xBD, 0x04, 0xEF, 0xFE, 0x00, 0x00, 0x01, 0x00 });
        bytes.AddRange(BitConverter.GetBytes((uint)(major << 16 | (major == 4 ? 4 : 0))));
        bytes.AddRange(BitConverter.GetBytes(0u));
        return [.. bytes];
    }

    private string Loader(byte[] proxy, string config)
    {
        string install = Directory.CreateDirectory(Path.Combine(_root, "loader-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllBytes(Path.Combine(install, "winhttp.dll"), proxy);
        File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), config);
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "fake");
        return install;
    }
}

/// <summary>
/// A fake owned client install for this machine's platform that passes <see cref="ClientRunPlan.Preflight"/>: the launch's
/// files, a game assembly for its install pins, and ValheimCLI's core in <c>BepInEx/plugins</c> (or <c>scripts</c>).
/// </summary>
internal sealed class PreflightInstall : IDisposable
{
    public const string CliBuild = "valheimCLI core build";
    private readonly ClientLaunchTests.Install _install;
    public string Root => _install.Root;
    public string CliMd5 { get; } = OwnedRunPreflightTests.Md5(CliBuild);
    private PreflightInstall(ClientLaunchTests.Install install) => _install = install;

    public static PreflightInstall Create(string cliIn = "plugins")
    {
        var platform = GameLaunch.CurrentClientHost;
        var install = new PreflightInstall(ClientLaunchTests.Install.For(platform));
        install.Add(platform == ClientPlatform.MacOS ? "Valheim.app/Contents/Resources/Data/Managed/assembly_valheim.dll" : "valheim_Data/Managed/assembly_valheim.dll", "game");
        install.Add($"BepInEx/{cliIn}/valheimCLI.dll", CliBuild);
        return install;
    }
    public void Add(string relative, string content) => _install.Add(relative, content);
    public void Add(string relative, byte[] content) => _install.Add(relative, content);

    /// <summary>Points ValheimCLI's <c>[Expectations] File</c> at <paramref name="file"/> in BepInEx/config, written with <paramref name="text"/> unless null.</summary>
    public void Standing(string file, string? text, bool strict = false)
    {
        Add("BepInEx/config/valheimCLI.valheimCLI.cfg", $"[Expectations]\n\n# Setting type: String\nFile = {file}\n\nStrict = {(strict ? "true" : "false")}\n\n[Server]\nPort = 5556\n");
        if (text != null) Add("BepInEx/config/" + file, text);
    }

    public ClientRunPlan Plan() => new()
    {
        Mode = "owned", Install = Root, Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = CliMd5, ["my.mod"] = "absent" },
        InstallPins = InstallPins.Of(Root),
    };
    public void Dispose() => _install.Dispose();
}
