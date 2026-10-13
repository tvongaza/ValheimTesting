using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class MacSteamRuntimePackageTests
{
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
            Assert.Contains("libtier0_s.dylib", Assert.Throws<InvalidDataException>(() => MacSteamRuntimePackage.Read(manifest)).Message);
            string refusedProfile = Path.Combine(root, "refused-profile");
            Directory.CreateDirectory(refusedProfile);
            await Assert.ThrowsAnyAsync<Exception>(() => MacSteamRuntimeOnHost.StageAsync(host, remote, refusedProfile, TimeSpan.FromSeconds(60), default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(refusedProfile));
            Compile(root, source, "libtier0_s.dylib");
            File.Delete(Path.Combine(root, "crashhandler.dylib"));
            Assert.Contains("crashhandler.dylib", Assert.Throws<FileNotFoundException>(() => MacSteamRuntimePackage.Read(manifest)).Message);
            string missingProfile = Path.Combine(root, "missing-profile");
            Directory.CreateDirectory(missingProfile);
            await Assert.ThrowsAnyAsync<Exception>(() => MacSteamRuntimeOnHost.StageAsync(host, remote, missingProfile, TimeSpan.FromSeconds(60), default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(missingProfile));

            Compile(root, source, "crashhandler.dylib");
            Assert.Equal(0, ExitCode("/usr/bin/install_name_tool", "-id",
                "/private/tmp/unpinned-valheim-test-library.dylib", Path.Combine(root, "libtier0_s.dylib")));
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
