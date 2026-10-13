using Valheim.Testing.GameSessions;
using Xunit;

// #653: one source-check owner decides the four profile rows and keeps the whole-source post-run verdict.
public sealed class ProfileSourceCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("profile-source-check-").FullName;
    private readonly LocalGameHost _host = new("source-host", HostShell.ThisMachine);

    public void Dispose()
    {
        ProfileSourceCheck.HashAfterRunOverride.Value = null;
        Directory.Delete(_root, recursive: true);
    }

    private async Task<ProfileSourceCheck> PrepareAsync(params string[] links) =>
        ProfileSourceCheck.Prepare(await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30)), links);

    [Theory]
    [InlineData("game.bin")]
    [InlineData("logs/steam.txt")]
    public async Task AWriteDuringTheRunFailsThePostRunVerdictAndNamesTheSourceFile(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "before");
        var check = await PrepareAsync();

        File.WriteAllText(path, "changed during the fake run");
        var error = await Assert.ThrowsAsync<IOException>(() => check.VerifyAfterRunAsync(_host, TimeSpan.FromSeconds(30)));

        Assert.Contains(relative.Replace('\\', '/'), error.Message);
        Assert.Contains("not repaired", error.Message);
        Assert.Equal("changed during the fake run", File.ReadAllText(path));
    }

    [Fact]
    public async Task AnAddedLogIsAFindingRatherThanASkippedRuntimeFile()
    {
        File.WriteAllText(Path.Combine(_root, "game.bin"), "game");
        var check = await PrepareAsync();
        string log = Path.Combine(_root, "logs", "connection_log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "Steam wrote this during the run");

        var error = await Assert.ThrowsAsync<IOException>(() => check.VerifyAfterRunAsync(_host, TimeSpan.FromSeconds(30)));

        Assert.Contains("added: logs/connection_log.txt", error.Message);
    }

    [Fact]
    public async Task AGateRunHashesContentsEvenWhenSizeAndModifiedTimeAreRestored()
    {
        string path = Path.Combine(_root, "same.bin");
        File.WriteAllText(path, "aaaa");
        var modified = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);
        var check = await PrepareAsync();
        File.WriteAllText(path, "bbbb");
        File.SetLastWriteTimeUtc(path, modified);
        await check.VerifyAfterRunAsync(_host, TimeSpan.FromSeconds(30)); // ordinary metadata verdict

        ProfileSourceCheck.HashAfterRunOverride.Value = true;
        var error = await Assert.ThrowsAsync<IOException>(() => check.VerifyAfterRunAsync(_host, TimeSpan.FromSeconds(30)));
        Assert.Contains("different content: same.bin", error.Message);
    }

    [Fact]
    public async Task TheBeforeBootScopeIncludesEveryRootFileChosenByTheWindowsLinkPlan()
    {
        foreach (string relative in new[] { "UnityPlayer.dll", "steam_api64.dll", "valheim.exe", "valheim_server.exe", "UnityCrashHandler64.exe", "unrelated.txt" })
            File.WriteAllText(Path.Combine(_root, relative), relative);
        Directory.CreateDirectory(Path.Combine(_root, "valheim_Data"));
        File.WriteAllText(Path.Combine(_root, "valheim_Data", "data.unity3d"), "game data");
        var source = await ProfileSourceCheck.InspectAsync(_host, _root, TimeSpan.FromSeconds(30));
        var plan = HostedRuntimeStage.PlanWindowsProfile(source.Files.Keys);
        var check = ProfileSourceCheck.Prepare(source, plan.Links);

        Assert.Contains("UnityPlayer.dll", check.BeforeBoot.Keys);
        Assert.Contains("steam_api64.dll", check.BeforeBoot.Keys);
        Assert.DoesNotContain("unrelated.txt", check.BeforeBoot.Keys);
        Assert.Contains("valheim.exe", check.LinkedRootFiles.Keys);
        Assert.Contains("valheim_server.exe", check.LinkedRootFiles.Keys);
        Assert.Contains("UnityCrashHandler64.exe", check.LinkedRootFiles.Keys);
        Assert.DoesNotContain("valheim_Data/data.unity3d", check.LinkedRootFiles.Keys);
    }

    [Fact]
    public void TheStagingScopeIsCopiedFilesBepInExLoaderAndPins()
    {
        var paths = ProfileSourceCheck.StagingVerificationPaths(["steam-runtime/libsteam_api.dylib"]);
        Assert.Contains("BepInEx", paths);
        Assert.Contains("*_Data/Managed", paths);
        Assert.Contains("steam-runtime/libsteam_api.dylib", paths);
        Assert.DoesNotContain("logs", paths);
    }

    [Fact]
    public void CallersDoNotRestoreTheRemovedFullSourceRules()
    {
        string source = Path.Combine(FixtureProjects.RepositoryRoot(), "src");
        string stage = File.ReadAllText(Path.Combine(source, "Valheim.Testing.GameSessions", "HostedRuntimeStage.Profile.cs"));
        string hostInstall = File.ReadAllText(Path.Combine(source, "Valheim.Testing.GameSessions", "HostInstall.cs"));
        string phases = File.ReadAllText(Path.Combine(source, "Valheim.Testing.NativeSmoke", "ServerLoadPhases.cs"));
        int directoryInputs = phases.IndexOf("DirectoryInputs", StringComparison.Ordinal);
        string phaseDirectories = phases[directoryInputs..phases.IndexOf(';', directoryInputs)];
        Assert.DoesNotContain("ListAsync(host, source", stage);
        Assert.DoesNotContain("PreparedSourceFiles", hostInstall);
        Assert.DoesNotContain("sourceFiles == null", hostInstall);
        Assert.DoesNotContain("\"--server\"", phaseDirectories);

        string sessions = string.Concat(Directory.EnumerateFiles(Path.Combine(source, "Valheim.Testing.GameSessions"), "*.cs")
            .Select(File.ReadAllText));
        static int Calls(string text, string name) => text.Split(name + "(", StringSplitOptions.None).Length - 1;
        Assert.Equal(2, Calls(sessions, "ListProfileSourceAsync")); // definition and ProfileSourceCheck owner
        Assert.Equal(2, Calls(sessions, "ListMetadataAsync")); // definition and ProfileSourceCheck
        Assert.Equal(2, Calls(sessions, "StagingVerificationPaths")); // definition and profile stage
    }
}
