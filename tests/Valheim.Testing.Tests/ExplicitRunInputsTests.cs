using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class ExplicitRunInputsTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();
    internal const string Opaque = "space = extra= \"quote\" \\path café 雪 $HOME $(touch should-not-exist) `touch should-not-exist` %PATH%; end";

    [Fact]
    public void StartAndDedicatedAdaptersShareRepeatedInputConversion()
    {
        string[] args = ["--mod", "mod.dll", "--config", "one.cfg", "--config", "two.cfg", "--env", "MOD_SETTING=" + Opaque,
            "--env", "SECOND=two"];
        Assert.True(StartArguments.TryRead(args, out var start, out string error), error);
        Assert.True(ServerLoad.TryRead(args, out var server, out error), error);
        Assert.Equal(["one.cfg", "two.cfg"], start!.RunInputs.Configs);
        Assert.Equal(start.RunInputs.Configs, server!.RunInputs.Configs);
        Assert.Equal(Opaque, start.RunInputs.Environment["MOD_SETTING"]);
        Assert.Equal(start.RunInputs.Environment, server.RunInputs.Environment);
        Assert.Equal(2, start.RunInputs.Environment.Count);
        Assert.False(SmokeCommandOptions.TryRead(["--project", "mod.csproj", "--env", "FLAG=1"],
            SmokeCommandOptions.Command.Build, true, out _, out error));
        Assert.Equal("Unknown option: --env", error);
    }

    [Theory]
    [InlineData("MISSING")]
    [InlineData("=value")]
    [InlineData("NAME=")]
    [InlineData("bad-name=value")]
    [InlineData("9NAME=value")]
    [InlineData("NAME\n=value")]
    [InlineData("NAME\r=value")]
    [InlineData("NAME\r\n=value")]
    [InlineData("NA\0ME=value")]
    [InlineData("NA\nME=value")]
    [InlineData("NAME=line\nbreak")]
    [InlineData("NAME=line\rbreak")]
    [InlineData("NAME=NUL\0value")]
    [InlineData("NAME={runtime}")]
    [InlineData("NAME=prefix{world}suffix")]
    [InlineData("NAME={port}")]
    [InlineData("DOORSTOP_ENABLED=0")]
    [InlineData("doorstop_target_assembly=other.dll")]
    [InlineData("DOORSTOP_FOO=other")]
    [InlineData("doorstop_custom=other")]
    [InlineData("DYLD_FOO=other")]
    [InlineData("dyld_custom=other")]
    [InlineData("LD_LIBRARY_PATH=other")]
    [InlineData("DYLD_INSERT_LIBRARIES=other")]
    [InlineData("VT_NATIVE_SMOKE_SESSION_TOKEN=private-canary")]
    [InlineData("vt_native_smoke_join_password=private-canary")]
    [InlineData("VT_NATIVE_SMOKE_PLUGIN_GUIDS=other")]
    public void InvalidEnvironmentInputsRefuseAtTheSharedParseBeforeMachineSelection(string assignment)
    {
        string[] args = ["--mod", "missing.dll", "--env", assignment];
        Assert.False(StartArguments.TryRead(args, out _, out string start));
        Assert.False(ServerLoad.TryRead(args, out _, out string server));
        Assert.Equal(start, server);
        Assert.DoesNotContain("private-canary", start);
    }

    [Fact]
    public void DuplicateNamesAndTheSelectedCustomTokenRefuseWithoutPublishingValues()
    {
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--env", "FLAG=first-private-canary", "--env", "flag=second-private-canary"],
            out _, out string duplicate));
        Assert.DoesNotContain("private-canary", duplicate);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--session-capability", "example/session", "--session-token-variable", "CUSTOM_TOKEN",
            "--env", "custom_token=private-canary"], out _, out string token));
        Assert.DoesNotContain("private-canary", token);
    }

    [Theory]
    [InlineData("valheimCLI.valheimCLI.cfg")]
    [InlineData("VALHEIMCLI.VALHEIMCLI.CFG")]
    [InlineData("bad;name.cfg")]
    public void ConfigTargetsUseTheRuntimeOwnersAdmissionRules(string config)
    {
        Assert.False(StartArguments.TryRead(["--mod", "mod.dll", "--config", config], out _, out _));
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--config", config], out _, out _));
    }

    [Theory]
    [InlineData("a/other.cfg")]
    [InlineData("a\\other.cfg")]
    public void NestedConfigPathsKeepTheirSourceAndUseTheRuntimeOwnersBasename(string config)
    {
        string source = _rig.Root + "/" + config;
        bool accepted = config == "a/other.cfg" || OperatingSystem.IsWindows();
        Assert.Equal(accepted, StartArguments.TryRead(["--mod", "mod.dll", "--config", source], out var start, out _));
        Assert.Equal(accepted, ServerLoad.TryRead(["--mod", "mod.dll", "--config", source], out var server, out _));
        if (accepted)
        {
            Assert.Equal([source], start!.RunInputs.Configs);
            Assert.Equal([source], server!.RunInputs.Configs);
            _rig.Write("a/other.cfg", Encoding.UTF8.GetBytes("[Settings]\nSelected = true\n"));
            Assert.Equal("other.cfg", Assert.Single(ModInputStage.RetainExplicit(start.RunInputs,
                ModInputStage.Route.Hosted, Path.Combine(_rig.Root, "start-inputs"), true).Configs).Destination);
            Assert.Equal("other.cfg", Assert.Single(ModInputStage.RetainExplicit(server.RunInputs,
                ModInputStage.Route.Dedicated, Path.Combine(_rig.Root, "server-inputs"), false).Configs).Destination);
        }
    }

    [Fact]
    public void ConfigNamesIgnoreCaseAndDedicatedKeepsItsExistingNonCfgFiles()
    {
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--config", "one/mod.cfg", "--config", "two/MOD.CFG"], out _, out _));
        Assert.False(StartArguments.TryRead(["--mod", "mod.dll", "--config", "data.json"], out _, out _));
        Assert.True(ServerLoad.TryRead(["--mod", "mod.dll", "--config", "data.json"], out _, out string error), error);
    }

    [Fact]
    public void RetainedConfigsPreserveExactBytesAndRejectChangedEvidence()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("[Settings]\r\nMessage = café = yes\r\n");
        string source = _rig.Write("inputs/example.cfg", bytes), output = Path.Combine(_rig.Root, "run");
        var retained = ModInputStage.RetainExplicit(ModInputStage.ReadExplicit([source], ["MOD_SETTING=" + Opaque], true),
            ModInputStage.Route.Hosted, output, true);
        var config = Assert.Single(retained.Configs);
        Assert.Equal(bytes, File.ReadAllBytes(config.File));
        Assert.Equal(FileHash.Sha256(source), config.Sha256);
        Assert.Equal(source, config.Source);
        File.WriteAllText(source, "source changed after admission");
        retained.Verify(); // Subsequent arms still use the selected bytes.
        Assert.Equal(bytes, File.ReadAllBytes(config.File));
        var record = JsonDocument.Parse(File.ReadAllText(retained.RecordFile!)).RootElement;
        Assert.Equal(Opaque, record.GetProperty("environment").GetProperty("MOD_SETTING").GetString());
        Assert.Equal("client", Assert.Single(record.GetProperty("environmentRoles").EnumerateArray()).GetString());
        Assert.Equal(config.Sha256, record.GetProperty("configs")[0].GetProperty("Sha256").GetString());
        File.AppendAllText(config.File, "tampered");
        Assert.Throws<InvalidDataException>(retained.Verify);
    }

    [Fact]
    public void MissingOrLinkedConfigRefusesBeforeRetainingAnything()
    {
        string output = Path.Combine(_rig.Root, "refused");
        Assert.Throws<FileNotFoundException>(() => ModInputStage.RetainExplicit(
            ModInputStage.ReadExplicit([Path.Combine(_rig.Root, "missing.cfg")], [], true), ModInputStage.Route.Hosted, output, true));
        Assert.False(Directory.Exists(output));
        string source = _rig.Write("inputs/source.cfg", Encoding.UTF8.GetBytes("setting"));
        string linked = Path.Combine(_rig.Root, "linked.cfg");
        // Symlink privileges are not assumed on Windows; the staging tests there exercise reparse-point admission separately.
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(linked, source);
        Assert.Throws<FileNotFoundException>(() => ModInputStage.RetainExplicit(
            ModInputStage.ReadExplicit([linked], [], true), ModInputStage.Route.Hosted, output, true));
        Assert.False(Directory.Exists(output));
    }

    [OsFact("windows")]
    public async Task WindowsDirectAndDesktopLauncherDeliverOpaqueValuesToARealChild()
    {
        string probe = Path.Combine(AppContext.BaseDirectory, "Valheim.Testing.ProcessSignalProbe.exe");
        Assert.True(File.Exists(probe));
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Valheim.Testing.ProcessSignalProbe.*")
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "Valheim.Testing*.dll")))
            File.Copy(file, Path.Combine(_rig.Game, Path.GetFileName(file)), overwrite: true);
        File.Copy(probe, Path.Combine(_rig.Game, GameLaunch.ClientWindowsExecutable), overwrite: true);
        var environment = ModInputStage.ReadExplicit([], ["VT_TEST_INPUT=" + Opaque], true).Environment;
        string directOutput = Path.Combine(_rig.Root, "direct.txt");
        var direct = GameLaunch.LocalClient(_rig.Game, ["environment", directOutput], environment,
            ClientArchitecture.X64, false, ClientPlatform.Windows).ToStartInfo();
        using (var process = System.Diagnostics.Process.Start(direct)!)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(cancellation.Token); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Assert.Equal(0, process.ExitCode);
        }
        Assert.Equal(Opaque, File.ReadAllText(directOutput));
        string taskOutput = Path.Combine(_rig.Root, "desktop.txt"), boot = Path.Combine(_rig.Root, "desktop-launcher");
        Directory.CreateDirectory(boot);
        var launch = GameLaunch.ForClient(_rig.Game, ["environment", taskOutput], environment,
            hostPlatform: ClientPlatform.Windows, console: false);
        File.WriteAllText(Path.Combine(boot, "spec.txt"), launch.Spec());
        string launcher = Path.Combine(boot, "launch.ps1");
        File.WriteAllText(launcher, InteractiveScripts.WindowsLauncher);
        var host = new LocalGameHost("desktop-launcher-input", HostShell.Pwsh);
        // Run the actual scheduled task's payload through pwsh; no desktop or task registration is needed for this transport check.
        (await host.RunAsync("& $launcher", new Dictionary<string, string> { ["launcher"] = launcher }, TimeSpan.FromSeconds(30)))
            .EnsureSuccess("Running the desktop launch specification");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(taskOutput), TimeSpan.FromSeconds(10)));
        Assert.Equal(Opaque, File.ReadAllText(taskOutput));
        Assert.False(File.Exists(Path.Combine(_rig.Game, "should-not-exist")));
    }

    [ClientHostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartInputsReachTheActualRegressionProfileAndClientPlanAndSurviveRuntimeRemoval(bool noMod)
    {
        string source = _rig.Write("inputs/example.cfg", Encoding.UTF8.GetBytes("[Settings]\nMessage = selected\n"));
        string settings = _rig.Write("inputs/BepInEx.cfg", Encoding.UTF8.GetBytes("[Logging.Console]\nEnabled = false\n"));
        string output = Path.Combine(_rig.Root, "run");
        string[] selection = noMod ? ["--no-mod"] : ["--mod", _rig.Parent];
        Assert.True(StartArguments.TryRead([.. selection, "--config", source, "--config", settings, "--env", "MOD_SETTING=" + Opaque],
            out var parsed, out string error), error);
        var retained = ModInputStage.RetainExplicit(parsed!.RunInputs, ModInputStage.Route.Hosted, output, true);
        var inputs = noMod ? _rig.ControlManifest() : _rig.Manifest();
        retained.ApplyTo(inputs); // The same assignment boundary Program uses before writing regression.json.
        inputs.Write(Path.Combine(output, "regression.json"));
        var regression = _rig.Regression(inputs);
        regression.InitialConfigSha256 = retained.Configs.ToDictionary(config => config.Destination, config => config.Sha256, StringComparer.Ordinal);
        var staged = regression.Stage(noMod ? "control" : "parent");
        Assert.Equal(Opaque, staged.Plan.Environment["MOD_SETTING"]);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(regression.Install, "BepInEx", "config", "example.cfg")));
        Assert.Equal(File.ReadAllBytes(settings), File.ReadAllBytes(Path.Combine(regression.Install, "BepInEx", "config", "BepInEx.cfg")));
        Assert.Equal("[Chainloader]\nHideManagerGameObject = true\n", File.ReadAllText(Path.Combine(_rig.Game, "BepInEx", "config", "BepInEx.cfg")));
        Directory.Delete(regression.Install, recursive: true);
        retained.Verify();
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Assert.Single(retained.Configs, file => file.Destination == "example.cfg").File));
    }

    [ClientHostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartCarriesOriginalConfigPinsPastTheOuterInputVerification(bool noMod)
    {
        string source = _rig.Write("inputs/example.cfg", Encoding.UTF8.GetBytes("[Settings]\nMessage = selected\n"));
        var retained = ModInputStage.RetainExplicit(ModInputStage.ReadExplicit([source], [], true),
            ModInputStage.Route.Hosted, Path.Combine(_rig.Root, "run"), true);
        var inputs = noMod ? _rig.ControlManifest() : _rig.Manifest();
        retained.ApplyTo(inputs);
        var regression = _rig.Regression(inputs);
        regression.InitialConfigSha256 = retained.Configs.ToDictionary(config => config.Destination, config => config.Sha256, StringComparer.Ordinal);
        retained.Verify();
        File.AppendAllText(Assert.Single(retained.Configs).File, "changed after verification");
        // Do not call retained.Verify again: the staging owner must enforce the original pin itself.
        var failure = Assert.Throws<InvalidDataException>(() => regression.Stage(noMod ? "control" : "parent"));
        Assert.Contains("admitted SHA256", failure.Message);
        Assert.False(Directory.Exists(regression.Install));
    }

    [ClientHostFact]
    public async Task NoModKeepsTheConfigPinAcrossTheAsynchronousStorageCheck()
    {
        var host = FakeServerHost.ThisMachine();
        using var machine = LocalGameHost.UseThisMachineForTest(host);
        string source = _rig.Write("inputs/control.cfg", Encoding.UTF8.GetBytes("selected bytes"));
        var retained = ModInputStage.RetainExplicit(ModInputStage.ReadExplicit([source], [], true),
            ModInputStage.Route.Hosted, Path.Combine(_rig.Root, "control-inputs"), true);
        var inputs = _rig.ControlManifest(); retained.ApplyTo(inputs);
        var regression = _rig.Regression(inputs);
        regression.InitialConfigSha256 = retained.Configs.ToDictionary(config => config.Destination, config => config.Sha256, StringComparer.Ordinal);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        host.BeforeScriptAsync = async name =>
        {
            if (name != "apart") return;
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellation.Token);
        };
        var staging = Task.Run(() => regression.Stage("control"));
        Exception? stimulusFailure = null;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(staging.IsCompleted);
            var check = Assert.Single(host.Runs, run => run.Script == "apart");
            Assert.Equal(_rig.Game, check.Variables["root"]);
            Assert.Contains(regression.Install, check.Variables["paths"].Split('\n'));
            File.AppendAllText(Assert.Single(retained.Configs).File, " changed while storage was pending");
        }
        catch (Exception failure) { stimulusFailure = failure; }
        finally { release.TrySetResult(); }
        if (stimulusFailure != null)
        {
            try
            {
                await staging;
                regression.Remove(); // A failed stimulus may leave a successfully staged profile to retire.
            }
            catch (Exception settlementFailure)
            {
                throw new AggregateException("The config-change stimulus and staging or cleanup both failed.", stimulusFailure, settlementFailure);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stimulusFailure).Throw();
        }
        Assert.Contains("admitted SHA256", (await Assert.ThrowsAsync<InvalidDataException>(() => staging)).Message);
        Assert.False(Directory.Exists(regression.Install));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvenanceRefusesAModifiedPrivateRecordIndependentlyOfConfigDelivery(bool noMod)
    {
        var retained = ModInputStage.RetainExplicit(ModInputStage.ReadExplicit([], ["MOD_SETTING=selected"], true),
            ModInputStage.Route.Hosted, Path.Combine(_rig.Root, "run"), true);
        var inventory = _rig.Inventory();
        var actor = new ModInputStage.Actor("client", Assert.Single(inventory.Environments), "test", null, null, null);
        var inputs = new ModInputStage.Inputs(ModInputStage.Route.Hosted, inventory, actor, null, actor,
            "x64", null, [], noMod ? "explicit --no-mod" : "selected", null) { RunInputs = retained, NoMod = noMod };
        var provenance = new Dictionary<string, string>();
        inputs.Record(provenance);
        Assert.Equal(retained.RecordSha256, provenance["runInputsSha256"]);
        File.AppendAllText(retained.RecordFile!, "tampered");
        provenance.Clear();
        Assert.Contains("private run-inputs record changed", Assert.Throws<InvalidDataException>(() => inputs.Record(provenance)).Message);
        Assert.False(provenance.ContainsKey("runInputs"));
    }
}
