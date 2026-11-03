using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing.Game;
using Xunit;

public sealed class FixtureBakeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fixture-bake-test-" + Guid.NewGuid().ToString("N"));

    public FixtureBakeTests() => Directory.CreateDirectory(root);
    public void Dispose() => DirectoryLinks.DeleteTree(root);

    [Fact]
    public void AConfirmedSaveAndCleanStopExportAnAtomicPinnedFixture()
    {
        var input = Source();
        string evidence = Evidence(saved: "2", clean: "true");
        string output = Path.Combine(root, "baked");
        string mod = Path.Combine(root, "MyMod.dll"), dependencyLock = Path.Combine(root, "dependencies.lock.json");
        File.WriteAllText(mod, "selected mod build");
        File.WriteAllText(dependencyLock, "pinned dependencies");

        FixtureBake.Export(evidence, output, input, FixtureBake.CaptureBuild([mod], dependencyLock));

        string world = Path.Combine(output, "worlds_local");
        Assert.Equal(input.Identity.Uid, WorldIdentity.Read(world).Uid);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "fixture-manifest.json"))).RootElement;
        Assert.Equal(input.Identity.Uid, manifest.GetProperty("world").GetProperty("Uid").GetInt64());
        Assert.Equal(2u, manifest.GetProperty("build").GetProperty("saveNumber").GetUInt32());
        Assert.Equal(2u, manifest.GetProperty("build").GetProperty("confirmedSaveNumber").GetUInt32());
        Assert.Equal("run-test", manifest.GetProperty("build").GetProperty("runId").GetString());
        Assert.Equal(Hash(mod), manifest.GetProperty("build").GetProperty("selectedModsSha256").GetProperty("MyMod.dll").GetString());
        Assert.Equal(Hash(dependencyLock), manifest.GetProperty("build").GetProperty("dependencyLockSha256").GetString());
        Assert.Equal(WorldFixture.Manifest(world).Count, manifest.GetProperty("filesSha256").EnumerateObject().Count());
        WorldFixture.Verify(input.Source, input.Files);
        Assert.DoesNotContain(Directory.EnumerateDirectories(root), path => Path.GetFileName(path).StartsWith(".baked.tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public void ACancelledExportDoesNotPublishAFixture()
    {
        var input = Source();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string output = Path.Combine(root, "baked");
        Assert.Throws<OperationCanceledException>(() =>
            FixtureBake.Export(Evidence("2", "true"), output, input, EmptyBuild(), cancelled.Token));
        Assert.False(Path.Exists(output));
    }

    [Theory]
    [InlineData(null, "true", "save")]
    [InlineData("2", "false", "stop cleanly")]
    public void WithoutAConfirmedSaveOrCleanStopNothingIsExported(string? saved, string clean, string error)
    {
        var input = Source();
        string evidence = Evidence(saved, clean);
        string output = Path.Combine(root, "baked");
        var failure = Assert.Throws<InvalidOperationException>(() =>
            FixtureBake.Export(evidence, output, input, EmptyBuild()));
        Assert.Contains(error, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.Exists(output));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AFailedRunOrCleanupCannotExport(bool passed, bool cleanup)
    {
        var input = Source();
        string evidence = Evidence("2", "true", passed, cleanup);
        string output = Path.Combine(root, "baked");
        Assert.Throws<InvalidOperationException>(() =>
            FixtureBake.Export(evidence, output, input, EmptyBuild()));
        Assert.False(Path.Exists(output));
    }

    [Fact]
    public void SameNamedSelectedModsCannotLoseOneHash()
    {
        var input = Source();
        string evidence = Evidence("2", "true");
        string first = Path.Combine(root, "a", "Mod.dll"), second = Path.Combine(root, "b", "Mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, "first build");
        File.WriteAllText(second, "second build");
        string lockFile = Path.Combine(root, "dependencies.lock.json");
        File.WriteAllText(lockFile, "pinned dependencies");
        string output = Path.Combine(root, "baked");
        Assert.Throws<InvalidDataException>(() => FixtureBake.CaptureBuild([first, second], lockFile));
        Assert.False(Path.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedBuildInputsDuringRunCannotBeMisrepresented(bool changeLock)
    {
        var input = Source();
        string evidence = Evidence("2", "true");
        string mod = Path.Combine(root, "MyMod.dll"), dependencyLock = Path.Combine(root, "dependencies.lock.json");
        File.WriteAllText(mod, "loaded build");
        File.WriteAllText(dependencyLock, "loaded lock");
        var build = FixtureBake.CaptureBuild([mod], dependencyLock);
        File.AppendAllText(changeLock ? dependencyLock : mod, "changed while game ran");

        var error = Assert.Throws<InvalidDataException>(() =>
            FixtureBake.Export(evidence, Path.Combine(root, "baked"), input, build));
        Assert.Contains(changeLock ? "dependency lock" : "selected mod", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.Exists(Path.Combine(root, "baked")));
    }

    [Fact]
    public void ChangedModAfterDependencyResolutionIsRefusedBeforeLaunch()
    {
        string mod = Path.Combine(root, "MyMod.dll"), dependencyLock = Path.Combine(root, "dependencies.lock.json");
        File.WriteAllText(mod, "resolved build");
        File.WriteAllText(dependencyLock, "resolved lock");
        var resolved = new NativeDependencyLock();
        resolved.Mods.Add(new NativeDependencyFile(mod, Hash(mod), "selected mod"));
        File.AppendAllText(mod, "changed before launch");
        Assert.Throws<InvalidDataException>(() => FixtureBake.CaptureBuild([mod], dependencyLock, resolved));
    }

    [Fact]
    public void ManifestNamesTheSaveActuallyFetchedAfterQuit()
    {
        var input = Source();
        string evidence = Evidence("2", "true", finalSave: 3);
        string output = Path.Combine(root, "baked");
        FixtureBake.Export(evidence, output, input, EmptyBuild());
        var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "fixture-manifest.json")))
            .RootElement.GetProperty("build");
        Assert.Equal(2u, build.GetProperty("confirmedSaveNumber").GetUInt32());
        Assert.Equal(3u, build.GetProperty("saveNumber").GetUInt32());
    }

    [Fact]
    public void BakedFixtureManifestIsCheckedBeforeItCanBeReused()
    {
        var input = Source();
        string evidence = Evidence("2", "true");
        string baked = Path.Combine(root, "baked");
        FixtureBake.Export(evidence, baked, input, EmptyBuild());
        var reused = FixtureBake.Prepare(baked, Path.Combine(root, "reuse"));
        Assert.Equal(input.Identity.Uid, reused.Identity.Uid);
        File.AppendAllText(Path.Combine(baked, "worlds_local", DefaultSmokeWorld.Name, "_main.2.fwl2"), "changed");
        Assert.Throws<InvalidDataException>(() => FixtureBake.Prepare(baked, Path.Combine(root, "tampered")));
    }

    [Fact]
    public void BakedFixtureManifestUsesPortableNamesAndAcceptsTheOtherPlatformsSeparators()
    {
        var input = Source();
        string baked = Path.Combine(root, "baked");
        FixtureBake.Export(Evidence("2", "true"), baked, input, EmptyBuild());
        string path = Path.Combine(baked, "fixture-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var files = manifest["filesSha256"]!.AsObject();
        Assert.All(files, entry => Assert.DoesNotContain('\\', entry.Key));
        var otherPlatform = new JsonObject();
        foreach (var entry in files)
            otherPlatform[entry.Key.Replace('/', '\\')] = entry.Value?.DeepClone();
        manifest["filesSha256"] = otherPlatform;
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.Equal(input.Identity.Uid, FixtureBake.Prepare(baked, Path.Combine(root, "reuse-other-os")).Identity.Uid);
    }

    [Fact]
    public void MacFetchedWorldLayoutCanBeExported()
    {
        var input = Source();
        string evidence = Evidence("2", "true", mac: true);
        string baked = Path.Combine(root, "baked");
        FixtureBake.Export(evidence, baked, input, EmptyBuild());
        Assert.Equal(input.Identity.Uid, WorldIdentity.Read(Path.Combine(baked, "worlds_local")).Uid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FetchedWorldWithAnotherNameOrUidCannotBeExported(bool changeUid)
    {
        var input = Source();
        string evidence = Evidence("2", "true");
        string world = Path.Combine(evidence, "host-world", "worlds_local", DefaultSmokeWorld.Name);
        byte[] other = OwnedRunPreflightTests.Metadata(changeUid ? input.Identity.Name : "AnotherWorld",
            changeUid ? input.Identity.Uid + 1 : input.Identity.Uid);
        File.WriteAllBytes(Path.Combine(world, "_main.2.fwl2"), other);
        Assert.Throws<InvalidDataException>(() => FixtureBake.Export(evidence, Path.Combine(root, "baked"), input, EmptyBuild()));
    }

    [Fact]
    public void FetchedSaveOlderThanConfirmedSaveIsRefused()
    {
        var input = Source();
        string evidence = Evidence("3", "true", finalSave: 2);
        Assert.Throws<InvalidDataException>(() =>
            FixtureBake.Export(evidence, Path.Combine(root, "baked"), input, EmptyBuild()));
        Assert.False(Path.Exists(Path.Combine(root, "baked")));
    }

    [Fact]
    public void SymlinkedOutputParentCannotReachProtectedSource()
    {
        string source = Path.Combine(root, "protected");
        Directory.CreateDirectory(source);
        string alias = Path.Combine(root, "alias");
        DirectoryLinks.Create(alias, source);
        Assert.Equal(source, new DirectoryInfo(alias).ResolveLinkTarget(true)?.FullName);
        Assert.Equal(ProtectedPaths.Resolve(source), ProtectedPaths.Resolve(alias));
        Assert.Throws<IOException>(() => FixtureBake.RefuseOutput(Path.Combine(alias, "baked"), source));
        Assert.False(Path.Exists(Path.Combine(source, "baked")));
    }

    [Fact]
    public void ExportRechecksProtectedGameInstallBeforeCreatingItsTemporaryCopy()
    {
        var input = Source();
        string evidence = Evidence("2", "true");
        string install = Path.Combine(root, "game-install");
        Directory.CreateDirectory(install);
        string alias = Path.Combine(root, "game-alias");
        DirectoryLinks.Create(alias, install);
        Assert.Equal(install, new DirectoryInfo(alias).ResolveLinkTarget(true)?.FullName);
        Assert.Throws<IOException>(() => FixtureBake.Export(evidence, Path.Combine(alias, "baked"), input,
            EmptyBuild(), install));
        Assert.Empty(Directory.EnumerateFileSystemEntries(install));
    }

    [Fact]
    public void ExistingOutputIsRefusedBeforeTheSourceIsCopied()
    {
        string output = Path.Combine(root, "baked");
        Directory.CreateDirectory(output);
        Assert.Throws<IOException>(() => FixtureBake.RefuseOutput(output, Path.Combine(root, "source")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void CaseVariantOfSourceIsRefusedOnMacAndWindows()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        string source = Path.Combine(root, "Fixtures");
        Directory.CreateDirectory(source);
        string output = Path.Combine(root, "fixtures", "out");

        Assert.Throws<IOException>(() => FixtureBake.RefuseOutput(output, source));
        Assert.False(Path.Exists(output));
    }

    [Fact]
    public void ChangedSourceAfterPreparationCannotBeExported()
    {
        var input = Source();
        string evidence = Evidence("2", "true");
        File.AppendAllText(Path.Combine(input.Source, DefaultSmokeWorld.Name, "_main.1.ok"), "changed");
        Assert.Throws<InvalidOperationException>(() =>
            FixtureBake.Export(evidence, Path.Combine(root, "baked"), input, EmptyBuild()));
        Assert.False(Path.Exists(Path.Combine(root, "baked")));
    }

    [Fact]
    public void CustomSourceIsCopiedAndItsUidIsUsed()
    {
        string sourceRoot = Path.Combine(root, "custom-source");
        DefaultSmokeWorld.PrepareServerSaveRoot(sourceRoot);
        var before = WorldFixture.Manifest(Path.Combine(sourceRoot, "worlds_local"));
        var input = FixtureBake.Prepare(sourceRoot, Path.Combine(root, "run"));
        Assert.Equal(DefaultSmokeWorld.Uid, input.Identity.UidText);
        Assert.Equal("custom-source", input.Origin);
        WorldFixture.Verify(Path.Combine(sourceRoot, "worlds_local"), before);
        WorldFixture.Verify(Path.Combine(root, "run", "world-source", "worlds_local"), before);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("uid")]
    [InlineData("name")]
    [InlineData("broken")]
    [InlineData("bytes")]
    public void AdmissionPinsTheSourceBeforeTheDisposableCopy(string change)
    {
        string source = FakeInstalls.World(Path.Combine(root, "source"));
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "selected host");
        var admission = FixtureBake.Admit(source, target);
        string world = Path.Combine(source, "Campaign");
        if (change == "broken") File.Delete(Path.Combine(world, "_main.1.db2"));
        else if (change == "bytes") File.AppendAllText(Path.Combine(world, "_main.1.db2"), "drift");
        else File.WriteAllBytes(Path.Combine(world, "_main.1.fwl2"), FakeInstalls.Metadata(change == "name" ? "Other" : "Campaign", change == "uid" ? 999 : 4242, version: change == "version" ? 42 : 41));
        string output = Path.Combine(root, "copy-refused");
        Assert.Throws<InvalidOperationException>(() => FixtureBake.Prepare(source, output, admission));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyRefusesDriftAfterAnEarlierFileWasCopiedAndCleansOnlyItsOutput(bool sourceDrift)
    {
        string source = FakeInstalls.World(Path.Combine(root, "source"));
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "selected host");
        var admission = FixtureBake.Admit(source, target);
        string output = Path.Combine(root, "copy"), unrelated = Path.Combine(root, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");
        string? firstSource = null, firstDestination = null;
        int copies = 0;
        Assert.Throws<InvalidOperationException>(() => FixtureBake.Prepare(source, output, admission, copyFile: (from, to) =>
        {
            File.Copy(from, to);
            if (++copies == 1) { firstSource = from; firstDestination = to; }
            else if (copies == 2)
            {
                string changed = sourceDrift ? firstSource! : firstDestination!;
                DateTime modified = File.GetLastWriteTimeUtc(changed);
                byte[] bytes = File.ReadAllBytes(changed);
                bytes[0] ^= 1;
                File.WriteAllBytes(changed, bytes);
                File.SetLastWriteTimeUtc(changed, modified);
            }
        }));
        Assert.True(copies > 1);
        Assert.False(Directory.Exists(Path.Combine(output, "world-source")));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.True(Directory.Exists(source));
        if (!sourceDrift) FixtureBake.VerifyAdmission(admission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructuralInspectionOfRawWorldsDoesNotReplaceAdmissionOrBakedPins(bool baked)
    {
        string source = Path.Combine(root, "fixture"), worlds = FakeInstalls.World(Path.Combine(source, "worlds_local"));
        if (baked) File.WriteAllText(Path.Combine(source, FixtureBake.ManifestFile), JsonSerializer.Serialize(new
        { schemaVersion = 1, world = new { Name = "Campaign", Uid = 4242L }, filesSha256 = WorldFixture.Manifest(worlds) }));
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "host");
        var admitted = FixtureBake.Admit(source, target);
        File.AppendAllText(Path.Combine(worlds, "Campaign", "_main.1.db2"), "payload drift, same world identity");
        if (baked) Assert.Throws<InvalidDataException>(() => FixtureBake.Check(source));
        else Assert.Equal(admitted.Identity, FixtureBake.Check(source));
        Assert.Throws<InvalidOperationException>(() => FixtureBake.VerifyAdmission(admitted));
    }

    [Fact]
    public void PreparationVerifiesDestinationAfterTheFinalSourceBoundary()
    {
        string source = FakeInstalls.World(Path.Combine(root, "source"));
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "selected host");
        var admission = FixtureBake.Admit(source, target);
        string output = Directory.CreateDirectory(Path.Combine(root, "copy")).FullName;
        string keep = Path.Combine(output, "unrelated.txt"); File.WriteAllText(keep, "keep");
        Assert.Throws<InvalidOperationException>(() => FixtureBake.Prepare(source, output, admission, afterSourceVerification: copy =>
        {
            string payload = Path.Combine(copy, "Campaign", FakeInstalls.ChunkFile);
            DateTime modified = File.GetLastWriteTimeUtc(payload);
            byte[] bytes = File.ReadAllBytes(payload); bytes[0] ^= 1;
            File.WriteAllBytes(payload, bytes); File.SetLastWriteTimeUtc(payload, modified);
        }));
        Assert.False(Directory.Exists(Path.Combine(output, "world-source")));
        Assert.Equal("keep", File.ReadAllText(keep));
        FixtureBake.VerifyAdmission(admission); // the source still matches the original pins
    }

    [Fact]
    public void PreparedAdmissionIsBoundToTheVerifiedOutputAndKeepsOriginalPins()
    {
        string source = FakeInstalls.World(Path.Combine(root, "source"));
        var target = GameAssemblyVersion.Read(new MemoryStream(FakeInstalls.GameAssembly()), "selected host");
        var admission = FixtureBake.Admit(source, target);
        var input = FixtureBake.Prepare(source, Path.Combine(root, "copy"), admission);
        var copied = Assert.IsType<WorldIdentity.Admission>(input.CopyAdmission);
        Assert.Equal(Path.Combine(root, "copy", "world-source", "worlds_local"), copied.Source);
        copied.Verify();
        File.AppendAllText(Path.Combine(copied.Source, "Campaign", "_main.1.db2"), "copy-only drift");
        Assert.Throws<InvalidOperationException>(() => copied.Verify());
        FixtureBake.VerifyAdmission(admission);
    }

    private FixtureBake.Input Source() => FixtureBake.Prepare(null, Path.Combine(root, "run"));

    private FixtureBake.BuildInputs EmptyBuild()
    {
        string dependencyLock = Path.Combine(root, "dependencies.lock.json");
        File.WriteAllText(dependencyLock, "pinned dependencies");
        return FixtureBake.CaptureBuild([], dependencyLock);
    }

    private string Evidence(string? saved, string clean, bool passed = true, bool cleanup = true, int finalSave = 2, bool mac = false)
    {
        string evidence = Path.Combine(root, "evidence");
        string host = mac ? Path.Combine(evidence, "mac-world-input", "host-world") : Path.Combine(evidence, "host-world");
        DefaultSmokeWorld.PrepareServerSaveRoot(host);
        string world = Path.Combine(host, "worlds_local", DefaultSmokeWorld.Name);
        // The game saved: its new set replaces the old one, as the game deletes the previous set once the new one is complete.
        foreach (string part in new[] { "fwl2", "db2", "chunks", "ok" })
            File.Move(Path.Combine(world, "_main.1." + part), Path.Combine(world, $"_main.{finalSave}.{part}"));
        var provenance = new Dictionary<string, string> { ["runId"] = "run-test", ["serverStopsClean"] = clean };
        if (saved != null) provenance["bakeSaveNumber"] = saved;
        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { Passed = passed, CleanupVerified = cleanup, Provenance = provenance }));
        return evidence;
    }

    private static string Hash(string file)
    {
        using var input = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
