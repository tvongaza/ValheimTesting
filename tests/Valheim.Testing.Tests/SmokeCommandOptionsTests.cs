using Xunit;

public sealed class SmokeCommandOptionsTests
{
    public static IEnumerable<object[]> SharedBadOptions()
    {
        yield return [new[] { "--client-architecture", "native" }];
        yield return [new[] { "--join-seconds", "9" }];
        yield return [new[] { "--join-seconds", "901" }];
        yield return [new[] { "--join-seconds", "1.5" }];
        yield return [new[] { "--expected-log-error", "error" }];
        yield return [new[] { "--expected-log-reason", "reason" }];
        yield return [new[] { "--client-loader-package", "one", "--client-loader-package", "two" }];
        yield return [new[] { "--hold", "--hold" }];
        yield return [new[] { "--client-env" }];
        yield return [new[] { "--does-not-exist", "value" }];
    }

    [Theory]
    [MemberData(nameof(SharedBadOptions))]
    public void SharedBadOptionsGiveTheSameRefusal(string[] extra)
    {
        string[] start = ["--mod", "mod.dll", "--output", "new-run", .. extra];
        string[] server = ["--mod", "mod.dll", "--output", "new-run", .. extra];
        Assert.False(StartArguments.TryRead(start, out _, out _, out _, out _, out string startError));
        Assert.False(ServerLoad.TryRead(server, out _, out string serverError));
        Assert.Equal(startError, serverError);
        string[] comparison = ["--mod", "mod.dll", "--mod", "other.dll", "--remove-mod", "other.dll", "--output", "new-run", .. extra];
        if (Array.IndexOf(extra, "--hold") < 0)
        {
            Assert.False(SmokeCommandOptions.TryRead(comparison, SmokeCommandOptions.Command.ServerLoadAb,
                allowImplicitMod: false, out _, out string comparisonError));
            Assert.Equal(startError, comparisonError);
        }
    }

    [Fact]
    public void SharedClientLoaderAndJoinBudgetHaveTheSameMeaning()
    {
        string[] options = ["--mod", "mod.dll", "--output", "new-run", "--client-loader-package", "client.json", "--join-seconds", "600"];
        Assert.True(StartArguments.TryRead(options, out var start, out _, out _, out _, out string startError), startError);
        Assert.True(ServerLoad.TryRead(options, out var server, out string serverError), serverError);
        Assert.Equal(start!["--client-loader-package"], server!.Options["--client-loader-package"]);
        Assert.Equal(start["--join-seconds"], server.Options["--join-seconds"]);
        Assert.False(StartArguments.TryRead([.. options, "--loader-package", "server.json"], out _, out _, out _, out _, out _));
        Assert.True(ServerLoad.TryRead([.. options, "--loader-package", "server.json"], out _, out _));
    }

    [Fact]
    public void LongGenerationBudgetBelongsToDedicatedServerRuns()
    {
        Assert.True(ServerLoad.TryRead(["--mod", "mod.dll", "--server-only", "--server-startup-seconds", "900"],
            out var server, out string error), error);
        Assert.Equal("900", server!.Options["--server-startup-seconds"]);
        Assert.False(StartArguments.TryRead(["--mod", "mod.dll", "--server-startup-seconds", "900"],
            out _, out _, out _, out _, out error));
        Assert.Contains("--server-startup-seconds", error);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--server-startup-seconds", "1801"], out _, out error));
        Assert.Contains("1 to 1800", error);
    }

    [Fact]
    public void ImplicitModIsAcceptedOnlyByTheOneShotCommandPath()
    {
        Assert.True(StartArguments.TryRead(["--output", "new-run"], out _, out var startMods, out _, out _, out _, allowImplicitMod: true));
        Assert.Empty(startMods!);
        Assert.True(ServerLoad.TryRead(["--output", "new-run"], out var server, out _, allowImplicitMod: true));
        Assert.Empty(server!.Mods);
        Assert.False(StartArguments.TryRead(["--output", "new-run"], out _, out _, out _, out _, out _));
        Assert.False(ServerLoad.TryRead(["--output", "new-run"], out _, out _));
    }

    [Fact]
    public void BothOneShotCommandsAllowTheSameDefaultOutputRule()
    {
        Assert.True(StartArguments.TryRead(["--mod", "mod.dll"], out var start, out _, out _, out _, out _));
        Assert.True(ServerLoad.TryRead(["--mod", "mod.dll"], out var server, out _));
        Assert.False(start!.ContainsKey("--output"));
        Assert.False(server!.Options.ContainsKey("--output"));
        string automatic = SmokeCommandOptions.Output(start);
        Assert.StartsWith(Path.Combine(Valheim.Testing.Game.EnvironmentInventory.ThisMachine.DataRoot, "valheim-test-runs") +
            Path.DirectorySeparatorChar, automatic, StringComparison.Ordinal);
        string existing = Directory.CreateTempSubdirectory("smoke-output-").FullName;
        try
        {
            Assert.Contains("existing evidence", Assert.Throws<IOException>(() => SmokeCommandOptions.Output(
                new Dictionary<string, string> { ["--output"] = existing })).Message);
        }
        finally { Directory.Delete(existing); }
    }

    [Fact]
    public void OutputCannotLeaveGeneratedSourcesInsideTheModProject()
    {
        string project = Directory.CreateTempSubdirectory("smoke-output-project-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(project, "MyMod.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            string nested = Path.Combine(project, "valheim-test-runs", "one");
            Assert.Contains("outside the mod project", Assert.Throws<ArgumentException>(() =>
                SmokeCommandOptions.Output(new Dictionary<string, string> { ["--output"] = nested })).Message);
            string sibling = Path.Combine(Path.GetDirectoryName(project)!, "sibling-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(sibling, SmokeCommandOptions.Output(new Dictionary<string, string> { ["--output"] = sibling }));
        }
        finally { Directory.Delete(project, recursive: true); }
    }

    [Fact]
    public void UnreadableOutputAncestorIsNamedBeforeAnyOutputIsCreated()
    {
        string root = Directory.CreateTempSubdirectory("smoke-output-unreadable-").FullName;
        try
        {
            string output = Path.Combine(root, "new", "run");
            var error = Assert.Throws<IOException>(() => SmokeCommandOptions.Output(
                new Dictionary<string, string> { ["--output"] = output },
                parent => parent == root ? throw new UnauthorizedAccessException("listing denied") : false));
            Assert.Contains(root, error.Message);
            Assert.Contains("omit --output", error.Message);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
            Assert.False(Path.Exists(Path.Combine(root, "new")));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void StartNamesTheRenamedClientLoaderOption()
    {
        Assert.False(StartArguments.TryRead(["--mod", "mod.dll", "--loader-package", "loader.json"],
            out _, out _, out _, out _, out string error));
        Assert.Contains("--client-loader-package", error);
    }

    [Fact]
    public void LoaderProvenanceUsesTheSameRoleKeysForEachCommand()
    {
        var provenance = new Dictionary<string, string>();
        SmokeInputResolver.RecordLoader(provenance, "client", null,
            new Valheim.Testing.GameSessions.ShippedLoader.Choice("unused.json", "mismatched source pair"));

        Assert.Equal("mismatched source pair", provenance["clientLoaderShipped"]);
        Assert.DoesNotContain("bepInExPackageShipped", provenance.Keys);
    }

    [Fact]
    public void ComparisonOnlyAcceptsOneExplicitRemovalAndOutput()
    {
        string[] mods = ["--mod", "a.dll", "--mod", "b.dll"];
        Assert.False(SmokeCommandOptions.TryRead(mods, SmokeCommandOptions.Command.ServerLoadAb,
            allowImplicitMod: false, out _, out string missing));
        Assert.Contains("--remove-mod", missing);
        Assert.False(SmokeCommandOptions.TryRead([.. mods, "--remove-mod", "b.dll"],
            SmokeCommandOptions.Command.ServerLoadAb, allowImplicitMod: false, out _, out string noOutput));
        Assert.Contains("--output", noOutput);
        Assert.False(SmokeCommandOptions.TryRead([.. mods, "--remove-mod", "b.dll", "--remove-mod", "a.dll", "--output", "run"],
            SmokeCommandOptions.Command.ServerLoadAb, allowImplicitMod: false, out _, out string duplicate));
        Assert.Contains("Repeated option", duplicate);
        Assert.True(SmokeCommandOptions.TryRead([.. mods, "--remove-mod", "b.dll", "--output", "run"],
            SmokeCommandOptions.Command.ServerLoadAb, allowImplicitMod: false, out var parsed, out _));
        Assert.Equal("b.dll", parsed!.Options["--remove-mod"]);
    }
}
