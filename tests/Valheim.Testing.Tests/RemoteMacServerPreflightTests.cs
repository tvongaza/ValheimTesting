using System.Diagnostics;
using System.Runtime.InteropServices;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class RemoteMacServerPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("remote-mac-preflight-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task HostPreflightAcceptsArm64AndRefusesX64BeforeCreatingAProfile()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.OSArchitecture != Architecture.Arm64) return;
        string source = Path.Combine(_root, "source");
        string runtime = Path.Combine(_root, "profile");
        string game = Path.Combine(source, "valheim_server", "Valheim");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        string code = Path.Combine(_root, "server.c");
        File.WriteAllText(code, "int main(void) { return 0; }\n");
        var role = new GameRole { Install = source, Runtime = runtime };
        var host = new LocalGameHost("mac-preflight", HostShell.Bash);

        Compile("arm64");
        string armHash = FileHash.Sha256(game);
        await RemoteMacServerPreflight.InspectAsync(host, role, TimeSpan.FromSeconds(30), default);
        Assert.Equal(armHash, FileHash.Sha256(game));
        Assert.False(Directory.Exists(runtime));

        Compile("x86_64");
        string x64Hash = FileHash.Sha256(game);
        var error = await Assert.ThrowsAsync<HostOperationException>(() =>
            RemoteMacServerPreflight.InspectAsync(host, role, TimeSpan.FromSeconds(30), default));
        Assert.Contains("arm64", error.Message + error.Result.Stdout);
        Assert.Equal(x64Hash, FileHash.Sha256(game));
        Assert.False(Directory.Exists(runtime));

        void Compile(string architecture)
        {
            var start = new ProcessStartInfo("/usr/bin/clang")
            { RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { "-arch", architecture, "-o", game, code }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Could not compile the Mac preflight stand-in: " + stderr);
        }
    }
}
