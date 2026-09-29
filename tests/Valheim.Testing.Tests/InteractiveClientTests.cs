using System.Diagnostics;
using System.Globalization;
using System.Text;
using Valheim.Testing.Game;
using Xunit;

// Starting a client in a host's desktop session, with fake processes: the launch each platform builds, what the host is sent,
// how each refusal and a lost reply are reported, that the Windows task is always named for this launch and removed, and that a
// secret never reaches anything a person or a report can read.
public class InteractiveClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const string WindowsInstall = @"C:\Games\Valheim";
    private const string WindowsLaunch = @"C:\ValheimTesting\runs\run-42\client";
    private const string LinuxInstall = "/home/steam/valheim";
    private const string LinuxLaunch = "/srv/valheim-testing/runs/run-42/client";

    private static ScriptedGameHost WindowsHost(FakeLauncher fake) => new SshGameHost("gaming-pc", "tester@gaming-pc.example", HostShell.WindowsPowerShell, 0, null, null, "ssh", fake);
    private static ScriptedGameHost LinuxHost(FakeLauncher fake) => new SshGameHost("linux-box", "tester@linux-box.example", HostShell.Bash, 0, null, null, "ssh", fake);
    private static string Reply(params string[] lines) => string.Join('\n', lines) + "\n";

    internal static List<(string Kind, string Value)> Decode(string spec) => spec.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split(' ', 2)).Select(parts => (parts[0], Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])))).ToList();
    internal static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact] public void AWindowsLaunchIsWhatClientLaunchBuildsThere()
    {
        var launch = HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall + "\\", ["+connect", "server.example:2456", "a b", "say \"hi\""],
            new Dictionary<string, string> { ["MY_FLAG"] = "1" }, ["VT_JOIN_PASSWORD"]);
        Assert.Equal(WindowsInstall, launch.Install);
        Assert.Equal(WindowsInstall + @"\valheim.exe", launch.Executable);
        Assert.Equal(new[] { "-console", "+connect", "server.example:2456", "a b", "say \"hi\"" }, launch.Arguments);
        Assert.Equal("892970", launch.Environment["SteamAppId"]);
        Assert.Equal("1", launch.Environment["my_flag"]); // Windows variable names ignore case.
        Assert.Equal(new[] { "DOORSTOP_ENABLED", "DOORSTOP_TARGET_ASSEMBLY", "DOORSTOP_DISABLE" }, launch.Unset);
        Assert.Empty(launch.Prepended);
        Assert.Equal(new[] { "valheim.exe", @"BepInEx\core\BepInEx.Preloader.dll", @"BepInEx\core\BepInEx.dll", "winhttp.dll", "doorstop_config.ini" }, launch.RequiredFiles);
        Assert.Equal(new[] { "VT_JOIN_PASSWORD" }, launch.SecretVariables);

        var spec = Decode(launch.Spec());
        Assert.Contains(("exe", WindowsInstall + @"\valheim.exe"), spec);
        Assert.Contains(("dir", WindowsInstall), spec);
        Assert.Contains(("args", "-console +connect server.example:2456 \"a b\" \"say \\\"hi\\\"\""), spec);
        Assert.Contains(("unset", "DOORSTOP_ENABLED"), spec);
        Assert.Contains(("env", "SteamAppId=892970"), spec);
        // A secret is named, never set, in the launch.
        Assert.DoesNotContain(spec, line => line.Value.Contains("VT_JOIN_PASSWORD"));
    }

    [Fact] public void ALinuxLaunchSetsTheLoaderAsThePacksScriptDoes()
    {
        var launch = HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, ["+name", "it's"], new Dictionary<string, string> { ["LD_PRELOAD"] = "mine.so" }, console: false);
        Assert.Equal(LinuxInstall + "/valheim.x86_64", launch.Executable);
        Assert.Equal(new[] { "+name", "it's" }, launch.Arguments);
        Assert.Equal("1", launch.Environment["DOORSTOP_ENABLED"]);
        Assert.Equal(LinuxInstall + "/BepInEx/core/BepInEx.Preloader.dll", launch.Environment["DOORSTOP_TARGET_ASSEMBLY"]);
        Assert.Equal(LinuxInstall + "/doorstop_libs", launch.Prepended["LD_LIBRARY_PATH"]);
        Assert.Equal("libdoorstop_x64.so", launch.Prepended["LD_PRELOAD"]);
        Assert.Equal(new[] { "DOORSTOP_DISABLE" }, launch.Unset);
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", launch.RequiredFiles);
        var spec = Decode(launch.Spec());
        Assert.Equal(new[] { "+name", "it's" }, spec.Where(line => line.Kind == "arg").Select(line => line.Value));
        // The caller's value is set first; the host then puts the loader in front of it.
        Assert.Contains(("env", "LD_PRELOAD=mine.so"), spec);
        Assert.Contains(("prepend", "LD_PRELOAD=libdoorstop_x64.so"), spec);
        Assert.True(spec.FindIndex(line => line.Kind == "env") < spec.FindIndex(line => line.Kind == "prepend"));
    }

    [Fact] public void ALaunchThatWouldNotLoadBepInExOrCannotBeStartedIsRefused()
    {
        Assert.Throws<PlatformNotSupportedException>(() => HostClientLaunch.Create(ClientPlatform.MacOS, "/Users/tester/Valheim", []));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, [], new Dictionary<string, string> { ["doorstop_enabled"] = "0" }));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, ["--doorstop-enabled", "false"]));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Linux, "games/valheim", []));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Linux, "/games/val:heim", []));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Windows, "/games/valheim", []));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, ["a\nb"]));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, [], null, ["DOORSTOP_ENABLED"]));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, [], new Dictionary<string, string> { ["PW"] = "x" }, ["PW"]));
        Assert.Throws<ArgumentException>(() => HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, [], null, ["not a name"]));
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\path with space\", "\"C:\\path with space\\\\\"")]
    [InlineData(@"a\""b", "\"a\\\\\\\"b\"")]
    [InlineData(@"a\b", @"a\b")]
    public void WindowsArgumentsAreQuotedAsTheGameReadsThemBack(string argument, string quoted) => Assert.Equal(quoted, WindowsCommandLine.Quote(argument));

    [Fact] public async Task AWindowsClientStartsThroughAUniqueTaskThatIsAlwaysRemoved()
    {
        var fake = new FakeLauncher()
            .Exits(0, Reply("VT-TASK removed", "VT-INTERACTIVE started 4242 133700000000000000"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-TASK removed", "VT-INTERACTIVE started 4343 133700000000000001"), FakeLauncher.Report(0));
        var host = WindowsHost(fake);
        var launch = HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, []);
        var client = await InteractiveClient.StartAsync(host, launch, WindowsLaunch + "\\", Timeout);
        Assert.Equal(4242, client.Id); Assert.Equal("133700000000000000", client.StartIdentity);
        Assert.Equal(WindowsLaunch, client.LaunchDirectory); Assert.Equal("gaming-pc", client.HostName);
        Assert.StartsWith(InteractiveClient.TaskPrefix, client.TaskName);
        string script = FakeLauncher.Script(fake.Calls[0]);
        Assert.Contains("$task = '" + client.TaskName + "'", script);
        Assert.Contains("$dir = '" + WindowsLaunch + "'", script);
        Assert.Contains("$install = '" + WindowsInstall + "'", script);
        // "Run only when user is logged on": an interactive token, and no password is given or stored.
        Assert.Contains("$definition.Principal.LogonType = 3", script);
        Assert.Contains("RegisterTaskDefinition($task, $definition, 2, $me, $null, 3)", script);
        Assert.Contains("try { $folder.DeleteTask($task, 0) }", script);
        Assert.Contains("$launcher = '" + InteractiveScripts.WindowsLauncher.Replace("'", "''"), script);

        var second = await InteractiveClient.StartAsync(host, launch, WindowsLaunch + "-2", Timeout);
        Assert.NotEqual(client.TaskName, second.TaskName);

        fake.Exits(0, Reply("VT-STOP stopped"), FakeLauncher.Report(0));
        await client.DisposeAsync();
        string stop = FakeLauncher.Script(fake.Calls[2]);
        Assert.Contains("$game = '4242'", stop); Assert.Contains("$start = '133700000000000000'", stop);
        Assert.True(client.HasExited);
        await client.DisposeAsync();
        Assert.Equal(3, fake.Calls.Count);
    }

    [Fact] public async Task ALinuxClientStartsOnTheDisplayOfTheContainerSession()
    {
        var fake = new FakeLauncher().Exits(0, Reply("VT-INTERACTIVE started 812 4711"), FakeLauncher.Report(0));
        var host = new ContainerGameHost("client", "vt", HostShell.Bash, "steam", "docker", fake);
        var client = await InteractiveClient.StartAsync(host, HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, []), LinuxLaunch, Timeout, LinuxDisplay.ClientContainer);
        Assert.Equal(812, client.Id); Assert.Null(client.TaskName);
        Assert.Equal(new[] { "exec", "-i", "--user", "steam", "vt", "bash", "-c", HostScripts.BashWrapper }, fake.Calls[0].Arguments);
        string script = FakeLauncher.Script(fake.Calls[0]);
        Assert.Contains("display=':0'", script); Assert.Contains("wayland=''", script); Assert.Contains("runtime=''", script);
        Assert.Contains("exe='valheim.x86_64'", script);
        Assert.Contains("setsid bash -c", script);
    }

    [Fact] public void ALinuxDisplayIsALocalDisplay()
    {
        _ = new LinuxDisplay(":1.0", "wayland-0", "/run/user/1000", "/run/user/1000/gdm/Xauthority");
        Assert.Throws<ArgumentException>(() => new LinuxDisplay("remote.example:0"));
        Assert.Throws<ArgumentException>(() => new LinuxDisplay("0"));
        Assert.Throws<ArgumentException>(() => new LinuxDisplay(":0", "../wayland-0"));
        Assert.Throws<ArgumentException>(() => new LinuxDisplay(":0", null, "run/user/1000"));
    }

    [Theory]
    [InlineData("no-session tester has no desktop session here", typeof(InteractiveSessionException))]
    [InlineData("no-steam no Steam client runs", typeof(InteractiveSessionException))]
    [InlineData("missing BepInEx\\core\\BepInEx.dll", typeof(FileNotFoundException))]
    [InlineData("exists", typeof(InvalidOperationException))]
    [InlineData("unsupported this host runs Darwin", typeof(PlatformNotSupportedException))]
    [InlineData("failed the launcher reported: access denied", typeof(InvalidOperationException))]
    public async Task EachRefusalIsItsOwnError(string verdict, Type expected)
    {
        foreach (bool windows in new[] { true, false })
        {
            var fake = new FakeLauncher().Exits(0, Reply("VT-INTERACTIVE " + verdict) + (windows && verdict.StartsWith("failed") ? "VT-TASK removed\n" : ""), FakeLauncher.Report(0));
            var error = await Record.ExceptionAsync(() => windows
                ? InteractiveClient.StartAsync(WindowsHost(fake), HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, []), WindowsLaunch, Timeout)
                : InteractiveClient.StartAsync(LinuxHost(fake), HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, []), LinuxLaunch, Timeout));
            Assert.IsType(expected, error);
            if (error is InteractiveSessionException refused)
                Assert.Equal(verdict.StartsWith("no-steam") ? InteractiveRefusal.NoSteam : InteractiveRefusal.NoSession, refused.Reason);
            Assert.Single(fake.Calls);
        }
    }

    [Fact] public async Task ATaskThatCouldNotBeRemovedStopsTheClientAndFails()
    {
        var fake = new FakeLauncher()
            .Exits(0, Reply("VT-INTERACTIVE started 4242 1", "VT-TASK kept"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-STOP stopped"), FakeLauncher.Report(0));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InteractiveClient.StartAsync(WindowsHost(fake), HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, []), WindowsLaunch, Timeout));
        Assert.Contains("schtasks /Delete /TN " + InteractiveClient.TaskPrefix, error.Message);
        Assert.Contains("$game = '4242'", FakeLauncher.Script(fake.Calls[1]));
    }

    [Fact] public async Task ALostStartReplyIsUnknownAndNamesTheTask()
    {
        var fake = new FakeLauncher().TimesOut();
        var error = await Assert.ThrowsAsync<HostOperationException>(() =>
            InteractiveClient.StartAsync(WindowsHost(fake), HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, []), WindowsLaunch, Timeout));
        Assert.Equal(HostOutcome.Unknown, error.Outcome);
        Assert.Contains("schtasks /Query /TN " + InteractiveClient.TaskPrefix, error.Message);
        Assert.Contains(WindowsLaunch, error.Message);
    }

    [Fact] public async Task AMisfittingHostOrLaunchIsRefusedBeforeAnythingRuns()
    {
        var fake = new FakeLauncher();
        var windows = HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, []);
        var linux = HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, []);
        await Assert.ThrowsAsync<ArgumentException>(() => InteractiveClient.StartAsync(LinuxHost(fake), windows, "/srv/launch", Timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => InteractiveClient.StartAsync(WindowsHost(fake), linux, WindowsLaunch, Timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => InteractiveClient.StartAsync(WindowsHost(fake), windows, WindowsLaunch, Timeout, new LinuxDisplay()));
        await Assert.ThrowsAsync<ArgumentException>(() => InteractiveClient.StartAsync(LinuxHost(fake), linux, "runs/client", Timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => InteractiveClient.StartAsync(LinuxHost(fake), linux, "/", Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => InteractiveClient.StartAsync(LinuxHost(fake), linux, LinuxLaunch, TimeSpan.FromSeconds(5)));
        string missing = "VT_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InteractiveClient.StartAsync(LinuxHost(fake), HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, [], null, [missing]), LinuxLaunch, Timeout));
        Assert.Contains(missing, error.Message);
        Assert.Empty(fake.Calls);
    }

    [Theory, InlineData(true), InlineData(false)] public async Task ASecretStaysOutOfTheComposedScriptAndIsRedactedFromEveryReply(bool windows)
    {
        string variable = "VT_TEST_CANARY_" + Guid.NewGuid().ToString("N");
        string canary = "canary-" + Guid.NewGuid().ToString("N");
        string line = Base64(variable + "=" + canary);
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            // A host that echoes the secret back (in plain and encoded form) as it fails.
            var fake = new FakeLauncher().Exits(1, "partial " + canary + "\n", "boom: " + canary + " " + line + "\n" + FakeLauncher.Report(1));
            var host = windows ? WindowsHost(fake) : LinuxHost(fake);
            var launch = windows ? HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, [], null, [variable])
                : HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, [], null, [variable]);
            var error = await Assert.ThrowsAsync<HostOperationException>(() => InteractiveClient.StartAsync(host, launch, windows ? WindowsLaunch : LinuxLaunch, Timeout));
            Assert.Contains("[redacted]", error.Message);
            // The composed script counts as visible: the host's wrapper writes it to a temporary file.
            var visible = new[] { error.Message, error.Result.Stdout, error.Result.Stderr, error.ToString(), launch.Spec(), string.Join(' ', fake.Calls[0].Arguments),
                string.Join(' ', launch.Environment.Values), string.Join(' ', launch.Arguments), FakeLauncher.Script(fake.Calls[0]) };
            foreach (string text in visible) { Assert.DoesNotContain(canary, text); Assert.DoesNotContain(line, text); }
            // It did travel: on the secrets line of standard input, which the wrapper keeps in memory.
            Assert.Equal(line, FakeLauncher.Secrets(fake.Calls[0]));

            // Negative control: the same value as a plain environment value is evidence, and the same scan finds it.
            var leaky = windows ? HostClientLaunch.Create(ClientPlatform.Windows, WindowsInstall, [], new Dictionary<string, string> { ["LEAKY"] = canary })
                : HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, [], new Dictionary<string, string> { ["LEAKY"] = canary });
            Assert.Contains(Decode(leaky.Spec()), item => item.Value.Contains(canary));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact] public async Task StoppingChecksTheIdentityAndMayBeRepeatedOnlyAfterAFailure()
    {
        var fake = new FakeLauncher().Exits(0, Reply("VT-INTERACTIVE started 900 55"), FakeLauncher.Report(0));
        var client = await InteractiveClient.StartAsync(LinuxHost(fake), HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, []), LinuxLaunch, Timeout);

        fake.TimesOut();
        var lost = await Assert.ThrowsAsync<HostOperationException>(() => client.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(HostOutcome.Unknown, lost.Outcome); Assert.False(client.HasExited);
        fake.Exits(0, Reply("VT-STOP running"), FakeLauncher.Report(0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StopAsync(TimeSpan.FromSeconds(10)));
        fake.Exits(0, Reply("VT-STOP gone"), FakeLauncher.Report(0));
        Assert.Equal(InteractiveStop.AlreadyGone, await client.StopAsync(TimeSpan.FromSeconds(10)));
        string stop = FakeLauncher.Script(fake.Calls[3]);
        Assert.Contains("game='900'", stop); Assert.Contains("start='55'", stop); Assert.Contains("seconds='10'", stop);
        Assert.Equal(InteractiveStop.AlreadyGone, await client.StopAsync(TimeSpan.FromSeconds(10)));
        client.Dispose();
        Assert.Equal(4, fake.Calls.Count);
    }

    [Fact] public async Task WaitingForTheExitReportsItsCodeWhereTheHostKnowsIt()
    {
        var fake = new FakeLauncher()
            .Exits(0, Reply("VT-INTERACTIVE started 900 55"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-WAIT running"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-WAIT exited 3"), FakeLauncher.Report(0))
            .Exits(0, Reply("VT-WAIT exited ?"), FakeLauncher.Report(0));
        var client = await InteractiveClient.StartAsync(LinuxHost(fake), HostClientLaunch.Create(ClientPlatform.Linux, LinuxInstall, []), LinuxLaunch, Timeout);
        Assert.Equal(3, await client.WaitForExitAsync(CancellationToken.None));
        Assert.True(client.HasExited);
        Assert.Equal(-1, await client.WaitForExitAsync(CancellationToken.None));
        Assert.Contains($"dir='{LinuxLaunch}'", FakeLauncher.Script(fake.Calls[1]));
    }
}

/// <summary>A fact that runs only on the named OS (windows, linux or macos).</summary>
public sealed class OsFactAttribute : FactAttribute
{
    public OsFactAttribute(string os)
    {
        bool here = os switch { "windows" => OperatingSystem.IsWindows(), "linux" => OperatingSystem.IsLinux(), "macos" => OperatingSystem.IsMacOS(), _ => false };
        if (!here) Skip = "Runs on " + os;
    }
}

/// <summary>
/// Runs only where VALHEIM_TESTING_DISPLAY names a local X display this user may use (the game-hosts CI job starts Xvfb). The
/// tests start a stand-in Steam (a copy of sleep named steam) and a stand-in game on it, and stop only what they started.
/// </summary>
public sealed class DisplayTheoryAttribute : TheoryAttribute
{
    public DisplayTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Runs on Linux";
        else if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_DISPLAY") is null or "") Skip = "Set VALHEIM_TESTING_DISPLAY to a local X display";
    }
}

// The start through real shells: refusals wherever CI runs, a Linux client on a real X display (locally and over ssh to this
// machine) in the game-hosts job, and the Windows task in the runner's own desktop session when it has one.
public class InteractiveClientShellTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    internal static string FakeLinuxInstall(string root, string argumentsFile)
    {
        string install = Path.Combine(root, "valheim");
        foreach (string file in new[] { "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.dll", "doorstop_libs/libdoorstop_x64.so" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, file))!);
            File.WriteAllText(Path.Combine(install, file), "");
        }
        // The stand-in game records its arguments, then becomes a long sleep: the same process ID, environment and start time.
        string game = Path.Combine(install, "valheim.x86_64");
        File.WriteAllText(game, "#!/bin/sh\nprintf '%s\\n' \"$@\" > '" + argumentsFile + "'\nexec sleep 600\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(game, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return install;
    }

    [OsFact("linux")] public async Task ALinuxHostChecksTheInstallThenRefusesAMissingDisplay()
    {
        using var root = new TempDirectory();
        string install = FakeLinuxInstall(root.Path, Path.Combine(root.Path, "args.txt"));
        var host = new LocalGameHost("here", HostShell.Bash);
        var launch = HostClientLaunch.Create(ClientPlatform.Linux, install, []);
        string launchDirectory = Path.Combine(root.Path, "launch");

        var refused = await Assert.ThrowsAsync<InteractiveSessionException>(() => InteractiveClient.StartAsync(host, launch, launchDirectory, Generous, new LinuxDisplay(":87")));
        Assert.Equal(InteractiveRefusal.NoSession, refused.Reason); Assert.Contains(":87", refused.Message);
        Assert.False(Directory.Exists(launchDirectory));

        Directory.CreateDirectory(launchDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InteractiveClient.StartAsync(host, launch, launchDirectory, Generous, new LinuxDisplay(":87")));
        File.Delete(Path.Combine(install, "doorstop_libs", "libdoorstop_x64.so"));
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => InteractiveClient.StartAsync(host, launch, launchDirectory + "-2", Generous, new LinuxDisplay(":87")));
        Assert.Contains("libdoorstop_x64.so", missing.Message);
    }

    [OsFact("macos")] public async Task AMacHostRefusesALinuxClient()
    {
        using var root = new TempDirectory();
        var launch = HostClientLaunch.Create(ClientPlatform.Linux, FakeLinuxInstall(root.Path, Path.Combine(root.Path, "args.txt")), []);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            InteractiveClient.StartAsync(new LocalGameHost("here", HostShell.Bash), launch, Path.Combine(root.Path, "launch"), Generous));
    }

    /// <summary>
    /// The Windows start in this runner's session. With a desktop session (VALHEIM_TESTING_WINDOWS_DESKTOP=1 requires one) the
    /// task starts a stand-in game (a copy of ping named valheim.exe) next to a stand-in Steam, is removed, and stopping ends only
    /// that process. Without one, the refusal is all that can be checked.
    /// </summary>
    [OsFact("windows")] public async Task AWindowsClientStartsThroughATaskInTheRunnersDesktopSession()
    {
        using var root = new TempDirectory();
        string system = Environment.SystemDirectory, ping = Path.Combine(system, "PING.EXE");
        string install = Path.Combine(root.Path, "Valheim");
        foreach (string file in new[] { @"BepInEx\core\BepInEx.Preloader.dll", @"BepInEx\core\BepInEx.dll", "winhttp.dll", "doorstop_config.ini" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, file))!);
            File.WriteAllText(Path.Combine(install, file), "");
        }
        string game = CopyPing(ping, install, "valheim.exe");
        string variable = "VT_TEST_CANARY_" + Guid.NewGuid().ToString("N"), canary = "canary-" + Guid.NewGuid().ToString("N");
        var launch = HostClientLaunch.Create(ClientPlatform.Windows, install, ["-n", "600", "127.0.0.1"], null, [variable], console: false);
        var host = new LocalGameHost("here", HostShell.WindowsPowerShell);
        Environment.SetEnvironmentVariable(variable, canary);
        var started = new List<Process>();
        try
        {
            var refused = await Assert.ThrowsAsync<InteractiveSessionException>(() => InteractiveClient.StartAsync(host, launch, Path.Combine(root.Path, "launch-1"), Generous));
            // Without Steam anywhere, a desktop session shows as the missing Steam client, and no session as itself.
            bool desktop = refused.Reason == InteractiveRefusal.NoSteam;
            int session = Process.GetCurrentProcess().SessionId;
            if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_WINDOWS_DESKTOP") == "1")
                Assert.True(desktop && session != 0, $"This runner was expected to run in a desktop session (it runs in session {session}): {refused.Message}");
            // The stand-ins below start in this process's session, so only a runner inside the desktop session can go on.
            if (!desktop || session == 0) return;

            // A stand-in Steam and a bystander with the game's name, both in this session; stopping must leave the bystander alone.
            started.Add(Start(CopyPing(ping, Path.Combine(root.Path, "steam"), "steam.exe")));
            var bystander = Start(game);
            started.Add(bystander);
            var secretsBefore = Directory.GetFiles(Path.GetTempPath(), "vt-secrets-*").ToHashSet();

            string launchDirectory = Path.Combine(root.Path, "launch-2");
            var client = await InteractiveClient.StartAsync(host, launch, launchDirectory, Generous);
            using (var process = Process.GetProcessById(client.Id))
            {
                Assert.Equal("valheim", process.ProcessName, ignoreCase: true);
                Assert.Equal(process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), client.StartIdentity);
                Assert.Equal(process.SessionId, Process.GetCurrentProcess().SessionId);
            }
            Assert.NotEqual(0, Run(Path.Combine(system, "schtasks.exe"), "/Query", "/TN", client.TaskName!));
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "vt-secrets-*").Where(file => !secretsBefore.Contains(file)));
            foreach (string file in Directory.GetFiles(launchDirectory)) Assert.DoesNotContain(canary, File.ReadAllText(file));

            Assert.Equal(InteractiveStop.Stopped, await client.StopAsync(TimeSpan.FromSeconds(30)));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(client.Id).Dispose());
            Assert.False(bystander.HasExited);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            foreach (var process in started) { try { process.Kill(); } catch (InvalidOperationException) { } process.Dispose(); }
        }
    }

    private static string CopyPing(string ping, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, name);
        File.Copy(ping, target);
        // Its messages live in a language resource beside it; without one it still runs, silently.
        foreach (string culture in new[] { CultureInfo.CurrentUICulture.Name, "en-US" }.Distinct())
        {
            string mui = Path.Combine(Path.GetDirectoryName(ping)!, culture, "PING.EXE.mui");
            if (culture.Length == 0 || !File.Exists(mui)) continue;
            Directory.CreateDirectory(Path.Combine(directory, culture));
            File.Copy(mui, Path.Combine(directory, culture, name + ".mui"), overwrite: true);
        }
        return target;
    }

    private static Process Start(string executable) =>
        Process.Start(new ProcessStartInfo(executable, "-n 600 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;

    private static int Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}

[Trait("Category", "GameHosts")]
public class InteractiveClientDisplayTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
    public static TheoryData<string> Hosts
    {
        get
        {
            var hosts = new TheoryData<string> { "local" };
            if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION") is { Length: > 0 }) hosts.Add("ssh");
            return hosts;
        }
    }
    private static IGameHost Host(string kind) => kind == "local" ? new LocalGameHost("here", HostShell.Bash)
        : new SshGameHost("ssh-here", Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!, HostShell.Bash,
            int.TryParse(Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_PORT"), out int port) ? port : 0,
            (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    [DisplayTheory, MemberData(nameof(Hosts))] public async Task ALinuxClientStartsOnTheDisplayNextToSteamAndStopsAlone(string kind)
    {
        string display = Environment.GetEnvironmentVariable("VALHEIM_TESTING_DISPLAY")!;
        using var root = new TempDirectory();
        string argumentsFile = Path.Combine(root.Path, "args.txt");
        string install = InteractiveClientShellTests.FakeLinuxInstall(root.Path, argumentsFile);
        string variable = "VT_TEST_CANARY_" + Guid.NewGuid().ToString("N"), canary = "canary-" + Guid.NewGuid().ToString("N");
        var launch = HostClientLaunch.Create(ClientPlatform.Linux, install, ["+name", "it's $HOME"], new Dictionary<string, string> { ["VT_VISIBLE"] = "plain value" }, [variable]);
        var host = Host(kind);
        var started = new List<Process>();
        InteractiveClientProcess? client = null;
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            var refused = await Assert.ThrowsAsync<InteractiveSessionException>(() =>
                InteractiveClient.StartAsync(host, launch, Path.Combine(root.Path, "launch-1"), Generous, new LinuxDisplay(display)));
            Assert.Equal(InteractiveRefusal.NoSteam, refused.Reason);

            // A stand-in Steam on the display: a copy of sleep, whose process name is then steam.
            string steam = Path.Combine(root.Path, "steam");
            File.Copy("/bin/sleep", steam);
            File.SetUnixFileMode(steam, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var steamStart = new ProcessStartInfo(steam, "600") { UseShellExecute = false };
            steamStart.Environment["DISPLAY"] = display;
            started.Add(Process.Start(steamStart)!);
            var bystander = Process.Start(new ProcessStartInfo("sleep", "600") { UseShellExecute = false })!;
            started.Add(bystander);

            string launchDirectory = Path.Combine(root.Path, "launch-2");
            client = await InteractiveClient.StartAsync(host, launch, launchDirectory, Generous, new LinuxDisplay(display));
            await WaitForFileAsync(argumentsFile);
            Assert.Equal("-console\n+name\nit's $HOME\n", File.ReadAllText(argumentsFile));
            string environment = File.ReadAllText($"/proc/{client.Id}/environ").Replace('\0', '\n');
            Assert.Contains("\nDISPLAY=" + display + "\n", "\n" + environment);
            Assert.Contains("\nDOORSTOP_ENABLED=1\n", "\n" + environment);
            Assert.Contains("\nDOORSTOP_TARGET_ASSEMBLY=" + install + "/BepInEx/core/BepInEx.Preloader.dll\n", "\n" + environment);
            Assert.Contains("\nLD_LIBRARY_PATH=" + install + "/doorstop_libs", "\n" + environment);
            Assert.Contains("\nLD_PRELOAD=libdoorstop_x64.so", "\n" + environment);
            Assert.Contains("\nSteamAppId=892970\n", "\n" + environment);
            Assert.Contains("\nVT_VISIBLE=plain value\n", "\n" + environment);
            // The secret reached the game's environment, and nothing else anyone can read: no command line, no evidence file.
            Assert.Contains("\n" + variable + "=" + canary + "\n", "\n" + environment);
            foreach (string cmdline in Directory.GetDirectories("/proc").Where(path => Path.GetFileName(path).All(char.IsAsciiDigit)).Select(path => Path.Combine(path, "cmdline")))
            {
                string text;
                try { text = File.ReadAllText(cmdline); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                Assert.DoesNotContain(canary, text);
            }
            foreach (string file in Directory.GetFiles(launchDirectory)) Assert.DoesNotContain(canary, File.ReadAllText(file));
            Assert.True(File.Exists(Path.Combine(launchDirectory, "spec.txt")));

            Assert.Equal(InteractiveStop.Stopped, await client.StopAsync(TimeSpan.FromSeconds(30)));
            // The recorder saw the kill.
            using (var wait = new CancellationTokenSource(Generous)) Assert.Equal(137, await client.WaitForExitAsync(wait.Token));
            Assert.False(bystander.HasExited);
            Assert.False(started[0].HasExited);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            foreach (var process in started) { try { process.Kill(); } catch (InvalidOperationException) { } process.Dispose(); }
            // A failed check leaves no stand-in game behind: the same identity-checked stop, through the local shell, which is always reachable.
            if (client != null)
                await new InteractiveClientProcess(new LocalGameHost("cleanup", HostShell.Bash), ClientPlatform.Linux, client.Id, client.StartIdentity, client.LaunchDirectory, null)
                    .StopAsync(TimeSpan.FromSeconds(30));
        }
    }

    // The stand-in game writes the file just before it execs sleep; its creation is the event awaited.
    private static async Task WaitForFileAsync(string path)
    {
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { EnableRaisingEvents = true };
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => changed.TrySetResult();
        watcher.Changed += (_, _) => changed.TrySetResult();
        if (!File.Exists(path) || new FileInfo(path).Length == 0) await changed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // A write may still be under way when the event arrives.
        for (int i = 0; i < 50 && (!File.Exists(path) || !File.ReadAllText(path).EndsWith('\n')); i++) await Task.Delay(20);
    }

    [Fact] public void TheDisplayIsConfiguredWhereHostsAreRequired()
    {
        if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_REQUIRE_HOSTS") != "1") return;
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VALHEIM_TESTING_DISPLAY")), "VALHEIM_TESTING_DISPLAY is not set");
    }
}

// The wrapper's hand-off of secrets through real shells: the script sees them in VT_SECRETS, and no file in the wrapper's own
// temporary directory (the composed script and the upload) holds them while the script runs. The negative control passes the
// same value as an ordinary variable, which the same scan finds in the script file.
internal static class SecretHandOffChecks
{
    private const string Bash = """
        set -u
        secrets=${VT_SECRETS:-}; unset VT_SECRETS
        dir=$(dirname -- "$0")
        needles=()
        for t in $secrets; do v=$(printf %s "$t" | base64 -d); printf 'got %s\n' "$v"; needles+=("$t" "$v"); done
        if [ -n "${needle:-}" ]; then needles+=("$needle"); fi
        for f in "$dir"/*; do
            for n in ${needles[@]+"${needles[@]}"}; do if grep -qF -- "$n" "$f"; then printf 'leaked %s\n' "$(basename -- "$f")"; fi; done
        done
        if env | grep -q '^VT_SECRETS='; then echo still-set; fi
        """;
    private const string PowerShell = """
        $secrets = [Environment]::GetEnvironmentVariable('VT_SECRETS')
        [Environment]::SetEnvironmentVariable('VT_SECRETS', $null)
        $dir = Split-Path -Parent $PSCommandPath
        $needles = @()
        foreach ($t in ("$secrets" -split ' ')) { if ($t) { $v = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($t)); 'got ' + $v; $needles += $t; $needles += $v } }
        if ($needle) { $needles += $needle }
        foreach ($f in [IO.Directory]::GetFiles($dir)) {
            $text = [IO.File]::ReadAllText($f)
            foreach ($n in $needles) { if ($text.Contains($n)) { 'leaked ' + [IO.Path]::GetFileName($f) } }
        }
        if ([Environment]::GetEnvironmentVariable('VT_SECRETS')) { 'still-set' }
        """;

    public static async Task SecretsReachTheScriptButNoFile(ScriptedGameHost host)
    {
        string canary = "canary-" + Guid.NewGuid().ToString("N");
        string token = InteractiveClientTests.Base64("VT_JOIN_PASSWORD=" + canary);
        string script = host.Shell.Kind == HostShellKind.Bash ? Bash : PowerShell;
        var result = (await host.RunWithSecretsAsync(script, null, [token, InteractiveClientTests.Base64("SECOND=two words")], GameHostChecks.Generous)).EnsureSuccess("Handing over secrets");
        Assert.Contains("got VT_JOIN_PASSWORD=" + canary + "\n", result.Stdout);
        Assert.Contains("got SECOND=two words\n", result.Stdout);
        Assert.DoesNotContain("leaked", result.Stdout);
        Assert.DoesNotContain("still-set", result.Stdout);

        var control = (await host.RunWithSecretsAsync(script, new Dictionary<string, string> { ["needle"] = canary }, [], GameHostChecks.Generous)).EnsureSuccess("Control");
        Assert.Contains("leaked script", control.Stdout);
    }
}

public class SecretHandOffTests
{
    [Fact] public async Task ASecretIsOneBase64TokenOnItsOwnLine()
    {
        var fake = new FakeLauncher().Exits(0, "", FakeLauncher.Report(0));
        var host = new LocalGameHost("here", HostShell.Bash, fake);
        await Assert.ThrowsAsync<ArgumentException>(() => host.RunWithSecretsAsync("true", null, ["not base64 \n"], TimeSpan.FromSeconds(10)));
        Assert.Empty(fake.Calls);
        await host.RunWithSecretsAsync("true", null, ["QUI9Yw==", "Qz1k"], TimeSpan.FromSeconds(10));
        Assert.Equal("QUI9Yw== Qz1k", FakeLauncher.Secrets(fake.Calls[0]));
        Assert.DoesNotContain("QUI9Yw==", FakeLauncher.Script(fake.Calls[0]));
        await host.RunAsync("true", null, TimeSpan.FromSeconds(10));
        Assert.Equal("", FakeLauncher.Secrets(fake.Calls[1]));
        await Assert.ThrowsAsync<ArgumentException>(() => host.RunAsync("true", new Dictionary<string, string> { ["VT_SECRETS"] = "x" }, TimeSpan.FromSeconds(10)));
    }

    public static TheoryData<string> Shells => LocalGameHostShellTests.Shells;
    [Theory, MemberData(nameof(Shells))] public Task SecretsReachTheScriptButNoFile(string shell) =>
        SecretHandOffChecks.SecretsReachTheScriptButNoFile(new LocalGameHost("local-" + shell, HostShell.Parse(shell)));
}

[Trait("Category", "GameHosts")]
public class RemoteSecretHandOffTests
{
    public static TheoryData<string> Shells => SshGameHostIntegrationTests.Shells;
    [SshTheory, MemberData(nameof(Shells))] public Task SecretsReachTheScriptButNoFileOverSsh(string shell) =>
        SecretHandOffChecks.SecretsReachTheScriptButNoFile(new SshGameHost("ssh-" + shell, Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!, HostShell.Parse(shell),
            int.TryParse(Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_PORT"), out int port) ? port : 0,
            (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    [ContainerTheory, InlineData("bash")] public Task SecretsReachTheScriptButNoFileInAContainer(string shell) =>
        SecretHandOffChecks.SecretsReachTheScriptButNoFile(new ContainerGameHost("ctr", Environment.GetEnvironmentVariable("VALHEIM_TESTING_CONTAINER")!, HostShell.Parse(shell)));
}
