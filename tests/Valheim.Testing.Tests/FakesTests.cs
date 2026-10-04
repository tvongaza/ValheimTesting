using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The public fakes behave like the real protocol where scenario code depends on it.
public class FakesTests
{
    // #270: the one fake process, standalone. A failing stop counts, throws and leaves the process running, as an unproven real stop does.
    [Fact] public async Task AStandaloneFakeProcessCountsStopsAndCanRefuseThem()
    {
        var process = new FakeOwnedProcess(42) { StopFailure = () => new IOException("stop could not be proven") };
        Assert.Throws<IOException>(() => process.Stop(TimeSpan.FromSeconds(1)));
        Assert.Throws<IOException>(() => process.StopCleanly(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Equal((42, 2, false), (process.Id, process.Stops, process.HasExited));
        var crashed = FakeOwnedProcess.Exited(3);
        crashed.StopFailure = () => new IOException("stop could not be proven");
        Assert.Throws<IOException>(() => crashed.StopCleanly(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))); // Even an exited process's stop fails.
        process.StopFailure = null;
        Assert.Equal(StopOutcome.Clean, process.StopCleanly(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).Outcome);
        Assert.Equal(0, await process.WaitForExitAsync(default));
        process.Dispose();
        Assert.Equal((3, 1), (process.Stops, process.Disposals));
    }
    [Fact] public void AScriptedExtensionIsDiscoveredAndObservedLikeARealOne()
    {
        var transport = new ScriptedTransport().Extension("my.mod", "state", args => new { source = "memory", complete = true, zone = args.Count == 0 ? "none" : args[0] });
        using var actor = transport.Actor();
        var capability = actor.RequireCapability("my.mod/state");
        Assert.True(capability.ReadOnly);
        var observation = actor.Observe(capability, "3,4");
        Assert.Equal("memory", observation.Source); Assert.True(observation.Complete);
        Assert.Equal("3,4", observation.Data.GetProperty("zone").GetString());
        Assert.Throws<InvalidOperationException>(() => actor.RequireCapability("my.mod/absent"));
    }
    [Fact] public void UnscriptedCommandsFailLoudlyAndEveryCommandIsRecorded()
    {
        var transport = new ScriptedTransport();
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => actor.Execute("road_generate"));
        Assert.Equal(1, transport.Count("road_generate"));
        Assert.Contains(transport.Commands, c => c.StartsWith("cli_expect", StringComparison.Ordinal));
    }
    [Fact] public void RegisteredRepliesComeFirstAndSavesAreConfirmedWithRisingNumbers()
    {
        var transport = new ScriptedTransport().Saves().On("road_generate", _ => ScriptedTransport.Ok("OK: generated")).OnPrefix("road_path ", c => ScriptedTransport.Ok("OK: " + c));
        using var actor = transport.Actor();
        Assert.Equal("OK: generated", actor.Execute("road_generate").Output.Single());
        Assert.Equal("OK: road_path 1,2 3,4", actor.Execute("road_path 1,2 3,4").Output.Single());
        Assert.Equal("OK: SAVE saveNumber=2", actor.Execute("cli_save").Output.Single());
        Assert.Equal("OK: SAVE saveNumber=3", actor.Execute("cli_save").Output.Single());
        var refusing = new ScriptedTransport().Saves(confirmed: false);
        using var other = refusing.Actor();
        Assert.Throws<InvalidOperationException>(() => other.Execute("cli_save"));
    }
    [Fact] public void BrokenPinsRefuseTheActor()
    {
        var transport = new ScriptedTransport { PinsHold = false };
        Assert.Throws<InvalidOperationException>(() => transport.Actor());
    }
    [Fact] public void AFakeClientDataDirectoryIsTheDefaultOnEveryPlatformOnlyInsideItsScope()
    {
        var platforms = new[] { ClientPlatform.Windows, ClientPlatform.MacOS, ClientPlatform.Linux };
        var real = platforms.Select(HostedWorld.DefaultSaveDirectory).ToArray();
        string outer = Path.Combine(Path.GetTempPath(), "fake-client-outer"), inner = Path.Combine(Path.GetTempPath(), "fake-client-inner");
        using (var first = new FakeClientDataDirectory(outer))
        {
            Assert.All(platforms, p => Assert.Equal(Path.GetFullPath(outer), HostedWorld.DefaultSaveDirectory(p)));
            using (new FakeClientDataDirectory(inner))
                Assert.All(platforms, p => Assert.Equal(Path.GetFullPath(inner), HostedWorld.DefaultSaveDirectory(p)));
            // Disposing the inner scope restores the outer one, not the real default.
            Assert.All(platforms, p => Assert.Equal(Path.GetFullPath(outer), HostedWorld.DefaultSaveDirectory(p)));
            first.Dispose(); first.Dispose(); // Twice is harmless.
            Assert.Equal(real, platforms.Select(HostedWorld.DefaultSaveDirectory));
        }
        Assert.Equal(real, platforms.Select(HostedWorld.DefaultSaveDirectory));
        Assert.All(real, path => Assert.DoesNotContain("fake-client", path, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new FakeClientDataDirectory(" "));
    }
    [Fact] public void AnOuterFakeClientDataDirectoryCannotBeDisposedBeforeItsInnerOne()
    {
        var real = HostedWorld.DefaultSaveDirectory(ClientPlatform.MacOS);
        var outer = new FakeClientDataDirectory(Path.Combine(Path.GetTempPath(), "fake-client-outer"));
        var inner = new FakeClientDataDirectory(Path.Combine(Path.GetTempPath(), "fake-client-inner"));
        Assert.Throws<InvalidOperationException>(outer.Dispose);
        Assert.Equal(inner.Directory, HostedWorld.DefaultSaveDirectory(ClientPlatform.MacOS)); // Nothing changed.
        inner.Dispose();
        Assert.Equal(outer.Directory, HostedWorld.DefaultSaveDirectory(ClientPlatform.MacOS));
        outer.Dispose();
        Assert.Equal(real, HostedWorld.DefaultSaveDirectory(ClientPlatform.MacOS));
    }
    [Fact] public void AFakeOwnedServerStartsRestartsAndRecordsItsLifecycle()
    {
        var server = new FakeOwnedServer("my.mod");
        using var session = server.Session(TimeSpan.FromSeconds(60));
        session.Start(); session.Restart();
        Assert.Equal(new[] { "launch1", "probe1", "pins1", "disconnect1", "stop1", "dispose1", "launch2", "probe2", "pins2" }, server.Events);
        Assert.Equal(2, server.Tokens.Distinct().Count());
    }
    [Fact] public void AFakeOwnedServerCanReportTheWrongIdentity()
    {
        var server = new FakeOwnedServer("my.mod") { ReportWrongPid = true };
        using var session = server.Session(TimeSpan.FromSeconds(60));
        Assert.Throws<InvalidOperationException>(() => session.Start());
        Assert.DoesNotContain("pins1", server.Events);
    }
    [Fact] public void ATempRuntimeLogCanBeAppendedAndRewritten()
    {
        string path;
        using (var runtime = new TempRuntime())
        {
            path = runtime.DirectoryPath;
            runtime.Append("a\n"); runtime.Append("b\n"); Assert.Equal("a\nb\n", File.ReadAllText(runtime.LogPath));
            runtime.Replace("c\n"); Assert.Equal("c\n", File.ReadAllText(runtime.LogPath));
        }
        Assert.False(Directory.Exists(path));
    }
}
