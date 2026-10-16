using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;

/// <summary>Directory links for tests: a symlink, or on Windows a junction, which needs no privilege.</summary>
internal static class DirectoryLinks
{
    public static void Create(string alias, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(alias, target); return; }
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{alias}\" \"{target}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var error = mklink.StandardError.ReadToEndAsync();
        string output = mklink.StandardOutput.ReadToEnd() + error.GetAwaiter().GetResult();
        mklink.WaitForExit();
        if (mklink.ExitCode != 0) throw new IOException("Could not create junction " + alias + ": " + output);
    }

    /// <summary>Removes the link itself, never its target.</summary>
    public static void Remove(string alias)
    {
        if (OperatingSystem.IsWindows()) Directory.Delete(alias); else File.Delete(alias);
    }

    /// <summary>
    /// Deletes a test folder that holds links: each link goes first, so a junction whose target is already deleted
    /// (or a link loop) cannot fail the recursive delete.
    /// </summary>
    public static void DeleteTree(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (new FileInfo(entry).LinkTarget != null) Remove(entry);
            else if (Directory.Exists(entry)) DeleteTree(entry);
        }
        Directory.Delete(root, recursive: true);
    }
}

// The one owner of "is this path inside that protected folder" resolves links, so every protected-output check refuses an alias.
public sealed class ProtectedPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("protected-paths-").FullName;
    public void Dispose() => DirectoryLinks.DeleteTree(_root);

    private string Folder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Alias(string name, string target)
    {
        string alias = Path.Combine(_root, name);
        DirectoryLinks.Create(alias, target);
        return alias;
    }

    [Fact]
    public void ARootedWriteAllowsSystemAliasesAboveItsBoundaryButRefusesLinksWithinIt()
    {
        string real = Folder("real"), alias = Alias("system-alias", real);
        string boundary = Path.Combine(alias, "trusted"); Directory.CreateDirectory(boundary);
        ProtectedPaths.RequireUnlinkedPath(boundary, Path.Combine(boundary, "new", "recent"));
        string escaped = Path.Combine(boundary, "escape"); DirectoryLinks.Create(escaped, Folder("external"));
        Assert.Throws<IOException>(() => ProtectedPaths.RequireUnlinkedPath(boundary, Path.Combine(escaped, "recent")));
        Assert.Throws<IOException>(() => ProtectedPaths.RequireUnlinkedPath(boundary, Path.Combine(_root, "foreign", "recent")));
        string linkedRoot = Alias("linked-root", Path.Combine(real, "trusted"));
        Assert.Throws<IOException>(() => ProtectedPaths.RequireUnlinkedPath(linkedRoot, Path.Combine(linkedRoot, "recent")));
    }

    [Fact]
    public void AnAliasOfTheRootOrOfTheOutputsParentIsTheSameFolder()
    {
        string real = Folder("protected");
        string alias = Alias("alias", real);
        Assert.True(ProtectedPaths.Contains(real, Path.Combine(alias, "run")));
        Assert.True(ProtectedPaths.Contains(alias, Path.Combine(real, "run")));
        Assert.True(ProtectedPaths.Contains(alias, real));
        Assert.True(ProtectedPaths.Contains(real, alias));
        Assert.False(ProtectedPaths.Contains(real, Folder("sibling")));
        Assert.False(ProtectedPaths.Contains(Path.Combine(real, "run"), real));
    }

    [Fact]
    public void ADestinationThatDoesNotExistYetResolvesUnderItsLinkedParent()
    {
        string real = Folder("protected");
        string alias = Alias("alias", real);
        Assert.Equal(Path.Combine(ProtectedPaths.Resolve(real), "new", "deep"), ProtectedPaths.Resolve(Path.Combine(alias, "new", "deep")));
        Assert.False(Path.Exists(Path.Combine(real, "new")));
        Assert.True(ProtectedPaths.Contains(real, Path.Combine(alias, "new", "deep")));
    }

    [Fact]
    public void WindowsSpellingsOfTheSameFolderCompareEqual()
    {
        if (!OperatingSystem.IsWindows()) return;
        string real = Folder("protected");
        Assert.True(ProtectedPaths.Contains(real, real.Replace('\\', '/') + "/run"));
        Assert.True(ProtectedPaths.Contains(real.Replace('\\', '/'), Path.Combine(real, "run")));
        Assert.True(ProtectedPaths.Contains(real, real + ". \\run"));
    }

    [Fact]
    public void ADotDotAfterALinkGoesUpFromTheLinksTarget()
    {
        string real = Folder("protected");
        string inner = Path.Combine(real, "inner");
        Directory.CreateDirectory(inner);
        string alias = Alias("alias", inner);
        // Lexically alias/../run is a sibling of the alias; physically it is inside the protected folder.
        string output = Path.Combine(alias, "..", "run");
        Assert.Equal(Path.Combine(ProtectedPaths.Resolve(real), "run"), ProtectedPaths.Resolve(output));
        Assert.True(ProtectedPaths.Contains(real, output));
        Assert.False(ProtectedPaths.Contains(real, Path.Combine(_root, "run")));
    }

    [Fact]
    public void ALinkThatPointsThroughAnotherLinkIsFollowedToTheEnd()
    {
        string real = Folder("protected");
        string inner = Alias("inner", real);
        string outer = Alias("outer", inner);
        Assert.Equal(ProtectedPaths.Resolve(real), ProtectedPaths.Resolve(outer));
        Assert.NotEqual(outer, ProtectedPaths.Resolve(outer));
        Assert.True(ProtectedPaths.Contains(real, Path.Combine(outer, "run")));
    }

    [UnixFact] // A Windows junction needs an existing target.
    public void ALinkToAFolderThatIsNotThereYetStillCountsAsThatFolder()
    {
        string later = Path.Combine(_root, "later");
        string alias = Alias("dangling", later);
        Assert.Equal(Path.Combine(ProtectedPaths.Resolve(_root), "later", "run"), ProtectedPaths.Resolve(Path.Combine(alias, "run")));
        Assert.True(ProtectedPaths.Contains(later, alias));
    }

    [Fact]
    public void ALinkLoopIsRefusedWithAClearError()
    {
        string a = Folder("a"), b = Folder("b");
        Directory.Delete(b);
        DirectoryLinks.Create(b, a);
        Directory.Delete(a);
        DirectoryLinks.Create(a, b);
        try
        {
            string error = Assert.Throws<IOException>(() => ProtectedPaths.Resolve(Path.Combine(a, "run"))).Message;
            Assert.Contains("loop", error);
            Assert.Throws<IOException>(() => ProtectedPaths.Contains(Folder("elsewhere"), Path.Combine(a, "run")));
            Assert.Throws<IOException>(() => ProtectedPaths.Contains(a, Folder("elsewhere")));
        }
        finally
        {
            DirectoryLinks.Remove(a);
            DirectoryLinks.Remove(b);
        }
    }

    [Fact]
    public void ACaseVariantIsStillTheSameFolderOnMacAndWindows()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        string real = Folder("Protected");
        Assert.True(ProtectedPaths.Contains(real.ToUpperInvariant(), Path.Combine(real, "run")));
    }

    [Fact]
    public void RunOutputThroughAnAliasIsRefused()
    {
        string install = Folder("game"), steam = Folder("steam");
        string alias = Alias("game-alias", install);
        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseInside(Path.Combine(alias, "run"), install, steam));
        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseInside(Path.Combine(install, "run"), alias, steam));
        SmokeOutput.RefuseInside(Path.Combine(Folder("runs"), "new-run"), alias, steam);

        var chosen = new EnvironmentRecipe { Install = alias };
        string cli = Folder("cli"), loader = Folder("loader");
        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseResolved(
            Path.Combine(install, "run"), cli, new EnvironmentInventory(), [chosen], loader));
        Assert.Throws<ArgumentException>(() => SmokeOutput.RefuseResolved(
            Path.Combine(Alias("loader-alias", loader), "run"), cli, new EnvironmentInventory(), [chosen], loader));
    }

    [Fact]
    public async Task TestProjectBuildOutputThroughAnAliasOfItsProjectIsRefused()
    {
        string project = Path.Combine(Folder("scenario"), "Scenario.csproj");
        File.WriteAllText(project, "<Project />");
        string alias = Alias("scenario-alias", Path.GetDirectoryName(project)!);
        string real = Path.GetDirectoryName(project)!;
        await Assert.ThrowsAsync<ArgumentException>(() => OneShotScenario.BuildAsync(project, Path.Combine(alias, "built"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => OneShotScenario.BuildAsync(
            Path.Combine(alias, "Scenario.csproj"), Path.Combine(real, "built"), CancellationToken.None));
        Assert.False(Path.Exists(Path.Combine(real, "built")));
    }

    [Fact]
    public void ModBuildOutputThroughAnAliasOfTheGameOrTheProjectIsRefused()
    {
        using var game = ClientLaunchTests.Install.Windows();
        game.Add("valheim_Data/Managed/assembly_valheim.dll");
        game.Add("BepInEx/core/BepInEx.dll");
        game.Add("BepInEx/core/0Harmony.dll");
        string mod = Folder("mod");
        string project = Path.Combine(mod, "Sample.csproj");
        File.WriteAllText(project, "<Project />");
        string gameAlias = Alias("game-alias", game.Root), modAlias = Alias("mod-alias", mod);
        var none = new Dictionary<string, string>();
        Assert.Throws<ArgumentException>(() => ModBuild.Plan(project, game.Root, none, Path.Combine(gameAlias, "BepInEx", "plugins", "out")));
        Assert.Throws<ArgumentException>(() => ModBuild.Plan(project, gameAlias, none, Path.Combine(game.Root, "out")));
        Assert.Throws<ArgumentException>(() => ModBuild.Plan(project, game.Root, none, Path.Combine(modAlias, "out")));
        Assert.Throws<ArgumentException>(() => ModBuild.Plan(Path.Combine(modAlias, "Sample.csproj"), game.Root, none, Path.Combine(mod, "out")));
        Assert.False(Path.Exists(Path.Combine(game.Root, "out")) || Path.Exists(Path.Combine(mod, "out")));
        ModBuild.Plan(project, gameAlias, none, Path.Combine(Folder("outside"), "out"));
    }

    [Fact]
    public void BakeOutputThroughAnAliasOfAnyProtectedFolderIsRefused()
    {
        string install = Folder("game"), source = Folder("source"), evidence = Folder("evidence");
        foreach (string protectedFolder in new[] { install, source, evidence })
        {
            string alias = Alias("alias-" + Path.GetFileName(protectedFolder), protectedFolder);
            Assert.Throws<IOException>(() => FixtureBake.RefuseOutput(Path.Combine(alias, "baked"), install, source, evidence));
            Assert.Throws<IOException>(() => FixtureBake.RefuseOutput(Path.Combine(protectedFolder, "baked"), alias));
            Assert.False(Path.Exists(Path.Combine(protectedFolder, "baked")));
        }
        FixtureBake.RefuseOutput(Path.Combine(Folder("elsewhere"), "baked"), install, source, evidence);
    }

    [Fact]
    public void RegressionInputsInsideSeesThroughAnAlias()
    {
        string install = Folder("install");
        string alias = Alias("install-alias", install);
        Assert.True(RegressionInputs.Inside(Path.Combine(alias, "BepInEx"), install));
        Assert.True(RegressionInputs.Inside(Path.Combine(install, "BepInEx"), alias));
        Assert.False(RegressionInputs.Inside(Folder("other"), alias));
    }
}
