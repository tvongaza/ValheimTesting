using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

// The rules every pinned server plan follows (ServerRunPlan) and the validation idioms mod runners share: each accepted
// and refused, and each refusal naming the field and the fix. Mods keep only their own scenario rules' tests.
public sealed class PlanRuleTests
{
    private static readonly string[] Required = ["my.mod", "valheimCLI.valheimCLI"];
    private const string Token = "MYMOD_TEST_SESSION_TOKEN";
    private static ServerRunPlan Valid() => new()
    {
        Scenario = "empty-save", Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["server.exe"] = new('a', 64) } },
        World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["worlds_local/test.db"] = new('b', 64) } },
        Executable = "valheim_server.exe", Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"],
        Pins = new() { ["worlduid"] = "123", ["my.mod"] = new('1', 32), ["valheimCLI.valheimCLI"] = new('2', 32) },
        RuntimePins = new() { Game = new('c', 64), Loader = new('d', 64), Patchers = new('e', 64) },
    };
    private static void Validate(ServerRunPlan plan) => plan.ValidateServerPlan(Required, Token);
    private static void Refused(ServerRunPlan plan) => Assert.Throws<ArgumentException>(() => Validate(plan));

    // ---- Validation idioms ----

    [Fact] public void AScenarioMustBeOneTheRunnerKnows()
    {
        var plan = Valid(); plan.RequireScenario("empty-save", "bridge-respawn");
        plan.Scenario = "empty_save";
        var error = Assert.Throws<ArgumentException>(() => plan.RequireScenario("empty-save", "bridge-respawn"));
        Assert.Equal("Unknown scenario \"empty_save\": set scenario to empty-save or bridge-respawn.", error.Message);
        plan.Scenario = ""; Assert.Throws<ArgumentException>(() => plan.RequireScenario("empty-save"));
    }

    [Fact] public void AFixtureFlagMustBeExactlyOne()
    {
        var plan = Valid();
        var missing = Assert.Throws<ArgumentException>(() => plan.RequireEnvironmentFlag("MYMOD_TEST_FIXTURE", "let the adapter write the fixture"));
        Assert.Equal("Set environment.MYMOD_TEST_FIXTURE to \"1\" to let the adapter write the fixture; the empty-save scenario needs it, and the plan does not set it.", missing.Message);
        plan.Environment["MYMOD_TEST_FIXTURE"] = "1"; plan.RequireEnvironmentFlag("MYMOD_TEST_FIXTURE", "let the adapter write the fixture");
    }

    [Theory][InlineData("true")][InlineData("0")][InlineData("1 ")][InlineData("")][InlineData("yes")]
    public void AFixtureFlagWithAnyOtherValueIsRefusedAndTheValueNamed(string value)
    {
        var plan = Valid(); plan.Environment["MYMOD_TEST_FIXTURE"] = value;
        var error = Assert.Throws<ArgumentException>(() => plan.RequireEnvironmentFlag("MYMOD_TEST_FIXTURE", "enable it"));
        Assert.Contains($"and it is \"{value}\"", error.Message);
    }

    [Fact] public void AFlagSpelledInAnotherCaseIsRefusedEvenBesideTheRightOne()
    {
        var plan = Valid(); plan.Environment["mymod_test_fixture"] = "1";
        var error = Assert.Throws<ArgumentException>(() => plan.RequireEnvironmentFlag("MYMOD_TEST_FIXTURE", "enable it"));
        Assert.Equal("environment.mymod_test_fixture differs from MYMOD_TEST_FIXTURE only in case: spell it MYMOD_TEST_FIXTURE, once.", error.Message);
        plan.Environment["MYMOD_TEST_FIXTURE"] = "1";
        Assert.Throws<ArgumentException>(() => plan.RequireEnvironmentFlag("MYMOD_TEST_FIXTURE", "enable it"));
    }

    [Fact] public void SettingsForAnotherScenarioAreRefused()
    {
        var plan = Valid();
        plan.OnlyForScenario("append and expected", supplied: false, "bridge-respawn"); // Not supplied: nothing to refuse.
        var error = Assert.Throws<ArgumentException>(() => plan.OnlyForScenario("append and expected", supplied: true, "bridge-respawn"));
        Assert.Equal("append and expected are for the bridge-respawn scenario, not empty-save: remove them from this plan, or set scenario to bridge-respawn.", error.Message);
        plan.Scenario = "bridge-respawn"; plan.OnlyForScenario("append and expected", supplied: true, "bridge-respawn");
        plan.Scenario = "terrain-persistence"; plan.OnlyForScenario("client", supplied: true, "terrain-persistence", "terrain-paint");
        plan.Scenario = "empty-save"; Assert.Contains("terrain-persistence or terrain-paint", Assert.Throws<ArgumentException>(() => plan.OnlyForScenario("client", true, "terrain-persistence", "terrain-paint")).Message);
    }

    // ---- The rules every pinned server plan follows (ValidateServerPlan and the runner's checks) ----

    [Fact] public void AValidPlanUsesStrictPinsAndExpandsItsPlaceholders()
    {
        var plan = Valid(); Validate(plan);
        Assert.StartsWith("cli_expect --strict ", plan.ExpectCommand);
        Assert.Equal("a b/5577", plan.Expand("{world}/{port}", "runtime", "a b"));
        Assert.Equal("runtime/extra", plan.Expand("{runtime}/extra", "runtime", "world"));
    }
    [Theory][InlineData("any")][InlineData("12345678")][InlineData("absent")]
    public void ARequiredPluginNeedsAFullMd5(string pin) { var plan = Valid(); plan.Pins["my.mod"] = pin; Refused(plan); }
    [Fact] public void AMissingRequiredPluginIsRefused() { var plan = Valid(); plan.Pins.Remove("my.mod"); Refused(plan); }
    [Fact] public void EveryOtherListedPluginNeedsAnExactPinToo() { var plan = Valid(); plan.Pins["other.mod"] = "any"; Refused(plan); plan.Pins["other.mod"] = new('3', 32); Validate(plan); }
    [Fact] public void ThePersistentWorldIdentityIsRequired() { var plan = Valid(); plan.Pins.Remove("worlduid"); Refused(plan); }
    [Fact] public void AWorldFileHashPinWouldGoStaleAfterASaveAndIsRefused() { var plan = Valid(); plan.Pins["worldfiles"] = new('f', 32); Refused(plan); }
    [Theory][InlineData("")][InlineData("valheim_server.exe")][InlineData("valheim_server.x86_64")]
    public void TheExecutableIsOptionalOrAKnownServerName(string file) { var plan = Valid(); plan.Executable = file; Validate(plan); }
    [Fact] public void ANullExecutableCountsAsOmitted() { var plan = Valid(); plan.Executable = null!; Validate(plan); plan.CheckExecutable(ServerPlatform.Linux); }
    [Theory][InlineData("server.exe")][InlineData("bin/valheim_server.exe")][InlineData("./valheim_server.x86_64")][InlineData("start_server_bepinex.sh")][InlineData(" ")]
    [InlineData("../valheim_server.exe")][InlineData("/valheim_server.exe")][InlineData("..\\valheim_server.exe")]
    public void AnyOtherExecutableIsRefusedBecauseTheLaunchStartsTheRuntimesRootServer(string file) { var plan = Valid(); plan.Executable = file; Refused(plan); }
    [Theory]
    [InlineData("", ServerPlatform.Windows, true)][InlineData("", ServerPlatform.Linux, true)]
    [InlineData("valheim_server.exe", ServerPlatform.Windows, true)][InlineData("valheim_server.x86_64", ServerPlatform.Linux, true)]
    [InlineData("valheim_server.exe", ServerPlatform.Linux, false)][InlineData("valheim_server.x86_64", ServerPlatform.Windows, false)]
    [InlineData("valheim_server/Valheim", ServerPlatform.MacOS, true)][InlineData("valheim_server/Valheim", ServerPlatform.Linux, false)]
    [InlineData("valheim_server.x86_64", ServerPlatform.MacOS, false)]
    public void AStatedExecutableMustMatchTheDetectedRuntime(string file, ServerPlatform platform, bool matches)
    {
        var plan = Valid(); plan.Executable = file;
        if (matches) plan.CheckExecutable(platform); else Assert.Throws<ArgumentException>(() => plan.CheckExecutable(platform));
    }
    [Theory]
    [InlineData(ServerPlatform.Windows, true, true)][InlineData(ServerPlatform.Linux, false, true)]
    [InlineData(ServerPlatform.Linux, true, false)][InlineData(ServerPlatform.Windows, false, false)]
    public void ARuntimeRunsOnlyOnItsOwnPlatform(ServerPlatform platform, bool windowsHost, bool allowed)
    {
        if (allowed) ServerRunPlan.CheckLaunchHost(platform, windowsHost);
        else Assert.Throws<PlatformNotSupportedException>(() => ServerRunPlan.CheckLaunchHost(platform, windowsHost));
    }
    [Theory]
    [InlineData(ServerPlatform.MacOS, ServerPlatform.MacOS, true)][InlineData(ServerPlatform.MacOS, ServerPlatform.Linux, false)]
    [InlineData(ServerPlatform.Linux, ServerPlatform.MacOS, false)][InlineData(ServerPlatform.Windows, ServerPlatform.MacOS, false)]
    [InlineData(ServerPlatform.Linux, ServerPlatform.Linux, true)]
    public void AMacRuntimeRunsOnlyOnAMac(ServerPlatform platform, ServerPlatform host, bool allowed)
    {
        if (allowed) ServerRunPlan.CheckLaunchHost(platform, host);
        else Assert.Contains("must run on a " + platform + " host", Assert.Throws<PlatformNotSupportedException>(() => ServerRunPlan.CheckLaunchHost(platform, host)).Message);
    }
    [Fact] public void TheMacExecutableIsAPlanExecutable()
    {
        Assert.Equal("valheim_server/Valheim", ServerRunPlan.ExecutableFor(ServerPlatform.MacOS));
        var plan = Valid(); plan.Executable = "valheim_server/Valheim"; plan.ValidateServerPlan([], "TOKEN");
        plan.Executable = "valheim_server/Other"; Refused(plan);
    }
    [Theory][InlineData("DOORSTOP_ENABLED")][InlineData("DOORSTOP_TARGET_ASSEMBLY")][InlineData("doorstop_enabled")][InlineData("DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE")]
    public void DoorstopVariablesBelongToTheLauncher(string name) { var plan = Valid(); plan.Environment[name] = "1"; Refused(plan); }
    [Fact] public void LoaderPathsAndOtherVariablesStayTheCallers()
    { var plan = Valid(); plan.Environment["LD_LIBRARY_PATH"] = "{runtime}/extra"; plan.Environment["SteamAppId"] = "892970"; Validate(plan); }
    [Fact] public void TheCallerCannotChooseTheSessionToken() { var plan = Valid(); plan.Environment[Token] = "reused"; Refused(plan); }
    [Fact] public void AMissingIsolatedSaveRootIsRefused() { var plan = Valid(); plan.Arguments = ["-batchmode", "-nographics"]; Refused(plan); }
    [Fact] public void ASecondSavedirCouldOverrideIsolationAndIsRefused() { var plan = Valid(); plan.Arguments = [.. plan.Arguments, "-savedir", "live-save"]; Refused(plan); }
    [Fact] public void ASavedirOtherThanTheWorldCopyIsRefused() { var plan = Valid(); plan.Arguments = ["-batchmode", "-nographics", "-savedir", "C:/saves"]; Refused(plan); }
    [Fact] public void AGraphicalServerIsRefused() { var plan = Valid(); plan.Arguments = ["-batchmode", "-savedir", "{world}"]; Refused(plan); }
    [Theory][InlineData(80)][InlineData(70000)]
    public void APortOutsideTheUnprivilegedRangeIsRefused(int port) { var plan = Valid(); plan.Port = port; Refused(plan); }
    [Fact] public void RelativeOrUnhashedSourcesAreRefused()
    {
        var plan = Valid(); plan.World.Source = "world"; Refused(plan);
        plan = Valid(); plan.Runtime.Sha256.Clear(); Refused(plan);
        plan = Valid(); plan.World.Sha256["worlds_local/test.db"] = "abc"; Refused(plan);
    }

    [Fact] public void OutputMustBeOutsideBothPinnedSources()
    {
        var plan = Valid();
        string root = Path.Combine(Path.GetTempPath(), "plan-rules-" + Guid.NewGuid().ToString("N"));
        plan.Runtime.Source = Path.Combine(root, "runtime"); plan.World.Source = Path.Combine(root, "world");
        Assert.Throws<ArgumentException>(() => plan.CheckOutput(plan.World.Source));
        Assert.Throws<ArgumentException>(() => plan.CheckOutput(Path.Combine(plan.World.Source, "run")));
        Assert.Throws<ArgumentException>(() => plan.CheckOutput(Path.Combine(plan.Runtime.Source, "nested", "run")));
        plan.CheckOutput(Path.Combine(root, "world-run")); // Shares the source's name as a prefix, but is a sibling.
        plan.CheckOutput(Path.Combine(root, "runs", "new"));
    }
    [Fact] public void LinuxAbsoluteSourcesAreAcceptedOnUnixHosts()
    {
        if (OperatingSystem.IsWindows()) return; // Sources are local to the runner's host; Windows has no rooted '/' form.
        var plan = Valid(); plan.Executable = "valheim_server.x86_64";
        plan.Runtime.Source = "/srv/mymod-test/runtime"; plan.World.Source = "/srv/mymod-test/world"; Validate(plan);
        Assert.Throws<ArgumentException>(() => plan.CheckOutput("/srv/mymod-test/world/run"));
        plan.CheckOutput("/srv/mymod-test/runs/new");
    }

    public sealed class ModPlan : ServerRunPlan { public string Append { get; set; } = ""; }
    [Theory][InlineData("{\"Scenaro\":\"empty-save\"}")][InlineData("{\"scenario\":\"empty-save\",\"apend\":\"road_path 1,2 3,4\"}")]
    public void UnknownJsonFieldsCannotSilentlyDefault(string json)
    {
        string file = Path.GetTempFileName();
        try { File.WriteAllText(file, json); Assert.Throws<JsonException>(() => ServerRunPlan.Read<ModPlan>(file)); }
        finally { File.Delete(file); }
    }
    [Fact] public void KnownFieldsReadInAnyCase()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"SCENARIO\":\"bridge-respawn\",\"append\":\"road_path 1,2 3,4\",\"Port\":5600}");
            var plan = ServerRunPlan.Read<ModPlan>(file);
            Assert.Equal("bridge-respawn", plan.Scenario); Assert.Equal("road_path 1,2 3,4", plan.Append); Assert.Equal(5600, plan.Port);
        }
        finally { File.Delete(file); }
    }
}
