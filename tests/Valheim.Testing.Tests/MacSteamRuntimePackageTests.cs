using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class MacSteamRuntimePackageContractTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-steam-contract-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ClosureVerificationHasOneHostScriptOwner()
    {
        string source = Path.Combine(FixtureProjects.RepositoryRoot(), "src");
        string package = File.ReadAllText(Path.Combine(source, "Valheim.Testing.Game", "MacSteamRuntimePackage.cs"));
        string onHost = File.ReadAllText(Path.Combine(source, "Valheim.Testing.GameSessions", "MacSteamRuntimeOnHost.cs"));
        string validate = package[package.IndexOf("public void Validate()", StringComparison.Ordinal)
            ..package.IndexOf("internal IReadOnlyDictionary<string, string> StageSignedFiles", StringComparison.Ordinal)];

        Assert.Contains("VerificationScript", validate);
        Assert.DoesNotContain("FileHash.Sha256", validate);
        Assert.DoesNotContain("/usr/bin/lipo", validate);
        Assert.Contains("MacSteamRuntimePackage.VerificationScript", onHost);
        Assert.DoesNotContain("/usr/bin/otool", onHost);
        Assert.DoesNotContain("internal static readonly string Verify", onHost);
    }

    [Fact] public void RemoteManifestSnapshotDoesNotInspectMacFilesOnItsWindowsRunner()
    {
        string folder = Directory.CreateTempSubdirectory("vt-steam-remote-").FullName;
        try
        {
            string manifest = Path.Combine(folder, "reviewed.json"), snapshot = Path.Combine(folder, "evidence.json");
            File.WriteAllText(manifest, """
                {"name":"SteamCMD","version":"123","root":"/remote/steamcmd","profileFormat":"thin-arm64-adhoc-sign-v1",
                 "sourceManifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                 "files":{"steamclient.dylib":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                          "crashhandler.dylib":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}}
                """);
            var package = MacSteamRuntimePackage.Snapshot(manifest, snapshot, remote: true);
            Assert.Equal("/remote/steamcmd", package.Root);
            Assert.Equal(File.ReadAllBytes(manifest), File.ReadAllBytes(snapshot));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AnSshMacProfileRunsExactlyTwoSharedRuntimeChecks()
    {
        var (host, package, source, selected) = Fixture("two-checks");
        string run = "/runs/two-checks";

        var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, source,
            run + "/runtime", run + "/staging", [new HostedRuntimeFile(selected, "BepInEx/plugins/selected.dll")],
            TimeSpan.FromSeconds(30), steamRuntimePackage: package);

        Assert.Equal(2, host.Runs.Count(item => item.Script == "steam-runtime-verify"));
        Assert.Single(host.Runs, item => item.Script == "steam-runtime-stage");
        Assert.Equal(package.Files.Keys.Order(StringComparer.Ordinal),
            prepared.Loader.Files.Keys.Where(path => path.StartsWith("steam-runtime/", StringComparison.Ordinal))
                .Select(path => path["steam-runtime/".Length..]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnSshMacProfileRefusesSourceBytesChangedBetweenFirstCheckAndCopy()
    {
        var (host, package, source, selected) = Fixture("changed-source");
        host.BeforeScript = script =>
        {
            if (script == "steam-runtime-stage") File.AppendAllText(host.Local(package.Root + "/steamclient.dylib"), "changed");
        };

        var error = await Assert.ThrowsAsync<HostOperationException>(() => HostedRuntimeStage.PrepareProfileAsync(host,
            HostedRuntimeKind.Server, source, "/runs/changed/runtime", "/runs/changed/staging",
            [new HostedRuntimeFile(selected, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(30),
            steamRuntimePackage: package));

        Assert.Equal(2, host.Runs.Count(item => item.Script == "steam-runtime-verify"));
        Assert.Equal("VT-STEAM-RUNTIME changed steamclient.dylib\n", error.Result.Stdout);
        Assert.False(Directory.Exists(host.Local("/runs/changed")));
    }

    [Fact]
    public async Task SharedRuntimeCheckRefusesDeclaredVersionMismatch()
    {
        var (host, package, _, _) = Fixture("version-mismatch");
        package.Version = "999";

        var error = await Assert.ThrowsAsync<HostOperationException>(() =>
            MacSteamRuntimeOnHost.VerifyAsync(host, package, TimeSpan.FromSeconds(30), default));

        Assert.Equal("VT-STEAM-RUNTIME version-changed\n", error.Result.Stdout);
        Assert.Equal("999", Assert.Single(host.Runs).Variables["expectedVersion"]);
    }

    private (FakeServerHost Host, MacSteamRuntimePackage Package, string Source, string Selected) Fixture(string name)
    {
        var host = new FakeServerHost("ssh-mac", Path.Combine(_root, name));
        const string source = "/games/valheim", steam = "/steamcmd";
        void HostFile(string path, string contents)
        {
            path = host.Local(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }
        HostFile(source + "/" + GameLaunch.ServerMacExecutable, "server");
        HostFile(source + "/valheim_server/Data/Managed/" + InstallPins.GameAssemblyName, "game");
        HostFile(source + "/BepInEx/core/BepInEx.dll", "core");
        HostFile(source + "/BepInEx/core/BepInEx.Preloader.dll", "preloader");
        HostFile(source + "/libdoorstop.dylib", "doorstop");
        HostFile(steam + "/package/steam_cmd_osx.manifest", "\"osx\"\n{\n  \"version\" \"123\"\n}\n");
        HostFile(steam + "/steamclient.dylib", "steam client");
        HostFile(steam + "/crashhandler.dylib", "crash handler");
        string manifest = host.Local(steam + "/package/steam_cmd_osx.manifest");
        var package = new MacSteamRuntimePackage
        {
            Name = "SteamCMD", Version = "123", Root = steam,
            SourceManifestSha256 = FileHash.Sha256(manifest), ProfileFormat = MacSteamRuntimePackage.SignedProfileFormat,
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["steamclient.dylib"] = FileHash.Sha256(host.Local(steam + "/steamclient.dylib")),
                ["crashhandler.dylib"] = FileHash.Sha256(host.Local(steam + "/crashhandler.dylib")),
            },
        };
        string selected = Path.Combine(_root, name + "-selected.dll");
        File.WriteAllText(selected, "selected");
        return (host, package, source, selected);
    }
}

[Trait("Os", "macos")]
public sealed class MacSteamRuntimePackageTests
{
    [Fact]
    public async Task PinsTheNativeClosureAndRefusesChangedOrMissingMembers()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string root = Path.Combine(Path.GetTempPath(), "vt-steam-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "package"));
        try
        {
            File.WriteAllText(Path.Combine(root, "package", "steam_cmd_osx.manifest"), "\"osx\"\n{\n  \"version\" \"123\"\n}\n");
            string source = Path.Combine(root, "native.c");
            File.WriteAllText(source, "int vt_native(void) { return 1; }\n");
            string clientSource = Path.Combine(root, "client.c");
            File.WriteAllText(clientSource, "extern int vt_native(void); int vt_client(void) { return vt_native(); }\n");
            Compile(root, source, "libtier0_s.dylib");
            Compile(root, clientSource, "steamclient.dylib", "-L" + root, "-ltier0_s");
            Compile(root, source, "crashhandler.dylib");

            var package = MacSteamRuntimePackage.Capture(root);
            Assert.Equal("123", package.Version);
            Assert.Equal(new[] { "crashhandler.dylib", "libtier0_s.dylib", "steamclient.dylib" }, package.Files.Keys.Order(StringComparer.Ordinal));
            package.Version = "999";
            Assert.Contains("VT-STEAM-RUNTIME version-changed", Assert.Throws<InvalidDataException>(package.Validate).Message);
            package.Version = "123";
            string manifest = Path.Combine(root, "reviewed.json");
            package.Write(manifest);
            _ = MacSteamRuntimePackage.Read(manifest);
            var remote = MacSteamRuntimePackage.ReadRemote(manifest);
            var listings = package.Files.Keys.ToDictionary(relative => relative,
                relative => Run("/usr/bin/otool", "-L", Path.Combine(root, relative)), StringComparer.Ordinal);
            remote.RequireRemoteClosure(listings);
            Assert.Contains("did not cover every pinned file", Assert.Throws<InvalidDataException>(() =>
                remote.RequireRemoteClosure(listings.Where(file => file.Key != "libtier0_s.dylib")
                    .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal))).Message);
            string profile = Path.Combine(root, "profile");
            var signed = package.StageSignedFiles(profile);
            Assert.Equal(package.Files.Keys.Order(StringComparer.Ordinal), signed.Keys.Order(StringComparer.Ordinal));
            foreach (var (relative, sha) in signed)
            {
                string path = Path.Combine(profile, relative);
                Assert.Equal(sha, FileHash.Sha256(path));
                Assert.Equal("arm64", Run("/usr/bin/lipo", "-archs", path));
                Assert.Equal(0, ExitCode("/usr/bin/codesign", "--verify", path));
                Assert.Equal(package.Files[relative], FileHash.Sha256(Path.Combine(root, relative)));
            }
            // The local server preparation path selects this reviewed package into steam-runtime/*. The real Bash apply
            // script must derive that permission from the selected payload kind and keep unrelated game targets refused.
            var host = new LocalGameHost("mac-shell", HostShell.Bash);
            string install = Path.Combine(root, "server");
            string executable = Path.Combine(install, "valheim_server", "Valheim");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "server");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string managed = Path.Combine(install, "valheim_server", "Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(managed, InstallPins.GameAssemblyName), "game");
            FakeInstalls.MacLoader(install);
            string core = Path.Combine(install, "BepInEx", "core");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "core");
            File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "preloader");
            string selected = Path.Combine(root, "selected.dll");
            File.WriteAllText(selected, "selected");
            string run = Path.Combine(root, "vt-prep-steam");
            var prepared = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Server, install,
                Path.Combine(run, "runtime"), Path.Combine(run, "staging"),
                [new HostedRuntimeFile(selected, "BepInEx/plugins/selected.dll")], TimeSpan.FromSeconds(60),
                steamRuntimePackage: package);
            Assert.Equal(FileHash.Sha256(Path.Combine(run, "runtime", "steam-runtime", "steamclient.dylib")),
                prepared.Loader.Files["steam-runtime/steamclient.dylib"]);
            Assert.False(Directory.Exists(Path.Combine(run, "staging")));
            string changedOutput = Path.Combine(run, "runtime", "steam-runtime", "libtier0_s.dylib");
            File.AppendAllText(changedOutput, "changed after staging");
            var beforeBoot = await Assert.ThrowsAsync<InvalidOperationException>(() => HostInstall.CheckProfilePinsAsync(host,
                prepared.Pins, prepared.GameRoot, prepared.LoaderRoot, prepared.SourceCheck, "prepared server profile before launch",
                TimeSpan.FromSeconds(60), default));
            Assert.Contains("prepared server profile before launch staged files", beforeBoot.Message);
            Assert.Contains("steam-runtime/libtier0_s.dylib", beforeBoot.Message);
            await HostedRuntimeStage.RetireAsync(host, prepared.LoaderRoot, Path.Combine(run, "staging"), TimeSpan.FromSeconds(60));

            await MacSteamRuntimeOnHost.VerifyAsync(host, remote, TimeSpan.FromSeconds(60), default);
            string sshProfile = Path.Combine(root, "ssh-profile");
            Directory.CreateDirectory(sshProfile);
            var sshSigned = await MacSteamRuntimeOnHost.StageAsync(host, remote, sshProfile, TimeSpan.FromSeconds(60), default);
            Assert.Equal(package.Files.Keys.Order(StringComparer.Ordinal), sshSigned.Keys.Order(StringComparer.Ordinal));
            foreach (var (relative, sha) in sshSigned)
            {
                Assert.Equal(sha, FileHash.Sha256(Path.Combine(sshProfile, "steam-runtime", relative)));
                Assert.Equal(package.Files[relative], FileHash.Sha256(Path.Combine(root, relative)));
            }

            string commandManifest = Path.Combine(root, "command.json");
            Assert.Equal(0, MacSteamRuntimeCommand.Run(["capture", "--steamcmd", root, "--output", commandManifest]));
            Assert.Equal(3, MacSteamRuntimeCommand.Run(["capture", "--steamcmd", root, "--output", commandManifest]));

            File.AppendAllText(Path.Combine(root, "libtier0_s.dylib"), "changed");
            Assert.Contains("libtier0_s.dylib", Assert.Throws<InvalidDataException>(package.Validate).Message);
            Compile(root, source, "libtier0_s.dylib");
            File.Delete(Path.Combine(root, "crashhandler.dylib"));
            Assert.Contains("crashhandler.dylib", Assert.Throws<InvalidDataException>(package.Validate).Message);

            Compile(root, source, "crashhandler.dylib");
            Assert.Equal(0, ExitCode("/usr/bin/install_name_tool", "-id",
                "/private/tmp/unreviewed-valheim-test-library.dylib", Path.Combine(root, "libtier0_s.dylib")));
            Compile(root, clientSource, "steamclient.dylib", "-L" + root, "-ltier0_s");
            Assert.Contains("outside macOS system libraries", Assert.Throws<InvalidDataException>(() =>
                MacSteamRuntimePackage.Capture(root)).Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Run(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }

    private static int ExitCode(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void Compile(string root, string source, string name, params string[] extra)
    {
        var info = new ProcessStartInfo("/usr/bin/clang") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-dynamiclib", "-arch", "arm64", "-install_name", "@loader_path/" + name,
            "-o", Path.Combine(root, name), source }.Concat(extra)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
