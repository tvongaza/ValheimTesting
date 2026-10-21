using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class DetachedModSwapTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vt-swap-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Write(string relative, string text) => Write(relative, System.Text.Encoding.UTF8.GetBytes(text));
    private string Write(string relative, byte[] bytes)
    {
        string file = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes); return file;
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AtomicSwapKeepsFixedFilesAndSourceBytesAndJournalsBeforeEffects()
    {
        string target = Write("profile/BepInEx/plugins/Mod.dll", "old"), source = Write("build/Mod.dll", "new");
        string companion = Write("profile/BepInEx/config/mod.cfg", "fixed");
        var events = new List<string>();
        StoppedPluginSwap.Apply(Path.Combine(_root, "profile"), "BepInEx/plugins/Mod.dll", source,
            FileHash.Sha256(target), FileHash.Sha256(source), Guid.NewGuid().ToString("N"), () => { }, entry =>
            {
                Assert.Equal("old", File.ReadAllText(target)); events.Add(entry.Kind);
                if (entry.Kind == "checkpoint-plugin-swap-intended") Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
                else Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(target)!).Length);
            }, default);
        Assert.Equal(["checkpoint-plugin-swap-intended", "checkpoint-plugin-activation-intended"], events);
        Assert.Equal("new", File.ReadAllText(target)); Assert.Equal("new", File.ReadAllText(source)); Assert.Equal("fixed", File.ReadAllText(companion));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Theory]
    [InlineData("intent")][InlineData("activation")][InlineData("finish")][InlineData("cancel")][InlineData("source")]
    public void FailedAdmissionOrJournalNeverActivatesAndInterruptedStageRemainsInsideOwnedProfile(string failure)
    {
        string target = Write("profile/BepInEx/plugins/Mod.dll", "old"), source = Write("build/Mod.dll", "new");
        using var cancellation = new CancellationTokenSource();
        int admissions = 0;
        Assert.ThrowsAny<Exception>(() => StoppedPluginSwap.Apply(Path.Combine(_root, "profile"), "BepInEx/plugins/Mod.dll", source,
            FileHash.Sha256(target), FileHash.Sha256(source), Guid.NewGuid().ToString("N"), () =>
            {
                if (++admissions != 2) return;
                if (failure == "finish") throw new InvalidOperationException("finish requested");
                if (failure == "cancel") cancellation.Cancel();
                if (failure == "source") { File.WriteAllText(source, "drift"); throw new InvalidDataException("source drift"); }
            }, entry =>
            {
                if (failure == "intent" || failure == "activation" && entry.Kind == "checkpoint-plugin-activation-intended") throw new IOException("journal failed");
            }, cancellation.Token));
        Assert.Equal("old", File.ReadAllText(target));
        if (failure == "intent") Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
        else Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(target)!).Length);
    }

    [Fact]
    public void SourceAndTargetLinksAndOversizeInputsAreRefused()
    {
        string source = Write("source/Mod.dll", "bytes"), linked = Path.Combine(_root, "link.dll");
        File.CreateSymbolicLink(linked, source);
        Assert.Throws<InvalidDataException>(() => DetachedModSwap.RequireRegular(linked, 100));
        Assert.Throws<InvalidDataException>(() => DetachedModSwap.RequireRegular(source, 1));
        string directory = Path.Combine(_root, "linked-directory"); Directory.CreateSymbolicLink(directory, Path.GetDirectoryName(source)!);
        Assert.Throws<InvalidDataException>(() => DetachedModSwap.RequireRegular(Path.Combine(directory, "Mod.dll"), 100));
        Assert.Throws<InvalidDataException>(() => DetachedModSwap.BoundedManifest(_root));
        string target = Write("profile/BepInEx/plugins/Mod.dll", "old"); File.Delete(target); File.CreateSymbolicLink(target, source);
        Assert.ThrowsAny<Exception>(() => StoppedPluginSwap.Apply(Path.Combine(_root, "profile"), "BepInEx/plugins/Mod.dll", source,
            FileHash.Sha256(source), FileHash.Sha256(source), Guid.NewGuid().ToString("N"), () => { }, _ => { }, default));
        Assert.Equal("bytes", File.ReadAllText(source));
    }

    [Fact]
    public void TraversalAndWrongHashShapeAreRejectedBeforePublishing()
    {
        var input = new DetachedModSwap.Input(Path.Combine(_root, "Mod.dll"), new string('a', 64), Path.Combine(_root, "build.json"), Path.Combine(_root, "lock.json"));
        DetachedModSwap.Validate(input);
        Assert.Throws<InvalidDataException>(() => DetachedModSwap.Validate(input with { Mod = Path.Combine(_root, "..", "Mod.dll") }));
        Assert.Throws<InvalidDataException>(() => DetachedModSwap.Validate(input with { Sha256 = "wrong" }));
    }

    private (DetachedModSwap Swap, DetachedModSwap.Input Input) Prepare(string fault = "")
    {
        string game = Path.Combine(_root, "game"), core = Path.Combine(game, "BepInEx", "core");
        string assembly = Write("game/valheim_server_Data/Managed/assembly_valheim.dll", "game");
        Write("game/valheim_server_Data/Managed/System.Private.CoreLib.dll", "core");
        string bepinex = Write("game/BepInEx/core/BepInEx.dll", "bepinex"), harmony = Write("game/BepInEx/core/0Harmony.dll", "harmony");
        string original = Write("original/Mod.dll", RegressionRig.Assembly("Mod", new("example.mod"), marker: "before"));
        string candidate = Write("candidate/Mod.dll", RegressionRig.Assembly("Mod", new(fault == "guid" ? "other.mod" : "example.mod")
            { Hard = fault == "dependency" ? ["missing.dep"] : [] }, marker: "after"));
        string cli = Write("cli/valheimCLI.dll", RegressionRig.Assembly("valheimCLI", new("valheimCLI.valheimCLI")));
        var cliManifest = new CliCapabilityManifest { Build = "test", Plugin = new() { File = "valheimCLI.dll", Sha256 = FileHash.Sha256(cli), Plugins = ["valheimCLI.valheimCLI"], Extensions = new() { ["valheim.session"] = new() { ["state"] = 1 } } } };
        string manifest = Path.Combine(_root, "cli.json"); cliManifest.Write(manifest);
        NativeDependencyLock Resolve(string mod) => NativeDependencyResolver.Resolve(new() { Mods = [mod], GameManaged = Path.GetDirectoryName(assembly)!, BepInExCore = core, CliFiles = Path.GetDirectoryName(cli)!, CliManifest = manifest });
        var initial = Resolve(original); Assert.True(initial.Ready);
        var next = Resolve(candidate);
        if (fault == "dependency") next.Gaps.Clear(); // A forged ready lock must still fail the independent closure resolution.
        if (fault == "duplicate") next.Mods.Add(next.Mods[0]);
        if (fault == "oversize")
        {
            string huge = Write("huge/Library.dll", "");
            using (var stream = new FileStream(huge, FileMode.Open, FileAccess.Write)) stream.SetLength(DetachedModSwap.MaximumBytes + 1);
            var pin = new NativeDependencyFile(huge, new string('a', 64), "bounded before hash");
            initial.Plugins.Add(pin); next.Plugins.Add(pin);
        }
        if (fault == "cli-link")
        {
            string sibling = Path.Combine(_root, "sibling"); Directory.CreateDirectory(sibling);
            Directory.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(cli)!, "linked"), sibling);
        }
        string dependencies = Path.Combine(_root, "lock.json"); next.Write(dependencies);
        string publicized = Write("references/assembly_valheim_publicized.dll", "publicized");
        ModBuild.FilePin Pin(string file) => new(file, FileHash.Sha256(file));
        var build = new ModBuild.BuildRecord(game, InstallPins.GameHash(game), Path.GetDirectoryName(assembly)!, candidate,
            FileHash.Sha256(candidate), "BepInEx.AssemblyPublicizer/0.4.3", [Pin(assembly)], [Pin(publicized)], [Pin(bepinex), Pin(harmony)]);
        string buildFile = Write("build.json", JsonSerializer.Serialize(build));
        string evidence = Path.Combine(_root, "evidence"); Directory.CreateDirectory(evidence);
        var placement = new Placement(game);
        var server = new ServerActor(placement, new ServerRunPlan { Pins = new() { ["example.mod"] = FileHash.Md5(original) } }, evidence, "test/session", "TEST_SESSION", default);
        return (new(server, initial, evidence), new(candidate, fault == "hash" ? new string('a', 64) : FileHash.Sha256(candidate), buildFile, dependencies));
    }

    [Theory]
    [InlineData("hash")][InlineData("guid")][InlineData("dependency")][InlineData("duplicate")]
    public void WrongHashGuidDependencyAndDuplicateAreRefusedBeforeActivation(string fault)
    {
        var (swap, input) = Prepare(fault);
        Assert.ThrowsAny<Exception>(() => swap.Prepare(input, Guid.NewGuid().ToString("N"), () => { }, default));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "evidence"), "detached-swap-*.json").Where(file => !file.EndsWith(".cli.json", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("oversize", "too large")][InlineData("cli-link", "contains a link")]
    public void LimitsAndCliParentLinksAreRejectedBeforeHashingOrRecursiveResolution(string fault, string message)
    {
        var (swap, input) = Prepare(fault);
        var error = Assert.Throws<InvalidDataException>(() => swap.Prepare(input, Guid.NewGuid().ToString("N"), () => { }, default));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("gaps")][InlineData("mods")][InlineData("cliManifest")][InlineData("mod-entry")][InlineData("Sources")][InlineData("build-entry")]
    public void MalformedManifestCollectionsAndEntriesRefuseWithoutEndingTheSupervisor(string field)
    {
        var (swap, input) = Prepare();
        bool build = field is "Sources" or "build-entry";
        string path = build ? input.BuildInputs : input.Dependencies;
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        if (field == "mod-entry") json["mods"]![0] = null;
        else if (field == "build-entry") json["Sources"]![0] = null;
        else json[field] = null;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => swap.Prepare(input, Guid.NewGuid().ToString("N"), () => { }, default));
    }

    [Fact]
    public void EquivalentAbsolutePathSpellingsSelectTheSamePinnedMod()
    {
        var (swap, input) = Prepare();
        string canonical = Path.GetFullPath(input.Mod);
        var spellings = new List<string> { canonical };
        if (OperatingSystem.IsWindows())
        {
            spellings.Add(canonical.Replace('\\', '/'));
            spellings.Add(canonical.ToUpperInvariant());
        }
        foreach (string spelling in spellings)
            Assert.NotNull(swap.Prepare(input with { Mod = spelling }, Guid.NewGuid().ToString("N"), () => { }, default));
    }

    [Fact]
    public void SourceDriftAfterValidationIsRefusedBeforeTheStoppedActorIsTouched()
    {
        var (swap, input) = Prepare();
        var activate = swap.Prepare(input, Guid.NewGuid().ToString("N"), () => { }, default);
        File.AppendAllText(input.Mod, "drift");
        Assert.Contains("hash", Assert.ThrowsAny<Exception>(activate).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwnedSessionTransitionsOnlyAfterCleanStopAndTheNextBootVerifiesTheNewPin()
    {
        string oldHash = new('a', 32), newHash = new('b', 32), other = new('c', 32);
        var fake = new FakeOwnedServer();
        int boot = 0;
        string recording = Path.Combine(_root, "commands-");
        using var session = new OwnedServerSession(fake.Launch, () => new RecordingTransport(fake.Connect(), recording + ++boot + ".jsonl"), fake.SaveRoot,
            $"cli_expect example.mod={oldHash} worlduid=1 other.mod={other}", fake.SessionCapability, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        Assert.Throws<InvalidOperationException>(() => session.ReplaceStoppedPluginExpectation("example.mod", oldHash, newHash));
        session.Start();
        Assert.Throws<InvalidOperationException>(() => session.ReplaceStoppedPluginExpectation("example.mod", oldHash, newHash));
        session.Stop();
        Assert.Throws<InvalidOperationException>(() => session.ReplaceStoppedPluginExpectation("example.mod", newHash, oldHash));
        session.ReplaceStoppedPluginExpectation("example.mod", oldHash, newHash);
        session.Start();
        session.Stop(); // Closes the recording writer before Windows can open it for reading.
        string text = File.ReadAllText(recording + "2.jsonl");
        Assert.Contains("example.mod=" + newHash, text); Assert.Contains("other.mod=" + other, text); Assert.Contains("worlduid=1", text);
    }

    [Fact]
    public void PreparedProfileTransitionLeavesOriginalSourceAndOtherFilesPinned()
    {
        var files = new Dictionary<string, string> { ["BepInEx/plugins/Mod.dll"] = "before", ["BepInEx/plugins/Companion.dll"] = "fixed" };
        var states = files.ToDictionary(file => file.Key, _ => new HostFileState(1, "time"));
        var source = new HostListing("local", HostShellKind.PowerShell, "source", files, [], states);
        var check = ProfileSourceCheck.Prepare(source, [], ProfileLayout.SourceExecutable);
        var profile = new HostListing("local", HostShellKind.PowerShell, "owned", files, [], states);
        check.RecordPreparedProfile(profile, profile, []);
        Assert.Throws<InvalidOperationException>(() => check.ReplacePreparedPlugin("BepInEx/plugins/Mod.dll", "wrong", "after"));
        check.ReplacePreparedPlugin("BepInEx/plugins/Mod.dll", "before", "after");
        Assert.Equal("after", check.PreparedLoader.Files["BepInEx/plugins/Mod.dll"]);
        Assert.Equal("after", check.PreparedGame.Files["BepInEx/plugins/Mod.dll"]);
        Assert.Equal("before", check.Source.Files["BepInEx/plugins/Mod.dll"]);
        Assert.Equal("fixed", check.PreparedLoader.Files["BepInEx/plugins/Companion.dll"]);
    }

    [Fact]
    public async Task ExactRecoveryRemovesInterruptedSwapBytesInsideTheOwnedProfileOnly()
    {
        string data = Path.Combine(_root, "data"), runId = "run-swap-recovery";
        using var machine = new FakeDataRoot(data);
        using var run = RunJournal.UseRun(runId);
        string source = Write("build/Mod.dll", "new"), unrelated = Write("unrelated/keep", "keep");
        string owned = JournalOwnedDirectory.Create(Path.Combine(data, "runs"), "server", "swap profile", directory =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(directory, "BepInEx", "plugins", "Mod.dll"), "old");
        });
        string target = Path.Combine(owned, "BepInEx", "plugins", "Mod.dll");
        Assert.Throws<IOException>(() => StoppedPluginSwap.Apply(owned, "BepInEx/plugins/Mod.dll", source,
            FileHash.Sha256(target), FileHash.Sha256(source), Guid.NewGuid().ToString("N"), () => { }, entry =>
            {
                if (entry.Kind == "checkpoint-plugin-activation-intended") throw new IOException("interrupted activation journal");
                new RunJournal(runId).AppendLocal("server", entry);
            }, default));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(target)!).Length);
        new RunJournal(runId).AppendLocal("run", JournalEntry.Of(JournalEntry.RunEnded, ("state", "failed"), ("cleanupVerified", "false")));
        var profile = new HostProfile { Kind = "local", Platform = HostProfile.CurrentPlatform,
            Shell = HostProfile.ShellFor(HostProfile.CurrentPlatform), Lock = Path.Combine(data, "lock") };
        var recovered = await RunRecovery.RecoverAsync(new Dictionary<string, HostProfile> { ["local"] = profile },
            _ => new LocalGameHost("local", HostShell.ThisMachine), runId, teardown: false, TimeSpan.FromSeconds(30));
        Assert.True(recovered.Clean, string.Join("\n", recovered.Steps));
        Assert.False(Directory.Exists(owned)); Assert.Equal("new", File.ReadAllText(source)); Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    private sealed class Placement(string game) : IServerPlacement
    {
        public string RuntimeDirectory => game;
        public string WorldDirectory => game;
        public IGameHost? Host => null;
        public ServerPlatform? Platform => null;
        public ServerBoot Start(int boot, GameLaunch launch, string output, TimeSpan quit, CancellationToken cancellation) => throw new NotSupportedException();
        public IGameTransport Connect() => throw new NotSupportedException();
        public StartupEvents? Events(ServerRunPlan plan) => null;
    }
}
