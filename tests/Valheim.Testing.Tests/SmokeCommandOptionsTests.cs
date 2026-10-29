using Xunit;

public sealed class SmokeCommandOptionsTests
{
    [Fact] public void SeededStartRetainsTheExactRequestAndDoesNotImplyNoMod()
    {
        Assert.True(StartArguments.TryRead(["--mod", "candidate.dll", "--world-seed", "aBc123"], out var first, out string error), error);
        Assert.Equal("aBc123", first!.Create!.Seed);
        Assert.Equal(first.Create.Name, first.Options["--world-name"]);
        Assert.DoesNotContain("--no-mod", first.Switches);
        Assert.True(StartArguments.TryRead(["--no-mod", "--world-seed", "aBc123", "--world-name", "Fresh-World"], out var named, out error), error);
        Assert.Equal("Fresh-World", named!.Create!.Name);
    }

    public static IEnumerable<object[]> InvalidSeedOptions()
    {
        yield return [new[] { "--world-name", "Fresh" }];
        yield return [new[] { "--world-seed", "abc", "--world-fixture", "fixture" }];
        yield return [new[] { "--world-seed", "abc", "--compare-mod", "other", "--compare-source", "commit" }];
        yield return [new[] { "--world-seed", "a b" }];
        yield return [new[] { "--world-seed", "abc", "--world-name", "CON" }];
    }

    [Theory]
    [MemberData(nameof(InvalidSeedOptions))]
    public void InvalidSeedCombinationsRefuseBeforeMachineSelection(string[] options) =>
        Assert.False(StartArguments.TryRead(["--mod", "candidate.dll", .. options], out _, out _));

    [Theory]
    [InlineData("Build")]
    [InlineData("ServerLoad")]
    [InlineData("ServerLoadAb")]
    public void SeededCreationIsNotAnUnimplementedServerOrBuildOption(string command) =>
        Assert.False(SmokeCommandOptions.TryRead(["--mod", "candidate.dll", "--world-seed", "abc"], Enum.Parse<SmokeCommandOptions.Command>(command), true, out _, out _));

    [Fact]
    public void NoModIsExplicitStartOnlyAndRetainsTheExistingRunInputs()
    {
        Assert.True(StartArguments.TryRead(["--no-mod", "--hold", "--config", "example.cfg", "--env", "CONTROL=" + ExplicitRunInputsTests.Opaque],
            out var parsed, out string error), error);
        Assert.Contains("--no-mod", parsed!.Switches);
        Assert.Empty(parsed.List("--mod"));
        Assert.Equal(["example.cfg"], parsed.RunInputs.Configs);
        Assert.Equal(ExplicitRunInputsTests.Opaque, parsed.RunInputs.Environment["CONTROL"]);
        Assert.False(StartArguments.TryRead([], out _, out error));
        Assert.Equal("Missing: --mod", error);
        foreach (var command in new[] { SmokeCommandOptions.Command.Build, SmokeCommandOptions.Command.ServerLoad, SmokeCommandOptions.Command.ServerLoadAb })
        {
            Assert.False(SmokeCommandOptions.TryRead(["--no-mod"], command, true, out _, out error));
            Assert.Equal("Unknown option: --no-mod", error);
        }
        Assert.False(StartArguments.TryRead(["--no-mod", "--no-mod"], out _, out error));
        Assert.Equal("Repeated option: --no-mod", error);
        Assert.False(StartArguments.TryRead(["--no-mod", "value"], out _, out error));
        Assert.Equal("Unknown option: value", error);
        Assert.False(StartArguments.TryRead(["--no-mod", "--env", "DOORSTOP_CUSTOM=value"], out _, out _));
        Assert.False(StartArguments.TryRead(["--no-mod", "--config", "valheimCLI.valheimCLI.cfg"], out _, out _));
    }

    [Theory]
    [InlineData("--mod")]
    [InlineData("--project")]
    [InlineData("--build-inputs")]
    [InlineData("--source")]
    [InlineData("--compare-mod")]
    [InlineData("--compare-source")]
    [InlineData("--dependency")]
    [InlineData("--search-root")]
    [InlineData("--optional-reference")]
    [InlineData("--scenario")]
    [InlineData("--scenario-project")]
    public void NoModRefusesContradictorySelectionsBeforeMachineSelection(string option)
    {
        Assert.False(StartArguments.TryRead(["--no-mod", option, "missing-input"], out _, out string error));
        Assert.Contains("--no-mod", error);
        Assert.Contains(option, error);
    }

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

    // Copy mode is gone (#641): --copy-game is an unknown option like any other, through the one parser, with no message of its own.
    [Fact]
    public void CopyGameIsAnUnknownOption()
    {
        Assert.False(StartArguments.TryRead(["--mod", "mod.dll", "--copy-game"], out _, out _, out _, out _, out string startError));
        Assert.Equal("Unknown option: --copy-game", startError);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--copy-game"], out _, out string serverError));
        Assert.Equal(startError, serverError);
        Assert.False(SmokeCommandOptions.TryRead(["--mod", "mod.dll", "--mod", "other.dll", "--remove-mod", "other.dll", "--output", "new-run",
            "--copy-game"], SmokeCommandOptions.Command.ServerLoadAb, allowImplicitMod: false, out _, out string comparisonError));
        Assert.Equal(startError, comparisonError);
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
        Assert.True(ServerLoad.TryRead(["--mod", "mod.dll", "--server-startup-seconds", "3600"], out _, out error), error);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--server-startup-seconds", "3601"], out _, out error));
        Assert.Contains("1 to 3600", error);
        Assert.True(ServerLoad.TryRead(["--mod", "mod.dll", "--world-fixture", "fixture"], out _, out error), error);
        Assert.False(ServerLoad.TryRead(["--mod", "mod.dll", "--bake-fixture", "new-fixture",
            "--assert-command", "cli_world", "--assert-line", "WORLD"], out _, out error));
        Assert.Contains("--server-only", error);
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
