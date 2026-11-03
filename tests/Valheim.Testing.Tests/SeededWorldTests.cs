using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

// Controlled game replies and temporary save trees only; none of these tests proves native game creation.
public sealed class SeededWorldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-seeded-world-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private const string Name = "Seeded-Test", Seed = "AbCdEf1234";
    private static readonly SeededWorldRequest Request = new(Name, Seed);
    private string Worlds => Path.Combine(_root, "data", "worlds_local");
    private string Output => Path.Combine(_root, "output");
    private static GameBuild Build => GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "controlled hosting build");
    private ClientRunPlan Plan() => new()
    {
        Mode = "owned", Install = _root, Character = "Tester", PreparedBuild = Build,
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32) },
        HostWorld = new() { Create = Request },
    };
    private readonly List<JournalEntry> _journal = [];
    private HostedWorldOnHost.Site Site(Func<ClientSession, ClientRunPlan, CancellationToken, Task>? hold = null) =>
        new(new LocalGameHost("controlled local", HostShell.ThisMachine), "controlled local", Worlds,
            Path.Combine(_root, "runs", "run-1", "host-world-stage"), Path.Combine(_root, "runs", "run-1", "host-world"),
            (entry, _) => { _journal.Add(entry); return Task.CompletedTask; }, _ => Task.CompletedTask)
        {
            SteamUserDataDirectory = Path.Combine(_root, "steam", "userdata"),
            // The site tests isolate creation/proof/collection. Canonical process admission has a refusal test below;
            // production TargetedRegression always supplies the real machine-lock + owned-menu hold.
            MenuHold = hold ?? ((_, _, _) => Task.CompletedTask),
        };
    private static string Success => $"OK: WORLD_CREATED name={Name} seedName={Seed} seed=1234 uid=8989 worldGenVersion=2";
    private ScriptedTransport Transport(Action? create = null, string? reply = null) => new ScriptedTransport()
        .On($"cli_create_world {Name} {Seed}", _ => { create?.Invoke(); return ScriptedTransport.Ok(reply ?? Success); })
        .On("cli_quit", _ => ScriptedTransport.Ok("quitting"));
    private ClientSession Open(ClientRunPlan plan, ScriptedTransport transport)
    {
        Directory.CreateDirectory(Output);
        return ClientSession.Launch(plan, Output, () => new FakeOwnedProcess(77), () => transport, (_, _) => Task.CompletedTask);
    }
    private void Created(long uid = 8989, string seed = Seed, bool malformed = false)
    {
        string world = Path.Combine(Worlds, Name);
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "_main.0.fwl2"), malformed ? [1, 2] : FakeInstalls.Metadata(Name, uid, seed, needsDb: false));
    }
    private Dictionary<string, string> Fields() => _journal.SelectMany(entry => entry.Fields)
        .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value);

    [Theory]
    [InlineData("", "Abc")]
    [InlineData("ab", "Abc")]
    [InlineData("CON", "Abc")]
    [InlineData("NUL", "Abc")]
    [InlineData("Seeded World", "Abc")]
    [InlineData("../Seeded", "Abc")]
    [InlineData("Seeded", "a b")]
    [InlineData("Seeded", "")]
    [InlineData("Seeded", "--overwrite")]
    [InlineData("Base_20261021-120000", "Abc")]
    public void UnsupportedCommandTokensAreRefusedBeforeLaunch(string name, string seed) =>
        Assert.Throws<ArgumentException>(() => new SeededWorldRequest(name, seed));

    [Fact] public void NamesAreFreshAndSeedCaseIsNeverNormalized()
    {
        var first = SeededWorldRequest.FromSeed(Seed, null);
        var second = SeededWorldRequest.FromSeed(Seed, null);
        Assert.StartsWith("VT-" + Seed + "-", first.Name);
        Assert.NotEqual(first.Name, second.Name);
        Assert.Equal(Seed, first.Seed);
        Assert.Equal(Request, new SeededWorldRequest(Name, Seed));
    }

    [Fact] public void FixtureAndPendingCreationAreMutuallyExclusiveAndPendingHasNoIdentity()
    {
        var fixture = new RegressionFixture { Create = Request };
        fixture.Validate(); fixture.WorldUid = "0";
        Assert.Throws<ArgumentException>(fixture.Validate);
        var plan = new HostWorldPlan { Create = Request };
        plan.Validate();
        Assert.Contains("unresolved", Assert.Throws<InvalidOperationException>(() => plan.Preflight()).Message);
        plan.World.Source = _root;
        Assert.Throws<ArgumentException>(plan.Validate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectSeededInputsRefuseComparisonsRatherThanGeneratingUnrelatedWorlds(bool repeatability)
    {
        var inputs = new RegressionInputs
        {
            Name = "seeded", Client = new() { Character = "Tester" }, Fixture = new() { Create = Request },
            Cli = new() { Core = new() { File = Path.Combine(_root, "valheimCLI.dll") }, Manifest = Path.Combine(_root, "cli.json") },
            Mod = new() { InstallAs = "Mod.dll", Repeatability = repeatability, Arms = new()
            {
                ["first"] = new() { File = Path.Combine(_root, "first.dll"), Sha256 = new string('a', 64) },
                ["second"] = new() { File = Path.Combine(_root, "second.dll"), Sha256 = new string(repeatability ? 'a' : 'b', 64) },
            } },
        };
        Assert.Contains("fixture.create", Assert.Throws<ArgumentException>(inputs.Validate).Message);
        inputs.Mod.Arms.Remove("second"); inputs.Mod.Repeatability = false;
        inputs.Validate(); // Still a mod run; creation does not imply a CLI-only control.
        string path = Path.Combine(_root, "regression.json"); inputs.Write(path);
        var reread = RegressionInputs.Read(path);
        Assert.Equal(Request, reread.Fixture.Create);
        Assert.Equal("", reread.Fixture.Root);
        Assert.Equal("", reread.Fixture.WorldUid);
        Assert.False(reread.NoMod);
    }

    [Theory]
    [InlineData("Unknown command: cli_create_world")]
    [InlineData("OK: WORLD_CREATED name=Other seedName=AbCdEf1234 seed=1234 uid=8989 worldGenVersion=2")]
    [InlineData("OK: WORLD_CREATED name=Seeded-Test seedName=abcdef1234 seed=1234 uid=8989 worldGenVersion=2")]
    [InlineData("OK: WORLD_CREATED name=Seeded-Test seedName=AbCdEf1234 seed=1234 uid=none worldGenVersion=2")]
    [InlineData("OK: WORLD_CREATED name=Seeded-Test seedName=AbCdEf1234 seed=1234 uid=8989 worldGenVersion=9999999999999")]
    public void AnUnprovenReplyIsNeverCreationProof(string line) => Assert.ThrowsAny<Exception>(() =>
        HostedWorldOnHost.ReadCreationReply(new GameReply("cli_create_world " + Name + " " + Seed, ScriptedTransport.Ok(line)), Request));

    [Fact] public void GeneratorVersionAndSaveFormatVersionAreSeparateWithoutChangingWorldIdentityEquality()
    {
        byte[] bytes = FakeInstalls.Metadata(Name, 8989, Seed, needsDb: false);
        var identity = WorldIdentity.Parse(bytes, Name + "/_main.0.fwl2");
        Assert.Equal(41, identity.WorldVersion);
        Assert.Equal(2, WorldIdentity.ReadGenerationVersion(bytes, identity.File));
        var (name, seedName, seed, uid, version, file) = identity;
        Assert.Equal(identity, new WorldIdentity(name, seedName, seed, uid, version, file));
        Assert.Equal(2, HostedWorldOnHost.ReadCreationReply(new GameReply("create", ScriptedTransport.Ok(Success)), Request).GenerationVersion);
    }

    [ClientHostFact] public void AProvenCreationBindsOnceAndCollectionPreservesPersonalSiblings()
    {
        Directory.CreateDirectory(Worlds);
        File.WriteAllText(Path.Combine(Worlds, "MyWorld.fwl"), "personal");
        var plan = Plan(); var site = Site();
        site.ReserveCreation(plan.HostWorld!, Output, default);
        Assert.False(Directory.Exists(Path.Combine(Worlds, Name)));
        Assert.DoesNotContain("worldUid", Fields().Keys);
        var transport = Transport(() => Created());
        using var client = Open(plan, transport);
        var world = site.CreateWorld(plan, client, Output, default);
        Assert.Null(plan.HostWorld!.Create);
        Assert.Equal("8989", plan.HostWorld.WorldUid);
        Assert.Equal(Request, plan.HostWorld.RequestedCreation);
        Assert.Equal(Seed, WorldIdentity.Read(world.FixtureCopy).SeedName);
        Assert.Throws<InvalidOperationException>(() => site.CreateWorld(plan, client, Output, default));
        Assert.Equal(1, transport.Count("cli_create_world"));
        client.Dispose(); // Collection occurs only after the existing client owner settles.
        world.Collect();
        Assert.False(Directory.Exists(Path.Combine(Worlds, Name)));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(Worlds, "MyWorld.fwl")));
        Assert.True(File.Exists(Path.Combine(world.CollectedTo!, Name, "_main.0.fwl2")));
        Assert.True(File.Exists(Path.Combine(Output, "world-created.json")));
        Assert.Equal("completed", Fields()["creationState"]);
    }

    [ClientHostFact] public void TheOpenedLifecyclePublishesActualUidBeforeAnyRoundAndNeverCreatesAgain()
    {
        using var install = PreflightInstall.Create();
        using var data = new FakeClientDataDirectory(Path.Combine(_root, "data"));
        var plan = Plan(); plan.Install = install.Root;
        plan.Architecture = "x64";
        plan.Port = 5556;
        plan.Pins["valheimCLI.valheimCLI"] = install.CliMd5;
        plan.RunManifestState = new RunManifest("seed", "control", null, null, null, false, [], [], [], new InstallPins(),
            Name, null, 0, [], [], "controlled") { Create = Request };
        Directory.CreateDirectory(Output);
        plan.RunManifestState.Write(Path.Combine(Output, "run-manifest.json"));
        using (var pending = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "run-manifest.json"))))
            Assert.Equal(JsonValueKind.Null, pending.RootElement.GetProperty("worldUid").ValueKind);
        var site = Site(); var report = new ScenarioReport("seed lifecycle");
        var lifecycle = new HostedWorldLifecycle(plan, Output, default, localSite: _ => site);
        lifecycle.Prepare(report);
        Assert.Null(lifecycle.World);
        var transport = Transport(() => Created()); using var client = Open(plan, transport);
        lifecycle.Opened(client, report);
        Assert.Equal("8989", plan.HostWorld!.WorldUid);
        Assert.Equal("8989", plan.RunManifestState.WorldUid);
        Assert.Equal(1, plan.RunManifestState.FixtureFiles);
        using (var resolved = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "run-manifest.json"))))
        {
            Assert.Equal("8989", resolved.RootElement.GetProperty("worldUid").GetString());
            Assert.Equal(Seed, resolved.RootElement.GetProperty("create").GetProperty("seed").GetString());
        }
        lifecycle.Opened(client, report);
        Assert.Equal(1, transport.Count("cli_create_world"));
        client.Dispose(); lifecycle.ReleaseWorld(report, released: true);
        Assert.NotNull(lifecycle.World!.CollectedTo);
    }

    [ClientHostTheory]
    [InlineData("lost-reply")]
    [InlineData("uid")]
    [InlineData("seed-case")]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("extra-file")]
    public void UnknownOrForeignCreationRemainsPendingWithNoRetryOrCollection(string fault)
    {
        var plan = Plan(); var site = Site();
        site.ReserveCreation(plan.HostWorld!, Output, default);
        var transport = Transport(() =>
        {
            if (fault != "missing") Created(fault == "uid" ? 9999 : 8989, fault == "seed-case" ? Seed.ToLowerInvariant() : Seed, fault == "malformed");
            if (fault == "extra-file") File.WriteAllText(Path.Combine(Worlds, Name, "personal.txt"), "foreign");
        }, fault == "lost-reply" ? "unconfirmed" : null);
        using var client = Open(plan, transport);
        Assert.ThrowsAny<Exception>(() => site.CreateWorld(plan, client, Output, default));
        Assert.Equal(Request, plan.HostWorld!.Create);
        Assert.Equal("", plan.HostWorld.WorldUid);
        Assert.Throws<InvalidOperationException>(() => site.CreateWorld(plan, client, Output, default));
        Assert.Equal(1, transport.Count("cli_create_world"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
        Assert.Throws<InvalidDataException>(() => HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default).GetAwaiter().GetResult());
        Assert.DoesNotContain(_journal, entry => entry.Kind == JournalEntry.CopyRetired);
        Assert.Equal(fault != "missing", Directory.Exists(Path.Combine(Worlds, Name)));
    }

    [ClientHostFact] public async Task APreCommandFailureRetiresOnlyItsAbsentCreationIntent()
    {
        var plan = Plan();
        var site = Site((_, _, _) => Task.FromException(new IOException("controlled failure before creation")));
        site.ReserveCreation(plan.HostWorld!, Output, default);
        var transport = Transport(); using var client = Open(plan, transport);
        Assert.Equal("controlled failure before creation", Assert.Throws<IOException>(() => site.CreateWorld(plan, client, Output, default)).Message);
        Assert.Equal(0, transport.Count("cli_create_world"));
        Assert.Equal("intended", Fields()["creationState"]);
        Assert.Empty(await HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.False(Directory.Exists(Worlds));
        Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task IntendedCreationAbsenceIsReadOnlyForMissingAndExistingWorldFolders(bool existing)
    {
        var site = Site(); site.ReserveCreation(Plan().HostWorld!, Output, default);
        if (existing) { Directory.CreateDirectory(Worlds); File.WriteAllText(Path.Combine(Worlds, "Personal.fwl"), "personal"); }
        Assert.Empty(await HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.Equal(existing, Directory.Exists(Worlds));
        if (existing) { Assert.Equal(["Personal.fwl"], Directory.GetFileSystemEntries(Worlds).Select(Path.GetFileName)); Assert.Equal("personal", File.ReadAllText(Path.Combine(Worlds, "Personal.fwl"))); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
    }

    [Theory] [InlineData("Seeded-Test.fwl")] [InlineData("SEEDED-TEST.fwl")]
    [InlineData("Seeded-Test_backup_auto-1.fwl")] [InlineData("Seeded-Test")]
    public async Task IntendedCreationNeverRetiresOverAForeignNamedEntry(string leaf)
    {
        var site = Site(); site.ReserveCreation(Plan().HostWorld!, Output, default);
        Directory.CreateDirectory(Worlds);
        string entry = Path.Combine(Worlds, leaf), file = leaf == Name ? Path.Combine(Directory.CreateDirectory(entry).FullName, "personal.txt") : entry;
        File.WriteAllText(file, "foreign bytes");
        await Assert.ThrowsAsync<HostOperationException>(() => HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.Equal("foreign bytes", File.ReadAllText(file));
        Assert.Equal([leaf], Directory.GetFileSystemEntries(Worlds).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
    }

    [UnixFact] public async Task IntendedCreationRefusesDanglingEntriesAndLinkedWorldParents()
    {
        var site = Site(); site.ReserveCreation(Plan().HostWorld!, Output, default);
        Directory.CreateDirectory(Worlds);
        string link = Path.Combine(Worlds, Name + ".fwl"), missing = Path.Combine(_root, "missing-world");
        File.CreateSymbolicLink(link, missing);
        await Assert.ThrowsAsync<HostOperationException>(() => HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.Equal(missing, new FileInfo(link).LinkTarget); Assert.False(File.Exists(missing));
        File.Delete(link); Directory.Delete(Worlds);
        string foreign = Directory.CreateDirectory(Path.Combine(_root, "foreign-worlds")).FullName;
        File.WriteAllText(Path.Combine(foreign, "personal.txt"), "personal");
        Directory.CreateSymbolicLink(Worlds, foreign);
        await Assert.ThrowsAsync<HostOperationException>(() => HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.Equal(foreign, new DirectoryInfo(Worlds).LinkTarget);
        Assert.Equal("personal", File.ReadAllText(Path.Combine(foreign, "personal.txt")));
        Assert.Equal(["personal.txt"], Directory.GetFileSystemEntries(foreign).Select(Path.GetFileName));
        Directory.Delete(Worlds); Directory.Delete(Path.GetDirectoryName(Worlds)!);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(Worlds)!, foreign);
        await Assert.ThrowsAsync<HostOperationException>(() => HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.Equal(foreign, new DirectoryInfo(Path.GetDirectoryName(Worlds)!).LinkTarget);
        Assert.Equal(["personal.txt"], Directory.GetFileSystemEntries(foreign).Select(Path.GetFileName));
    }

    [UnixFact] public async Task IntendedCreationRefusesAnUnreadableWorldFolder()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("UnixFact excludes Windows; this test uses Unix permissions.");
        var site = Site(); site.ReserveCreation(Plan().HostWorld!, Output, default);
        Directory.CreateDirectory(Worlds); var mode = File.GetUnixFileMode(Worlds);
        try
        {
            File.SetUnixFileMode(Worlds, UnixFileMode.None);
            await Assert.ThrowsAsync<HostOperationException>(() => HostedWorldOnHost.RecoverAsync(site.Host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        }
        finally { File.SetUnixFileMode(Worlds, mode); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Worlds));
    }

    [Theory]
    [InlineData("", 0, false)]
    [InlineData("VT-WORLD-ABSENCE", 0, false)]
    [InlineData("VT-WORLD-ABSENCE done\nunknown\n", 0, false)]
    [InlineData("VT-WORLD-ABSENCE done\nVT-WORLD-ABSENCE done\n", 0, false)]
    [InlineData("VT-WORLD-ABSENCE done\n", 5, false)]
    [InlineData("VT-WORLD-ABSENCE done\n", 0, true)]
    public async Task IntendedCreationRequiresASuccessfulCompleteAbsenceReceipt(string stdout, int exit, bool unknown)
    {
        var site = Site(); site.ReserveCreation(Plan().HostWorld!, Output, default);
        var host = new ForwardingWorldHost(site.Host)
        {
            InsteadOfRun = (script, _) => script == HostedWorldOnHost.BashCreationAbsence || script == HostedWorldOnHost.WindowsCreationAbsence
                ? new HostResult(unknown ? HostOutcome.TransportFailed : HostOutcome.Exited, exit, stdout, "controlled observation", TimeSpan.Zero, false) : null,
        };
        await Assert.ThrowsAsync<HostOperationException>(() => HostedWorldOnHost.RecoverAsync(host, Fields(), Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.False(Directory.Exists(Worlds)); Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
    }

    [Theory]
    [InlineData("placementState", "attempted")] [InlineData("creationState", "unknown")]
    [InlineData("worldUid", "")] [InlineData("verified", "false")]
    [InlineData("creationProof", "")] [InlineData("creationProofSha256", "")]
    [InlineData("absenceVerified", "false")] [InlineData("stage", "unexpected")]
    [InlineData("runtime", "unexpected")] [InlineData("holds", "runtime")] [InlineData("transferredEntries", "[]")]
    [InlineData("seed", "bad seed")] [InlineData("parent", "worlds_local")]
    [InlineData("keepIn", "/somewhere/another-run/host-world")]
    public async Task InconsistentCreationIntentNeverBecomesAnAbsenceClaim(string field, string value)
    {
        var site = Site(); site.ReserveCreation(Plan().HostWorld!, Output, default);
        var fields = Fields(); fields[field] = value; bool observed = false;
        var host = new ForwardingWorldHost(site.Host) { BeforeRun = (_, _) => observed = true };
        var error = await Record.ExceptionAsync(() => HostedWorldOnHost.RecoverAsync(host, fields, Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.True(error is ArgumentException or InvalidDataException, error?.ToString() ?? "invalid intent was accepted");
        Assert.False(observed); Assert.False(Directory.Exists(Worlds));
    }

    [Fact] public async Task AnAttemptedCreationStaysPendingEvenWhenNoEntryExists()
    {
        var site = Site(); site.ReserveCreation(Plan().HostWorld!, Output, default);
        var fields = Fields(); fields["creationState"] = fields["placementState"] = "attempted";
        await Assert.ThrowsAsync<InvalidDataException>(() => HostedWorldOnHost.RecoverAsync(site.Host, fields, Path.Combine(Worlds, Name), "run-1", TimeSpan.FromSeconds(30), default));
        Assert.False(Directory.Exists(Worlds));
    }

    [Theory]
    [InlineData("Seeded-Test.fwl", false)]
    [InlineData("SEEDED-TEST_backup_auto-1.fwl", true)]
    public void CreationReservationUsesTheRealLocalAndCloudGuardWithoutChangingExistingTrees(string leaf, bool cloud)
    {
        var site = Site(); var plan = Plan();
        string root = cloud ? Path.Combine(_root, "data", "worlds") : Worlds;
        Directory.CreateDirectory(root);
        string old = Path.Combine(root, leaf); File.WriteAllText(old, "personal");
        Assert.Throws<InvalidOperationException>(() => site.ReserveCreation(plan.HostWorld!, Output, default));
        Assert.Equal("personal", File.ReadAllText(old));
        Assert.False(Directory.Exists(site.KeepIn));
        Assert.Empty(_journal);
    }

    [ClientHostFact] public void ALateCollisionRefusesBeforeTheOneCreateCommand()
    {
        var plan = Plan(); var site = Site(); site.ReserveCreation(plan.HostWorld!, Output, default);
        Directory.CreateDirectory(Worlds); File.WriteAllText(Path.Combine(Worlds, Name + ".fwl"), "late personal");
        var transport = Transport(); using var client = Open(plan, transport);
        Assert.Throws<InvalidOperationException>(() => site.CreateWorld(plan, client, Output, default));
        Assert.Equal(0, transport.Count("cli_create_world"));
        Assert.Equal("late personal", File.ReadAllText(Path.Combine(Worlds, Name + ".fwl")));
    }

    [ClientHostFact] public void AForeignReplacementCannotBeCollectedUsingAnOldCreationReceipt()
    {
        var plan = Plan(); var site = Site(); site.ReserveCreation(plan.HostWorld!, Output, default);
        var transport = Transport(() => Created()); using var client = Open(plan, transport);
        var world = site.CreateWorld(plan, client, Output, default);
        client.Dispose();
        Created(uid: 9999);
        Assert.Throws<InvalidDataException>(world.Collect);
        Assert.Equal(9999, WorldIdentity.Parse(File.ReadAllBytes(Path.Combine(Worlds, Name, "_main.0.fwl2")), "foreign").Uid);
        Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
        Assert.DoesNotContain(_journal, entry => entry.Kind == JournalEntry.CopyRetired);
    }

    [ClientHostFact] public void AnUnidentifiedOwnedProcessCannotAcquireThePostOpenHold()
    {
        var plan = Plan(); var transport = Transport(); using var client = Open(plan, transport);
        Assert.Throws<InvalidOperationException>(() => client.RequireOwnedMenu(plan));
        Assert.Equal(0, transport.Count("cli_create_world"));
    }

    [Theory]
    [InlineData("VALHEIMCLI_START_WORLD_FILE")]
    [InlineData("VALHEIMCLI_START_WORLD_CLAIMED")]
    public void StartupInstructionsAreRefusedByTheLaunchOwnerBeforeItsStartDelegate(string variable)
    {
        var plan = Plan(); plan.Environment[variable] = "competing.json";
        bool started = false;
        Directory.CreateDirectory(Output);
        Assert.Throws<ArgumentException>(() => ClientSession.Launch(plan, Output, () => { started = true; return new FakeOwnedProcess(77); },
            () => Transport(), (_, _) => Task.CompletedTask));
        Assert.False(started);
    }
}
