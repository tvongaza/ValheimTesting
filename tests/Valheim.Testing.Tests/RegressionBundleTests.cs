using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

// Public bundles of targeted native regressions (#126): each way a hand-made public copy diverged from the run or disclosed
// the environment (an unrelated absent pin, a machine path, a package version the run did not use, a wrong file count, a
// result that does not match its evidence) is refused before anything could be shared. A clean run makes a small bundle.
public sealed class RegressionBundleTests : IDisposable
{
    private readonly BundleRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public void ACleanRunMakesASmallBundleThatVerifies()
    {
        string output = _rig.Output();
        var manifest = RegressionBundle.Create(_rig.Spec(), output, _rig.Sources);
        var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(output, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "BUNDLE-MANIFEST.json", "ExampleRegression.csproj", "Program.cs", "README.md", "Scenario.cs", "regression.template.json" }, files);
        Assert.Equal(File.ReadAllText(Path.Combine(_rig.Root, "runner", "Scenario.cs")), File.ReadAllText(Path.Combine(output, "Scenario.cs"))); // Exactly as it ran.
        Assert.Contains("<PackageReference Include=\"Valheim.Testing.Game\" Version=\"[0.1.0-preview.17]\" />", File.ReadAllText(Path.Combine(output, "ExampleRegression.csproj")));
        string readme = File.ReadAllText(Path.Combine(output, "README.md"));
        Assert.Contains("This bundle holds 6 files", readme);
        Assert.Contains($"| parent | `p1` | `{PluginPins.Md5(_rig.Rig.Parent)}` | `{WorldFixture.Hash(_rig.Rig.Parent)}` | fail | **fail** | 1/2 | first: exactly one marker stands there: 2 markers stand there; expected 1. |", readme);
        Assert.Contains("| pass | **pass** | 2/2 |  |", readme);
        Assert.Contains("only the mod under test differs", readme);
        Assert.Contains("Nothing in this directory has been published", readme);
        Assert.DoesNotContain("has not itself been run", readme); // Not a port.
        string template = File.ReadAllText(Path.Combine(output, "regression.template.json"));
        Assert.Contains("\"game\": \"<full path of a prepared Valheim install", template);
        Assert.Contains(WorldFixture.Hash(_rig.Rig.Candidate), template); // Hashes are kept; paths are not.
        foreach (string file in files) Assert.DoesNotContain(_rig.Root, File.ReadAllText(Path.Combine(output, file)));
        Assert.Contains("game", manifest.EnvironmentOnly);
        Assert.Contains("client.character", manifest.EnvironmentOnly);
        Assert.Null(manifest.Native);
        Assert.Equal(new[] { "parent", "parent", "parent", "parent", "candidate", "candidate", "candidate", "candidate" }, manifest.Evidence.Select(entry => entry.Arm));
        Assert.Equal(new[] { false, true }, manifest.Results.Select(result => result.Passed));
        Assert.Equal(new[] { "run-manifest.json", "run-manifest.json" }, manifest.Results.Select(result => result.Sha256Source));
        Assert.Contains(manifest.Checks, check => check.StartsWith("the arms' command traces pin the same world", StringComparison.Ordinal));
        RegressionBundle.Verify(output, _rig.Spec());
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".incomplete-*"));
    }

    [Fact] public void AnExistingIncompleteDirectoryIsNeverDeleted()
    {
        string output = _rig.Output();
        string existing = output + ".incomplete";
        Directory.CreateDirectory(existing);
        string valued = Path.Combine(existing, "keep.txt");
        File.WriteAllText(valued, "not created by this run");

        RegressionBundle.Create(_rig.Spec(), output, _rig.Sources);
        Assert.Equal("not created by this run", File.ReadAllText(valued));
        Assert.True(Directory.Exists(output));
    }

    // ---- names and paths of the environment ----

    [Fact] public void AnUnrelatedAbsentPinIsRefusedWithItsFileAndLine()
    {
        _rig.Scenario("        [\"ProceduralRoads\"] = \"absent\", // a station-only plugin");
        string output = _rig.Output();
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), output, _rig.Sources));
        Assert.Contains("Scenario.cs:5: names plugin ProceduralRoads, which is not in the plugin allowlist", error.Message);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".incomplete-*"));
    }

    [Fact] public void APluginTheNativeRunPinnedOutsideTheAllowlistCannotBeNamedAnywhere()
    {
        _rig.Evidence("parent", pass: false, extraPins: " warpalicious.More_World_Locations_AIO=absent");
        _rig.Evidence("candidate", pass: true, extraPins: " warpalicious.More_World_Locations_AIO=absent");
        _rig.Scenario("        // Not tested with warpalicious.More_World_Locations_AIO.");
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources));
        Assert.Contains("Scenario.cs:5: names warpalicious.More_World_Locations_AIO, which is outside the plugin allowlist", error.Message);
        _rig.Scenario("");
        var manifest = RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources); // Left out, it is only counted.
        Assert.Contains(manifest.Omitted, omission => omission.What.StartsWith("1 plugin pin(s) of the native run outside the allowlist", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"        // Built in C:\Users\someone\mods\EpicLoot.", "a Windows machine path")]
    [InlineData("        // Copied from /Users/someone/Documents/run.", "a machine path")]
    [InlineData("        // Steam account 76561198000000001.", "a Steam account ID")]
    [InlineData("        // Run as smoketest.", "the disposable character's name from the private environment")]
    [InlineData("        // Hosted SealFixture.", "a name or ID of the fixture world from the private environment")]
    [InlineData("        // UID -1320459616.", "a name or ID of the fixture world from the private environment")]
    public void AMachinePathOrPrivateIdentifierIsRefused(string line, string kind)
    {
        _rig.Scenario(line);
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources));
        Assert.Contains("Scenario.cs:5: " + kind, error.Message);
    }

    // ---- the toolkit pin ----

    [Fact] public void AnObsoleteOrUnavailableToolkitIsRefused()
    {
        var spec = _rig.Spec();
        spec.Toolkit.Package = "0.1.0-preview.16"; // Published, but older than the run's.
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(spec, _rig.Output(), _rig.Sources));
        Assert.Contains("0.1.0-preview.16 is older than the 0.1.0-preview.17 the native run used", error.Message);
        spec.Toolkit.Package = "0.1.0-preview.99";
        Assert.Contains("Valheim.Testing.Game 0.1.0-preview.99 is not published on NuGet.org", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(spec, _rig.Output(), _rig.Sources)).Message);
        spec.Toolkit.Package = null;
        spec.Toolkit.Commit = new string('d', 40);
        Assert.Contains($"has no commit {new string('d', 40)}", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(spec, _rig.Output(), _rig.Sources)).Message);
        spec.Toolkit.Commit = BundleRig.Commit;
        string output = _rig.Output();
        RegressionBundle.Create(spec, output, _rig.Sources);
        string project = File.ReadAllText(Path.Combine(output, "ExampleRegression.csproj"));
        Assert.Contains($"<ValheimTestingCommit>{BundleRig.Commit}</ValheimTestingCommit>", project);
        Assert.Contains("rev-parse HEAD", project);
        Assert.Contains($"git -C ValheimTesting checkout {BundleRig.Commit}", File.ReadAllText(Path.Combine(output, "README.md")));
    }

    [Fact] public void PackageVersionsCompareAsNuGetOrdersThem()
    {
        Assert.True(RegressionBundle.CompareVersions("0.1.0-preview.9", "0.1.0-preview.10") < 0);
        Assert.True(RegressionBundle.CompareVersions("0.1.0", "0.1.0-preview.17") > 0);
        Assert.True(RegressionBundle.CompareVersions("0.2.0-preview.1", "0.1.0") > 0);
        Assert.Equal(0, RegressionBundle.CompareVersions("0.1.0-preview.17+abc", "0.1.0-preview.17"));
    }

    // ---- the bundle's own files ----

    [Fact] public void AWrongFileCountIsRefused()
    {
        string output = _rig.Output();
        RegressionBundle.Create(_rig.Spec(), output, _rig.Sources);
        File.WriteAllText(Path.Combine(output, "notes.txt"), "a fifth file, as a README once said four");
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Verify(output, _rig.Spec()));
        Assert.Contains("notes.txt: not in BUNDLE-MANIFEST.json", error.Message);
        Assert.Contains("README.md: says the bundle holds 6 files, and it holds 7", error.Message);
        Assert.Contains("notes.txt: not source or text this bundle carries (.txt)", error.Message);
        File.Delete(Path.Combine(output, "notes.txt"));
        File.Delete(Path.Combine(output, "Program.cs"));
        error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Verify(output, _rig.Spec()));
        Assert.Contains("Program.cs: listed in BUNDLE-MANIFEST.json but missing", error.Message);
        Assert.Contains("says the bundle holds 6 files, and it holds 5", error.Message);
    }

    [Fact] public void AnEditedBundleFileOrChangedEvidenceIsRefused()
    {
        string output = _rig.Output();
        RegressionBundle.Create(_rig.Spec(), output, _rig.Sources);
        string readme = Path.Combine(output, "README.md");
        string original = File.ReadAllText(readme);
        File.WriteAllText(readme, original.Replace("**fail**", "**pass**"));
        Assert.Contains("README.md: changed after the bundle was generated", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Verify(output, _rig.Spec())).Message);
        File.WriteAllText(readme, original);
        // Another parent run, failing elsewhere than the bundle says: the bundle no longer describes the evidence.
        _rig.Evidence("parent", pass: false, error: "3 markers stand there; expected 1.");
        Assert.Contains("BUNDLE-MANIFEST.json: its results are not what the arms' evidence gives now", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Verify(output, _rig.Spec())).Message);
    }

    // ---- the result and its evidence ----

    [Fact] public void ATamperedResultIsRefused()
    {
        string result = Path.Combine(_rig.Root, "evidence", "parent", "result.json");
        string original = File.ReadAllText(result);
        // A failed step flipped to passed, and the summary with it: junit.xml still says it failed.
        File.WriteAllText(result, original.Replace("\"Passed\": false", "\"Passed\": true"));
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources));
        Assert.Contains("result.json and junit.xml disagree", error.Message);
        // Only the summary flipped.
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(original)!;
        edited["Passed"] = JsonSerializer.SerializeToElement(true);
        File.WriteAllText(result, JsonSerializer.Serialize(edited));
        Assert.Contains("result.json says Passed=True, but its steps say a step failed", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources)).Message);
        // An unpinned run is no evidence of which build ran.
        File.WriteAllText(result, original.Replace("\"Pinning\": \"strict\"", "\"Pinning\": \"none\""));
        Assert.Contains("result.json records pinning none", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources)).Message);
        File.WriteAllText(result, original);
        // A trace of another build.
        _rig.Evidence("parent", pass: false, md5: PluginPins.Md5(_rig.Rig.Candidate));
        Assert.Contains($"the command trace pinned example.mod={PluginPins.Md5(_rig.Rig.Candidate)}, not the arm's {PluginPins.Md5(_rig.Rig.Parent)}",
            Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources)).Message);
        _rig.Evidence("parent", pass: false);
        // A declared result the evidence does not hold.
        var spec = _rig.Spec();
        spec.Arms["candidate"].Expect = "fail";
        Assert.Contains("declared fail, but every step in result.json passed", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(spec, _rig.Output(), _rig.Sources)).Message);
        spec = _rig.Spec();
        spec.Arms["parent"].FailingStep = "first: the mod marks the ground";
        Assert.Contains("declared to fail at \"first: the mod marks the ground\", but result.json first fails at \"first: exactly one marker stands there\"",
            Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(spec, _rig.Output(), _rig.Sources)).Message);
        // A declared hash the evidence does not hold.
        spec = _rig.Spec();
        spec.Arms["parent"].Md5 = PluginPins.Md5(_rig.Rig.Candidate);
        Assert.Contains($"the declared build is not the one that ran: md5 is {PluginPins.Md5(_rig.Rig.Parent)} in run-manifest.json, {PluginPins.Md5(_rig.Rig.Candidate)} in the spec",
            Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(spec, _rig.Output(), _rig.Sources)).Message);
        File.Delete(Path.Combine(_rig.Root, "evidence", "parent", "client-commands.jsonl"));
        Assert.Contains("client-commands.jsonl is missing", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources)).Message);
    }

    [Fact] public void OnlyTheModMayDifferBetweenTheArms()
    {
        string trace = Path.Combine(_rig.Root, "evidence", "candidate", "client-commands.jsonl");
        string dependency = PluginPins.Md5(Path.Combine(_rig.Root, "deps", "Dependency.dll"));
        File.WriteAllText(trace, File.ReadAllText(trace).Replace("example.dependency=" + dependency, "example.dependency=" + new string('0', 32)));
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources));
        Assert.Contains($"More than the mod under test differs between the arms' strict pins: example.dependency is {dependency} in parent and {new string('0', 32)} in candidate", error.Message);
        _rig.Evidence("candidate", pass: true);
        File.WriteAllText(trace, File.ReadAllText(trace).Replace("worlduid=" + _rig.Rig.Uid, "worlduid=42"));
        string result = Path.Combine(_rig.Root, "evidence", "candidate", "result.json");
        File.WriteAllText(result, File.ReadAllText(result).Replace($"\"worldUid\": \"{_rig.Rig.Uid}\"", "\"worldUid\": \"42\""));
        Assert.Contains("The arms' command traces pinned the worlds -1320459616, 42", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.Spec(), _rig.Output(), _rig.Sources)).Message);
    }

    // ---- a run made before environment manifests, ported to the template ----

    [Fact] public void ARetainedRunIsBundledFromATemplateAndAPortHoldingTheNativeAssertions()
    {
        foreach (string arm in new[] { "parent", "candidate" }) _rig.Retain(arm);
        string native = _rig.NativeRunner("        public static void Measure(ClientRound round) => round.Step(\"the mod marks the ground\", () => { });");
        string output = _rig.Output();
        var manifest = RegressionBundle.Create(_rig.RetainedSpec(native), output, _rig.Sources);
        Assert.Equal(File.ReadAllText(_rig.TemplatePath), File.ReadAllText(Path.Combine(output, "regression.template.json")));
        string readme = File.ReadAllText(Path.Combine(output, "README.md"));
        Assert.Contains("The native run used a hand-written runner with ValheimTesting source 0123456.", readme);
        Assert.Contains("**which has not itself been run in the game**", readme);
        Assert.Contains("- Port and character moved to the environment manifest.", readme);
        Assert.Contains($"| parent | `p1` | `{PluginPins.Md5(_rig.Rig.Parent)}` | `{WorldFixture.Hash(_rig.Rig.Parent)}` (declared) |", readme);
        Assert.Contains($"| candidate | `c1` | `{PluginPins.Md5(_rig.Rig.Candidate)}` | not recorded |", readme);
        Assert.Equal(WorldFixture.Hash(native), manifest.Native!.RunnerSha256);
        Assert.Contains(manifest.Files, file => file.Path == "Scenario.cs" && file.Origin.StartsWith("the port", StringComparison.Ordinal));
        Assert.Contains(manifest.Files, file => file.Path == "Program.cs" && file.Origin.StartsWith("the harness", StringComparison.Ordinal));
        Assert.Contains(manifest.Checks, check => check.StartsWith("native: lines 4-4 of the native runner", StringComparison.Ordinal));
        Assert.Contains("fixture.worldUid", manifest.EnvironmentOnly);
        RegressionBundle.Verify(output, _rig.RetainedSpec(native));

        // The native assertions changed: the port no longer holds what ran.
        File.WriteAllText(native, File.ReadAllText(native).Replace("marks the ground", "marks the wet ground"));
        var error = Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.RetainedSpec(native), _rig.Output(), _rig.Sources));
        Assert.Contains("Scenario.cs does not hold lines 4-4 of the native runner verbatim", error.Message);
        Assert.Contains("The public assertions must be the ones that ran", error.Message);
        // A template with a machine-specific value.
        _rig.NativeRunner("        public static void Measure(ClientRound round) => round.Step(\"the mod marks the ground\", () => { });");
        File.WriteAllText(_rig.TemplatePath, File.ReadAllText(_rig.TemplatePath).Replace("\"<that world's UID>\"", "\"-1320459616\""));
        Assert.Contains("fixture.worldUid is \"-1320459616\", not a <...> placeholder", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(_rig.RetainedSpec(native), _rig.Output(), _rig.Sources)).Message);
        // Without a run manifest the arm's commit must be declared.
        _rig.WriteTemplate();
        var spec = _rig.RetainedSpec(native);
        spec.Arms["candidate"].Commit = null;
        Assert.Contains("neither run-manifest.json nor the environment records the build's source commit", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Create(spec, _rig.Output(), _rig.Sources)).Message);
    }

    [Fact] public void TheSampleBundleSpecNamesOnlyKnownFields()
    {
        string sample = File.ReadAllText(Path.Combine(FixtureProjects.RepositoryRoot(), "examples", "RegressionBundle", "bundle.sample.json"));
        var read = JsonSerializer.Deserialize<BundleSpec>(sample, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        })!;
        Assert.Equal(new[] { "parent", "candidate" }, read.Arms.Keys);
        Assert.Equal("fail", read.Arms["parent"].Expect);
    }

    // ---- the clean build ----

    [NetworkFact] public void ABundlePinnedToAPublishedPackageBuildsWithOnlyNuGetOrg()
    {
        _rig.Evidence("parent", pass: false, toolkit: "Valheim.Testing.Game 0.1.0-preview.16");
        _rig.Evidence("candidate", pass: true, toolkit: "Valheim.Testing.Game 0.1.0-preview.16");
        var spec = _rig.Spec();
        spec.Toolkit.Package = "0.1.0-preview.16";
        string output = _rig.Output();
        RegressionBundle.Create(spec, output, new PublicBundleSources());
        RegressionBundle.Build(output, TimeSpan.FromMinutes(8));
        // The same project pinned to a version NuGet.org does not have does not build.
        string project = Path.Combine(output, "ExampleRegression.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("0.1.0-preview.16", "0.1.0-preview.99"));
        Assert.Contains("does not build in a clean directory", Assert.Throws<InvalidOperationException>(() => RegressionBundle.Build(output, TimeSpan.FromMinutes(8))).Message);
    }
}

/// <summary>A test that needs NuGet.org: reported as skipped when it does not answer, or with <c>VALHEIM_TESTING_OFFLINE=1</c>.</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
    {
        if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_OFFLINE") == "1") return "VALHEIM_TESTING_OFFLINE=1";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var reply = http.GetAsync("https://api.nuget.org/v3/index.json").GetAwaiter().GetResult();
            return reply.IsSuccessStatusCode ? null : $"NuGet.org answered {(int)reply.StatusCode}";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return "NuGet.org does not answer: " + error.Message; }
    });

    public NetworkFactAttribute()
    {
        if (Reason.Value is { } reason) Skip = "Builds against NuGet.org; skipped: " + reason;
    }
}

/// <summary>A regression rig with a runner, two arms' evidence (parent fails, candidate passes) and a spec for its bundle.</summary>
internal sealed class BundleRig : IDisposable
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public RegressionRig Rig { get; } = new();
    public string Root => Rig.Root;
    public string EnvironmentPath => Path.Combine(Root, "regression.json");
    public string TemplatePath => Path.Combine(Root, "retained", "regression.template.json");
    public FakeSources Sources { get; } = new();
    private int _outputs;

    public BundleRig()
    {
        Rig.Manifest().Write(EnvironmentPath);
        Directory.CreateDirectory(Path.Combine(Root, "runner"));
        File.WriteAllText(Path.Combine(Root, "runner", "Program.cs"), "using Valheim.Testing.Game;\n\n// The runner's entry point.\nConsole.WriteLine(new ScenarioReport(Scenario.Name).Passed ? \"PASS\" : \"FAIL\");\n");
        Scenario("");
        Evidence("parent", pass: false);
        Evidence("candidate", pass: true);
        WriteTemplate();
    }

    /// <summary>Writes the scenario source with <paramref name="line"/> as its line 5.</summary>
    public void Scenario(string line) => File.WriteAllText(Path.Combine(Root, "runner", "Scenario.cs"),
        "using Valheim.Testing.Game;\n\npublic static class Scenario\n{\n" + (line.Length == 0 ? "" : line + "\n") +
        "    public const string Name = \"example-regression\";\n    public static void Measure(ClientRound round) => round.Step(\"the mod marks the ground\", () => { });\n}\n");

    /// <summary>One arm's evidence as a template run writes it, its trace pinning <paramref name="md5"/> for the mod (the arm's own by default).</summary>
    public void Evidence(string arm, bool pass, string? md5 = null, string extraPins = "", string toolkit = "Valheim.Testing.Game 0.1.0-preview.17", string error = "2 markers stand there; expected 1.")
    {
        var staged = new TargetedRegression(Rig.Manifest()).Stage(arm);
        string directory = Path.Combine(Root, "evidence", arm);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var report = new ScenarioReport("example-regression");
        report.Provenance["toolkit"] = toolkit;
        staged.Record(report.Provenance);
        report.Provenance["hostWorld"] = "SealFixture";
        report.Step("first: the mod marks the ground", () => { });
        try { report.Step("first: exactly one marker stands there", () => { if (!pass) throw new InvalidOperationException(error); }); }
        catch (InvalidOperationException) { }
        report.Write(directory);
        File.WriteAllText(Path.Combine(directory, "run-manifest.json"), JsonSerializer.Serialize(staged.Manifest, TargetedRegression.ManifestJson));
        var pins = staged.Plan.Pins.ToDictionary(pin => pin.Key, pin => pin.Key == "example.mod" && md5 != null ? md5 : pin.Value);
        string expect = "cli_expect --strict " + string.Join(" ", pins.Select(pin => $"{pin.Key}={pin.Value}")) + extraPins;
        File.WriteAllLines(Path.Combine(directory, "client-commands.jsonl"),
        [
            JsonSerializer.Serialize(new { utc = "2026-10-01T00:00:00Z", command = expect, reply = new { Ok = true, Output = new[] { "OK: EXPECT" } } }),
            JsonSerializer.Serialize(new { utc = "2026-10-01T00:00:01Z", command = expect + " worlduid=" + Rig.Uid, reply = new { Ok = true, Output = new[] { "OK: EXPECT" } } }),
            JsonSerializer.Serialize(new { utc = "2026-10-01T00:00:02Z", command = "mymod_mark 1 2", reply = new { Ok = true, Output = new[] { "OK: marked" } } }),
        ]);
    }

    /// <summary>Makes an arm's evidence look like a hand-written runner's: no run manifest, and no build or toolkit in the provenance.</summary>
    public void Retain(string arm)
    {
        string directory = Path.Combine(Root, "evidence", arm);
        File.Delete(Path.Combine(directory, "run-manifest.json"));
        var result = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "result.json")))!;
        var provenance = result["Provenance"]!.AsObject();
        foreach (string key in new[] { "modSha256", "modCommit", "modPlugin", "toolkit", "allowlist" }) provenance.Remove(key);
        File.WriteAllText(Path.Combine(directory, "result.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public BundleSpec Spec() => new()
    {
        Title = "Example mod: one marker where the host stands", Issue = "https://github.com/example/mod/issues/1",
        Summary = "The candidate places one marker; the parent placed two.", Environment = EnvironmentPath, Runner = "ExampleRegression",
        Sources = [Path.Combine(Root, "runner", "Program.cs"), Path.Combine(Root, "runner", "Scenario.cs")],
        Toolkit = new() { Package = "0.1.0-preview.17" },
        Plugins = ["example.mod", "example.dependency", "testing.probe"],
        Arms = new()
        {
            ["parent"] = new() { Evidence = Path.Combine(Root, "evidence", "parent"), Expect = "fail", FailingStep = "first: exactly one marker stands there" },
            ["candidate"] = new() { Evidence = Path.Combine(Root, "evidence", "candidate"), Expect = "pass" },
        },
        Limitations = ["It checks one marker on one fixture."],
    };

    /// <summary>A hand-written native runner whose line 4 is <paramref name="line4"/>; it names its machine and an unrelated pin.</summary>
    public string NativeRunner(string line4)
    {
        string path = Path.Combine(Root, "native", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// the hand-written runner\nvar plan = new ClientRunPlan { Port = 5560, Character = \"smoketest\" };\nplan.Pins[\"ProceduralRoads\"] = \"absent\";\n" + line4 + "\n");
        return path;
    }

    /// <summary>The bundle of a run made before environment manifests: a written template, declared builds and a port of <paramref name="native"/>.</summary>
    public BundleSpec RetainedSpec(string native)
    {
        var spec = Spec();
        spec.Environment = null;
        spec.Template = TemplatePath;
        spec.ModPlugin = "example.mod";
        spec.Toolkit = new() { Commit = Commit };
        spec.Deny = ["smoketest"];
        spec.Arms["parent"].Commit = "p1";
        spec.Arms["parent"].Md5 = PluginPins.Md5(Rig.Parent);
        spec.Arms["parent"].Sha256 = WorldFixture.Hash(Rig.Parent);
        spec.Arms["candidate"].Commit = "c1";
        spec.Arms["candidate"].Md5 = PluginPins.Md5(Rig.Candidate);
        spec.Native = new()
        {
            Runner = native, RanWith = "ValheimTesting source 0123456", Excerpts = [new() { From = 4, To = 4, In = "Scenario.cs" }],
            Differences = ["Port and character moved to the environment manifest.", "An absent pin of an unrelated plugin removed."],
        };
        return spec;
    }

    public void WriteTemplate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TemplatePath)!);
        File.WriteAllText(TemplatePath, """
            {
              "name": "example-regression",
              "game": "<full path of a prepared Valheim install with BepInEx; only read>",
              "install": "<full path of a new directory for the disposable install>",
              "client": { "port": 5560, "character": "<a disposable local character's file name>" },
              "fixture": { "root": "<directory holding exactly one world folder>", "worldUid": "<that world's UID>" },
              "cli": { "core": { "file": "<ValheimCLI build>/valheimCLI.dll", "sha256": "" }, "packs": [{ "file": "<ValheimCLI build>/Valheim.Cli.Standard.dll", "sha256": "" }] },
              "plugins": [{ "file": "<dependency>/Dependency.dll", "sha256": "" }],
              "probe": { "file": "<probe build>/Probe.dll", "sha256": "" },
              "mod": {
                "installAs": "ExampleMod.dll",
                "arms": {
                  "parent": { "file": "<parent build>/ExampleMod.dll", "sha256": "<sha256 of your parent build>", "commit": "p1" },
                  "candidate": { "file": "<candidate build>/ExampleMod.dll", "sha256": "<sha256 of your candidate build>", "commit": "c1" }
                }
              }
            }
            """);
    }

    public string Output() => Path.Combine(Root, "bundle-" + ++_outputs);

    public void Dispose() => Rig.Dispose();

    /// <summary>A feed with preview.16 and preview.17 published and one known commit.</summary>
    internal sealed class FakeSources : IBundleSources
    {
        public IReadOnlyList<string>? PackageVersions(string id) => id == RegressionBundle.Package ? ["0.1.0-preview.16", "0.1.0-preview.17"] : null;
        public bool HasCommit(string repository, string commit) => commit == Commit;
    }
}
