using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;

public sealed class MacSteamRuntimePackageTests
{
    [Fact]
    public void PinsTheNativeClosureAndRefusesChangedOrMissingMembers()
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
            string commandManifest = Path.Combine(root, "command.json");
            Assert.Equal(0, MacSteamRuntimeCommand.Run(["capture", "--steamcmd", root, "--output", commandManifest]));
            Assert.Equal(3, MacSteamRuntimeCommand.Run(["capture", "--steamcmd", root, "--output", commandManifest]));

            File.AppendAllText(Path.Combine(root, "libtier0_s.dylib"), "changed");
            Assert.Contains("libtier0_s.dylib", Assert.Throws<InvalidDataException>(() => MacSteamRuntimePackage.Read(manifest)).Message);
            Compile(root, source, "libtier0_s.dylib");
            File.Delete(Path.Combine(root, "crashhandler.dylib"));
            Assert.Contains("crashhandler.dylib", Assert.Throws<FileNotFoundException>(() => MacSteamRuntimePackage.Read(manifest)).Message);
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
