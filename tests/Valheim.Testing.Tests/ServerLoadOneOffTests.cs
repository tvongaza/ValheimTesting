using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// server-load as the one-off: the actors come from the inventory (this machine with no file), the derived campaign is
// written into the output, the host preflight runs before anything is copied, and nothing has to be written by hand.
public sealed class ServerLoadOneOffTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    private static readonly CampaignPreflightReport Ready = new([]);
    private string Adapter() => _rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
        RegressionRig.Assembly("NativeSmoke.SessionAdapter", new(NativeServerRuntime.SessionAdapterPluginGuid)));

    // The rig's game plays the dedicated server install: its managed assemblies and BepInEx core resolve the mod.
    private string[] Arguments(string output, params string[] more)
    {
        _rig.Write("game/valheim_server.exe", Encoding.UTF8.GetBytes("fake dedicated executable"));
        return ["--server", _rig.Game, "--mod", _rig.Parent, "--cli-manifest", _rig.CliManifest(save: true), "--cli-files", Path.Combine(_rig.Root, "cli"),
            "--search-root", Path.Combine(_rig.Root, "deps"), "--adapter", Adapter(), "--output", output, .. more];
    }

    // This machine with Valheim installed (a fake one, of this OS), so the default client is found.
    private static FakeMachine WithValheim(out string game)
    {
        var machine = new FakeMachine(HostProfile.CurrentPlatform);
        string steam = machine.Platform switch
        {
            "windows" => @"C:\Program Files (x86)\Steam", "macos" => "/Users/tester/Library/Application Support/Steam", _ => "/home/tester/.local/share/Steam",
        };
        machine.Directories.Add(steam);
        machine.Directories.Add(HostPath.Join(steam, "userdata"));
        game = machine.App(steam, "892970", "Valheim", machine.Platform switch
            { "windows" => GameLaunch.ClientWindowsExecutable, "macos" => "Valheim.app/Contents/MacOS/Valheim", _ => GameLaunch.ClientLinuxExecutable });
        return machine;
    }

    // A Mac keeps the staged path (the campaign runner has no macOS server); the campaign route runs on Windows and Linux.
    private static bool CampaignRoute => !OperatingSystem.IsMacOS();

    [Fact] public async Task TheDefaultIsAServerAndOneCleanClientAsADerivedCampaign()
    {
        if (!CampaignRoute) return;
        string output = Path.Combine(_rig.Root, "one-off");
        string? campaignFile = null; ServerRunPlan? plan = null; IReadOnlyDictionary<string, ClientRunPlan>? clients = null;
        int result;
        using (EnvironmentInventory.UseMachine(WithValheim(out string game)))
            result = await ServerLoad.RunAsync(Arguments(output), new ServerLoad.Seams(
                Inspect: _ => Task.FromResult(Ready),
                Campaign: (file, runPlan, bind, evidence, options) =>
                {
                    campaignFile = file; plan = runPlan; clients = bind(runPlan);
                    Assert.Equal(Path.Combine(output, "evidence"), evidence);
                    return Task.FromResult(0);
                }));
        Assert.Equal(0, result);
        Assert.Equal(Path.Combine(output, "campaign.json"), campaignFile);
        var campaign = JsonDocument.Parse(File.ReadAllText(campaignFile!)).RootElement;
        // --server is written as this machine's override; the client is this machine's detected Valheim.
        Assert.Equal(Path.Combine(output, "environments.json"), campaign.GetProperty("inventory").GetString());
        var inventory = EnvironmentInventory.Read(Path.Combine(output, "environments.json"), WithValheim(out _));
        Assert.Equal(["local-server", "local-client"], inventory.Environments.Select(recipe => recipe.Name));
        Assert.Equal(["local-server"], campaign.GetProperty("server").GetProperty("environmentCandidates").EnumerateArray().Select(name => name.GetString()));
        var client = campaign.GetProperty("clients").GetProperty("client");
        Assert.Equal(["local-client"], client.GetProperty("environmentCandidates").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(DefaultSmokeCharacter.Name, client.GetProperty("character").GetProperty("registeredName").GetString());
        Assert.Matches("^vt[0-9a-f]{8}$", client.GetProperty("character").GetProperty("fileName").GetString());
        Assert.Equal("127.0.0.1:2486", campaign.GetProperty("join").GetString());
        Assert.Equal(DefaultSmokeWorld.Uid, campaign.GetProperty("worldUid").GetString());
        // The client's lock holds ValheimCLI only; the server's holds the mod, and its own files the adapter.
        var clientLock = NativeDependencyLock.ReadReady(client.GetProperty("dependencyLock").GetString()!);
        Assert.Empty(clientLock.Mods);
        Assert.Contains(campaign.GetProperty("server").GetProperty("files").EnumerateArray(),
            file => file.GetProperty("relativePath").GetString() == "BepInEx/plugins/NativeSmoke.SessionAdapter.dll");
        Assert.Equal(5688, plan!.Port);
        Assert.Equal("2486", plan.Arguments[plan.Arguments.IndexOf("-port") + 1]);
        var clientPlan = Assert.Single(clients!).Value;
        Assert.All(clientPlan.Pins, pin => Assert.Equal("absent", pin.Value)); // the clean client loads none of the server's plugins
        Assert.NotEmpty(clientPlan.Pins);
        Assert.False(Directory.Exists(Path.Combine(output, "consumer")));
        // The unbound plans beside it, for an editable consumer to run the same campaign again.
        Assert.Equal("2486", ServerRunPlan.Read<ServerRunPlan>(Path.Combine(output, "plan.json")).Arguments.SkipWhile(argument => argument != "-port").ElementAt(1));
        Assert.True(File.Exists(Path.Combine(output, "client-plan.json")));
        // The campaign is the shared one: its static preflight reads it as written.
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            Assert.DoesNotContain(HostedCampaignPreparation.Inspect(campaignFile!).Problems, problem => problem.Input is "inventory" or "manifest");
    }

    // No client install: refused with the reason and --server-only, never run without its client.
    [Fact] public async Task AClientThatCannotRunIsRefusedNeverDropped()
    {
        if (!CampaignRoute) return;
        bool ran = false;
        string output = Path.Combine(_rig.Root, "no-client");
        int result = await ServerLoad.RunAsync(Arguments(output), new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); }));
        Assert.Equal(3, result);
        Assert.False(ran);
        Assert.True(ServerLoad.TryRead(Arguments(Path.Combine(_rig.Root, "unused")), out var parsed, out _));
        var refused = Assert.Throws<ArgumentException>(() => ServerLoad.Choose(parsed!, Path.Combine(_rig.Root, "choice")));
        Assert.Contains("--server-only", refused.Message);
        Assert.Contains("892970", refused.Message); // what was looked for

        // A host check that refuses the client (here: no signed-in Steam, and a mod manager's Doorstop proxy) stops before
        // anything is copied or launched, names the reviewed-loader fix, and marks the folder as a refused run.
        string checkedOutput = Path.Combine(_rig.Root, "client-refused");
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            result = await ServerLoad.RunAsync(Arguments(checkedOutput), new ServerLoad.Seams(
                Inspect: _ => Task.FromResult(new CampaignPreflightReport([
                    new("client", "Steam identity", "No Steam account is signed in on local."),
                    new("client", "game and loader", "The source install on local's winhttp.dll is Doorstop 4 (file version 4.4.0), which reads only [General] in doorstop_config.ini, but that file is written for Doorstop 3 ([UnityDoorstop])."),
                ])),
                Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); }));
        Assert.Equal(3, result);
        Assert.False(ran);
        string refusal = File.ReadAllText(Path.Combine(checkedOutput, "REFUSED.txt"));
        Assert.Contains("not a prepared run", refusal);
        Assert.Contains("--client-loader-package FILE", refusal);
        Assert.Contains("No Steam account is signed in", refusal);
        Assert.False(File.Exists(Path.Combine(checkedOutput, "plan.json"))); // the password is written only once the preflight passed

        // With a reviewed package already given, a loader refusal is the package's own (here: another platform's loader);
        // the hint would name the option that caused it, so there is none.
        string package = Path.Combine(_rig.Root, "client-loader.json");
        BepInExLoaderPackage.Capture(_rig.Game, "reviewed", "1").Write(package);
        string packagedOutput = Path.Combine(_rig.Root, "client-package-refused");
        using (EnvironmentInventory.UseMachine(WithValheim(out _)))
            result = await ServerLoad.RunAsync(Arguments(packagedOutput, "--client-loader-package", package), new ServerLoad.Seams(
                Inspect: _ => Task.FromResult(new CampaignPreflightReport([new("client", "game and loader", "The reviewed loader package does not match the host platform.")])),
                Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); }));
        Assert.Equal(3, result);
        Assert.False(ran);
        refusal = File.ReadAllText(Path.Combine(packagedOutput, "REFUSED.txt"));
        Assert.Contains("does not match the host platform", refusal);
        Assert.DoesNotContain("--client-loader-package FILE", refusal);
    }

    [Fact] public async Task ServerOnlySkipsTheClientAndPreflightOnlyStopsBeforeTheRun()
    {
        if (!CampaignRoute) return;
        bool ran = false;
        string output = Path.Combine(_rig.Root, "server-only");
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only", "--preflight-only"), new ServerLoad.Seams(
            Inspect: _ => Task.FromResult(Ready), Campaign: (_, _, _, _, _) => { ran = true; return Task.FromResult(0); }));
        Assert.Equal(0, result);
        Assert.False(ran);
        var campaign = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "campaign.json"))).RootElement;
        Assert.Empty(campaign.GetProperty("clients").EnumerateObject());
        Assert.True(File.Exists(Path.Combine(output, "plan.json"))); // the consumer's input, written with the campaign
        Assert.False(File.Exists(Path.Combine(output, "REFUSED.txt")));
        Assert.Single(EnvironmentInventory.Read(Path.Combine(output, "environments.json"), new FakeMachine(HostProfile.CurrentPlatform)).Environments);
    }

    // An install whose own Doorstop pair does not match takes the shipped BepInExPack (ShippedLoader): the campaign's role
    // names that package, and an install that needs none is left to its own loader.
    [Fact] public async Task AShippedLoaderChosenForAnInstallBecomesItsRolesLoaderPackage()
    {
        if (!CampaignRoute) return;
        string package = Path.Combine(_rig.Root, "shipped-loader.json");
        BepInExLoaderPackage.Capture(_rig.Game, "BepInExPack_Valheim", "5.4.2351").Write(package);
        var asked = new List<string>();
        string output = Path.Combine(_rig.Root, "shipped-server");
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only", "--preflight-only"), new ServerLoad.Seams(
            Inspect: _ => Task.FromResult(Ready), Campaign: (_, _, _, _, _) => Task.FromResult(0),
            Loader: (actor, install) => { asked.Add(actor); return new ShippedLoader.Choice(package, install + " mismatched"); }));
        Assert.Equal(0, result);
        Assert.Equal(new[] { "server" }, asked); // --server-only: no client to ask about
        var campaign = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "campaign.json"))).RootElement;
        Assert.Equal(package, campaign.GetProperty("server").GetProperty("loaderPackage").GetString());

        // An explicit package is the actor's own choice: the shipped one is not considered.
        asked.Clear();
        result = await ServerLoad.RunAsync(Arguments(Path.Combine(_rig.Root, "explicit"), "--server-only", "--preflight-only", "--loader-package", package),
            new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready), Campaign: (_, _, _, _, _) => Task.FromResult(0),
                Loader: (actor, _) => { asked.Add(actor); return null; }));
        Assert.Equal(0, result);
        Assert.Empty(asked);
    }

    // The server must be on this machine; a client elsewhere needs --join unless it can be inferred.
    [Fact] public void ARemoteServerIsRefusedAndARemoteClientNeedsAJoinAddress()
    {
        if (!CampaignRoute) return; // a Mac's own server environment is refused as a macOS server first
        string file = Path.Combine(_rig.Root, "lab.json");
        bool windows = OperatingSystem.IsWindows();
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            hosts = new { lab = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@lab", @lock = "/vt/lock" } },
            environments = new object[]
            {
                new { name = "lab-server", host = "lab", roles = new[] { "server" }, install = "/opt/server", runtime = "/vt/runs", cliPort = 5700, gamePort = 2496 },
                new { name = "lab-client", host = "lab", roles = new[] { "client" }, install = "/opt/valheim", runtime = "/vt/runs", cliPort = 5701 },
                new { name = "local-server", roles = new[] { "server" }, install = windows ? @"C:\Server" : "/srv/server" },
            },
            leaseHost = "lab", leaseDirectory = "/vt/leases",
        }));
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file], out var remote, out _));
        Assert.Contains("PinnedServerRun --inventory", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(remote!, _rig.Root)).Message);
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file, "--server-env", "local-server"], out var local, out _));
        Assert.Contains("--join HOST:2486", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(local!, _rig.Root)).Message);
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file, "--server-env", "local-server", "--join", "pc.lan:2456"], out var wrongPort, out _));
        Assert.Contains("game port 2486", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(wrongPort!, _rig.Root)).Message);
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", file, "--server-env", "local-server", "--join", "pc.lan:2486"], out var joined, out _));
        var choice = ServerLoad.Choose(joined!, _rig.Root);
        Assert.Equal(("local-server", "lab-client", "pc.lan:2486"), (choice.Server.Name, choice.Client!.Name, choice.Join));
    }

    [Fact] public void ConflictingOptionsAreRefusedBeforeAnything()
    {
        foreach (string[] args in new string[][]
        {
            ["--server", "a"], // no --mod
            ["--mod", "a.dll", "--server-only", "--client", "c"],
            ["--mod", "a.dll", "--server-only", "--join", "h:1"],
            ["--mod", "a.dll", "--port", "1"], // the port flags are gone: the inventory chooses
            ["--mod", "a.dll", "--server-only", "--steam-userdata", "u"],
            ["--mod", "a.dll", "--server-only", "--server-only"],
        })
            Assert.False(ServerLoad.TryRead(args, out _, out _), string.Join(" ", args));
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--inventory", "f.json", "--server", "s"], out var both, out _));
        Assert.Contains("--inventory", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(both!, _rig.Root)).Message);
        // An output inside a named install is refused before anything is written there.
        string server = Path.Combine(_rig.Root, "server-install");
        Assert.True(ServerLoad.TryRead(["--mod", "a.dll", "--server", server, "--server-only"], out var inside, out _));
        Assert.Contains("outside the prepared install", Assert.Throws<ArgumentException>(() => ServerLoad.Choose(inside!, Path.Combine(server, "run"))).Message);
        Assert.False(Directory.Exists(server));
    }

    // What the campaign route cannot use, or the staged path cannot do, is refused before anything is copied.
    [Fact] public async Task RouteSpecificOptionsAreRefused()
    {
        string output = Path.Combine(_rig.Root, "route");
        Assert.Equal(3, await ServerLoad.RunAsync(Arguments(output + "-userdata", "--steam-userdata", _rig.Root), new ServerLoad.Seams(MacOS: false)));
        Assert.Equal(3, await ServerLoad.RunAsync(Arguments(output + "-mac", "--server-only", "--preflight-only"), new ServerLoad.Seams(MacOS: true,
            Staged: (_, _) => throw new InvalidOperationException("never launched"))));
        Assert.Equal(3, await ServerLoadComparison.RunAsync(["--mod", "a.dll", "--mod", "b.dll", "--remove-mod", "b.dll", "--output", output + "-ab", "--preflight-only"],
            _ => throw new InvalidOperationException("no arm runs")));
    }

    // A server install without BepInEx is named as such, with the two ways to supply a loader, before resolution.
    [Fact] public async Task AServerWithoutBepInExIsRefusedPlainly()
    {
        if (!CampaignRoute) return;
        string output = Path.Combine(_rig.Root, "no-bepinex");
        var args = Arguments(output, "--server-only");
        Directory.Delete(Path.Combine(_rig.Game, "BepInEx", "core"), recursive: true);
        Assert.Equal(3, await ServerLoad.RunAsync(args, new ServerLoad.Seams(Inspect: _ => Task.FromResult(Ready),
            Campaign: (_, _, _, _, _) => throw new InvalidOperationException("never run"))));
        Assert.False(File.Exists(Path.Combine(output, "dependencies.lock.json")));
    }

    // A Mac stays on the staged local copies, says so, and refuses the campaign-only options.
    [Fact] public async Task AMacKeepsTheStagedLocalPath()
    {
        string output = Path.Combine(_rig.Root, "mac");
        var launched = new List<string[]>();
        int result = await ServerLoad.RunAsync(Arguments(output, "--server-only"), new ServerLoad.Seams(MacOS: true,
            Staged: (arguments, options) =>
            {
                launched.Add(arguments);
                Assert.NotNull(options.StagedRuntime);
                return Task.FromResult(0);
            }));
        Assert.Equal(0, result);
        Assert.Equal(Path.Combine(output, "plan.json"), Assert.Single(launched)[1]);
        Assert.False(File.Exists(Path.Combine(output, "campaign.json")));
        Assert.Equal(3, await ServerLoad.RunAsync(Arguments(Path.Combine(_rig.Root, "mac-inventory"), "--server-only", "--inventory", "f.json"),
            new ServerLoad.Seams(MacOS: true, Staged: (_, _) => Task.FromResult(0))));
    }

    // start's client is the inventory's: this machine's Valheim, --game and --loader-package as its override, or a file's
    // (--client-env picks one). It must be on this machine, and the chosen one is recorded beside the run's inputs.
    [Fact] public void StartTakesItsClientFromTheInventoryAndRecordsIt()
    {
        string output = Path.Combine(_rig.Root, "start");
        string game;
        using (EnvironmentInventory.UseMachine(WithValheim(out game)))
        {
            var (_, client, _) = SmokeInputs.Client(new Dictionary<string, string>(), output);
            Assert.Equal(("local-client", game), (client.Name, client.Install));
            Assert.EndsWith("userdata", SmokeInputs.SteamUserdata(new Dictionary<string, string>()));
        }
        Assert.Equal(game, EnvironmentInventory.Read(Path.Combine(output, "environments.json"), new FakeMachine(HostProfile.CurrentPlatform)).Environments.Single().Install);
        var (_, overridden, _) = SmokeInputs.Client(new Dictionary<string, string> { ["--game"] = _rig.Game }, Path.Combine(_rig.Root, "start-game"));
        Assert.Equal(_rig.Game, overridden.Install);

        // A file with two clients here and one elsewhere: --client-env picks, and only that one is recorded.
        string file = Path.Combine(_rig.Root, "lab.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            hosts = new { lab = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@lab", @lock = "/vt/lock" } },
            environments = new object[]
            {
                new { name = "lab-client", host = "lab", roles = new[] { "client" }, install = "/opt/valheim", runtime = "/vt/runs", cliPort = 5700 },
                new { name = "first", roles = new[] { "client" }, install = _rig.Game, runtime = Path.Combine(_rig.Root, "runs-first"), cliPort = 5701 },
                new { name = "alt", roles = new[] { "client" }, install = _rig.Game, runtime = Path.Combine(_rig.Root, "runs-alt"), cliPort = 5702 },
            },
            leaseHost = "lab", leaseDirectory = "/vt/leases",
        }));
        Assert.Contains("not this machine", Assert.Throws<ArgumentException>(() =>
            SmokeInputs.Client(new Dictionary<string, string> { ["--inventory"] = file }, Path.Combine(_rig.Root, "start-remote"))).Message);
        string chosen = Path.Combine(_rig.Root, "start-alt");
        var (_, alt, _) = SmokeInputs.Client(new Dictionary<string, string> { ["--inventory"] = file, ["--client-env"] = "alt" }, chosen);
        Assert.Equal(5702, alt.CliPort);
        var recorded = EnvironmentInventory.Read(Path.Combine(chosen, "environments.json"), new FakeMachine(HostProfile.CurrentPlatform));
        Assert.Equal(("alt", 5702), (recorded.Environments.Single().Name, recorded.Environments.Single().CliPort));

        Assert.Contains("Give --game", Assert.Throws<ArgumentException>(() => SmokeInputs.Client(new Dictionary<string, string>(), Path.Combine(_rig.Root, "none"))).Message);
        Assert.Contains("--inventory", Assert.Throws<ArgumentException>(() => SmokeInputs.Client(
            new Dictionary<string, string> { ["--game"] = _rig.Game, ["--inventory"] = "f.json" }, Path.Combine(_rig.Root, "both"))).Message);
        Assert.Contains("--steam-userdata", Assert.Throws<DirectoryNotFoundException>(() => SmokeInputs.SteamUserdata(new Dictionary<string, string>())).Message);
    }
}
