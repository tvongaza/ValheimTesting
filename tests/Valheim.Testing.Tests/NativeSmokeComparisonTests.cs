using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using System.Text.Json;
using Xunit;

public sealed class NativeSmokeComparisonTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact] public async Task ComparisonRefusesOutputInsideAModProjectBeforeBuildingItsAdapter()
    {
        string project = Path.Combine(_rig.Root, "mod-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "MyMod.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(project, "runs", "ab1");
        int arms = 0, builds = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion),
            runArm: _ => { arms++; return Task.FromResult(0); },
            buildAdapter: (_, _, _, _, _) => { builds++; throw new InvalidOperationException("must not build"); });
        Assert.Equal(3, result);
        Assert.Equal(0, arms);
        Assert.Equal(0, builds);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task OutputInsidePreparedInstallIsRefusedBeforeCreatingEvidenceOrRunningAnArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Game, "comparison-evidence");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task OutputInsideSteamUserdataIsRefusedBeforeCreatingEvidenceOrRunningAnArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string steam = Path.Combine(_rig.Root, "steam-userdata");
        string output = Path.Combine(steam, "comparison-evidence");
        string[] args = [.. Arguments(output, _rig.Parent, companion), "--client", Path.Combine(_rig.Root, "client"), "--steam-userdata", steam];
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(args, _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task IncompatiblePairStopsBeforeCreatingAnOwnedRun()
    {
        string clash = _rig.Write("clash/Clash.dll", RegressionRig.Assembly("Clash",
            new("example.clash") { Incompatible = ["example.mod"] }));
        string output = Path.Combine(_rig.Root, "incompatible-comparison");
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, clash));
        Assert.Equal(3, result);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task MissingHardDependencyStopsBeforeCreatingAnOwnedRun()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "missing-dependency-comparison");
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion, searchRoot: false));
        Assert.Equal(3, result);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task FailedFullSetStillRunsTheRemovalArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "runtime-failure-comparison");
        var arms = new List<string[]>();
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), args =>
        {
            arms.Add(args);
            return Task.FromResult(arms.Count == 1 ? 1 : 0);
        });
        Assert.Equal(1, result);
        Assert.Equal(2, arms.Count);
        Assert.Contains(companion, arms[0]);
        Assert.DoesNotContain(companion, arms[1]);
        Assert.True(File.Exists(Path.Combine(output, "before-dependencies.lock.json")));
        Assert.True(File.Exists(Path.Combine(output, "after-dependencies.lock.json")));
    }

    [Fact] public async Task RefusedFirstArmMarksTheIncompleteComparison()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "refused-arm-comparison");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), _ =>
        {
            calls++;
            return Task.FromResult(3);
        });
        Assert.Equal(3, result);
        Assert.Equal(1, calls);
        Assert.Contains("first arm refused", File.ReadAllText(Path.Combine(output, "REFUSED.txt")), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldIsRefusedWithoutShiftingArgumentsOrLaunchingAnArm(bool atEnd)
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, atEnd ? "trailing-hold" : "middle-hold");
        var args = Arguments(output, _rig.Parent, companion).ToList();
        args.Insert(atEnd ? args.Count : 2, "--hold");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync([.. args], _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(3, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact] public async Task BothArmsKeepParsedSearchRootsOptionalReferencesAndSwitches()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "parsed-comparison");
        string[] arguments = [.. Arguments(output, _rig.Parent, companion),
            "--server-only", "--optional-reference", "Optional.Plugin"];
        var arms = new List<ServerLoad.Arguments>();
        int result = await ServerLoadComparison.RunAsync(arguments, args =>
        {
            Assert.True(ServerLoad.TryRead(args, out var parsed, out string error), error);
            arms.Add(parsed!);
            return Task.FromResult(0);
        });
        Assert.Equal(0, result);
        Assert.Equal(2, arms.Count);
        Assert.All(arms, arm =>
        {
            Assert.True(arm.ServerOnly);
            Assert.Equal([Path.Combine(_rig.Root, "deps")], arm.Roots);
            Assert.Equal(["Optional.Plugin"], arm.Optional);
            Assert.DoesNotContain("--hold", arm.Switches);
        });
        Assert.Equal([_rig.Parent, companion], arms[0].Mods);
        Assert.Equal([_rig.Parent], arms[1].Mods);
        Assert.Equal(Path.Combine(output, "before"), arms[0].Options["--output"]);
        Assert.Equal(Path.Combine(output, "after"), arms[1].Options["--output"]);
    }

    [Fact] public async Task BothArmsUseOneFrozenSelectedEnvironment()
    {
        string companion = _rig.Write("companion/Companion.dll",
            RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "frozen-comparison");
        var arms = new List<ServerLoad.Arguments>();
        int result = await ServerLoadComparison.RunAsync(
            [.. Arguments(output, _rig.Parent, companion), "--server-only"],
            args =>
            {
                Assert.True(ServerLoad.TryRead(args, out var parsed, out string error), error);
                arms.Add(parsed!);
                return Task.FromResult(0);
            }, freezeInputs: true);
        Assert.Equal(0, result);
        Assert.Equal(2, arms.Count);
        string frozen = Path.Combine(output, "selection", "environments.json");
        Assert.All(arms, arm =>
        {
            Assert.Equal(frozen, arm.Options["--inventory"]);
            Assert.False(arm.Options.ContainsKey("--server"));
            Assert.True(arm.ServerOnly);
        });
        Assert.Single(EnvironmentInventory.Read(frozen).Environments);
    }

    [Fact]
    public async Task FrozenArmsKeepTheServerChosenBeforeDependencyResolution()
    {
        string inventory = Path.Combine(_rig.Root, "mutable-inventory.json");
        void Inventory(string install) => File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            environments = new[] { new { name = "local-server", roles = new[] { "server" }, install } },
        }));
        Inventory(_rig.Game);
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "frozen-choice-ab");
        var arguments = Arguments(output, _rig.Parent, companion).ToList();
        arguments.RemoveRange(0, 2); // The inventory supplies the server.
        arguments.AddRange(["--inventory", inventory, "--server-only"]);
        int resolved = 0;
        int result = await ServerLoadComparison.RunAsync([.. arguments],
            resolve: request =>
            {
                if (++resolved == 1) Inventory(Path.Combine(_rig.Root, "changed-after-selection"));
                return NativeDependencyResolver.Resolve(request);
            },
            runArm: _ => Task.FromResult(0), freezeInputs: true);

        Assert.Equal(0, result);
        Assert.Equal(2, resolved);
        string frozen = Path.Combine(output, "selection", "environments.json");
        Assert.Equal(_rig.Game, Assert.Single(EnvironmentInventory.Read(frozen).Environments).Install);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task BothArmsResolveAndBuildAgainstTheSelectedLoader(bool environmentPackage, bool explicitPackage)
    {
        var sourcePackage = BepInExLoaderPackage.Capture(_rig.Game, "source-loader", "1");
        (string Root, string Manifest) Package(string name)
        {
            string root = Path.Combine(_rig.Root, name);
            foreach (string relative in sourcePackage.Files.Keys)
            {
                string destination = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(_rig.Game, relative.Replace('/', Path.DirectorySeparatorChar)), destination);
            }
            string manifest = Path.Combine(_rig.Root, name + ".json");
            BepInExLoaderPackage.Capture(root, name, "2").Write(manifest);
            return (root, manifest);
        }
        var environment = Package("environment-loader");
        var explicitChoice = Package("explicit-loader");
        var shipped = Package("shipped-loader");
        var selected = explicitPackage ? explicitChoice : environmentPackage ? environment : shipped;
        string inventory = Path.Combine(_rig.Root, environmentPackage ? "inventory-with-loader.json" : "inventory-without-loader.json");
        File.WriteAllText(inventory, JsonSerializer.Serialize(new
        {
            environments = new[] { new { name = "local-server", roles = new[] { "server" }, install = _rig.Game,
                loaderPackage = environmentPackage ? environment.Manifest : null } },
        }));
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, environmentPackage ? "env-ab" : "shipped-ab");
        var arguments = Arguments(output, _rig.Parent, companion).ToList();
        arguments.RemoveRange(0, 2); // The inventory, not --server, owns this server's loader choice.
        int adapterAt = arguments.IndexOf("--adapter");
        arguments.RemoveRange(adapterAt, 2); // Exercise the adapter build's selected-core argument.
        arguments.AddRange(["--inventory", inventory, "--server-only"]);
        if (explicitPackage) arguments.AddRange(["--loader-package", explicitChoice.Manifest]);
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        var resolvedCores = new List<string>();
        var builtCores = new List<string?>();
        var arms = new List<ServerLoad.Arguments>();
        var armChoices = new List<ShippedLoader.Choice?>();
        int shippedCalls = 0;

        int result = await ServerLoadComparison.RunAsync([.. arguments],
        shippedLoader: (_, _) => { shippedCalls++; return new ShippedLoader.Choice(shipped.Manifest, "selected pinned pack"); },
        resolve: request => { resolvedCores.Add(request.BepInExCore); return NativeDependencyResolver.Resolve(request); },
        buildAdapter: (_, _, _, _, core) => { builtCores.Add(core); return Task.FromResult(adapter); },
        runArmWithSeams: (args, seams) =>
        {
            Assert.True(ServerLoad.TryRead(args, out var parsed, out string error), error);
            arms.Add(parsed!);
            armChoices.Add(seams.FrozenServerLoader);
            return Task.FromResult(0);
        });

        string expectedCore = Path.Combine(selected.Root, InstallPins.CoreDirectory);
        Assert.Equal(0, result);
        Assert.Equal(environmentPackage || explicitPackage ? 0 : 1, shippedCalls);
        Assert.Equal([expectedCore, expectedCore], resolvedCores);
        Assert.Equal([expectedCore], builtCores);
        Assert.Equal(2, arms.Count);
        Assert.All(arms, arm => Assert.Equal(selected.Manifest, arm.Options["--loader-package"]));
        Assert.All(armChoices, choice => Assert.Equal(environmentPackage || explicitPackage ? null : shipped.Manifest,
            choice?.Manifest));
    }

    [Fact] public async Task RefusedFullSetDoesNotLaunchTheRemovalArm()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "refused-comparison");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), _ =>
        {
            calls++;
            return Task.FromResult(3);
        });
        Assert.Equal(3, result);
        Assert.Equal(1, calls);
    }

    [Fact] public async Task FailedFullSetCannotHideAChangedSurvivingMod()
    {
        string companion = _rig.Write("companion/Companion.dll", RegressionRig.Assembly("Companion", new("example.companion")));
        string output = Path.Combine(_rig.Root, "changed-after-failure");
        int calls = 0;
        int result = await ServerLoadComparison.RunAsync(Arguments(output, _rig.Parent, companion), _ =>
        {
            calls++;
            File.AppendAllText(_rig.Parent, "changed");
            return Task.FromResult(1);
        });
        Assert.Equal(3, result);
        Assert.Equal(1, calls);
    }

    private string[] Arguments(string output, string first, string second, bool searchRoot = true)
    {
        string adapter = _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
            RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(SmokeSessionContract.SessionAdapterPluginGuid)));
        var args = new List<string>
        {
            "--server", _rig.Game, "--mod", first, "--mod", second, "--remove-mod", second,
            "--adapter", adapter, "--cli-manifest", _rig.CliManifest(save: true, full: true),
            "--cli-files", Path.Combine(_rig.Root, "cli"), "--output", output,
        };
        if (searchRoot) args.AddRange(["--search-root", Path.Combine(_rig.Root, "deps")]);
        return [.. args];
    }
}
