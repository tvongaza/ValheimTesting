using Valheim.Testing.Game;
using Xunit;

// The retire script itself, in bash on this machine (Linux only: a hosted server's host runs Linux, with GNU stat and du).
public sealed class HostedRetireScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hosted-retire-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact] public async Task ItKeepsListedFilesWithinTheLimitsAndRemovesOnlyTheRunsRuntime()
    {
        if (!OperatingSystem.IsLinux()) return;
        string run = Path.Combine(_root, "runs", "run-x"), runtime = Path.Combine(run, "runtime"), keep = Path.Combine(run, "runtime-changes");
        Directory.CreateDirectory(Path.Combine(runtime, "sub"));
        File.WriteAllText(Path.Combine(runtime, "a.txt"), "kept");
        File.WriteAllText(Path.Combine(runtime, "sub", "b.cfg"), "kept too");
        File.WriteAllBytes(Path.Combine(runtime, "big.bin"), new byte[2000]);
        File.WriteAllText(Path.Combine(run, "outside.txt"), "never copied through ..");
        string elsewhere = Path.Combine(_root, "elsewhere"); Directory.CreateDirectory(elsewhere); File.WriteAllText(Path.Combine(elsewhere, "secret.txt"), "outside the copy");
        Directory.CreateSymbolicLink(Path.Combine(runtime, "linked"), elsewhere);
        var host = new LocalGameHost("local-bash", HostShell.Bash);
        var variables = new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["keep"] = keep, ["run"] = "run-x", ["perfile"] = "1000", ["total"] = "5000",
            ["files"] = string.Join('\n', new[] { "a.txt", "sub/b.cfg", "big.bin", "../outside.txt", "missing.txt", "linked/secret.txt" }.Select(B64)),
        };
        var result = (await host.RunAsync(HostedRunScripts.Retire, variables, GameHostChecks.Generous)).EnsureSuccess("retire");
        Assert.Contains("VT-RETIRED ", result.Stdout);
        Assert.False(Directory.Exists(runtime));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(keep, "a.txt")));
        Assert.Equal("kept too", File.ReadAllText(Path.Combine(keep, "sub", "b.cfg")));
        Assert.False(File.Exists(Path.Combine(keep, "big.bin")));
        Assert.Contains($"VT-NOTKEPT {B64("big.bin")} 2000", result.Stdout);
        Assert.Contains($"VT-NOTKEPT {B64("../outside.txt")} -1", result.Stdout);
        Assert.Contains($"VT-NOTKEPT {B64("missing.txt")} -1", result.Stdout);
        Assert.Contains($"VT-NOTKEPT {B64("linked/secret.txt")} -1", result.Stdout); // through a linked directory: not the copy's
        Assert.True(File.Exists(Path.Combine(elsewhere, "secret.txt"))); // removing the copy removed the link only
        Assert.True(File.Exists(Path.Combine(run, "outside.txt")));

        // Anything but <run>/runtime is refused and left alone.
        string other = Path.Combine(run, "world"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "w.db"), "a save");
        var refused = await host.RunAsync(HostedRunScripts.Retire, new Dictionary<string, string>(variables) { ["runtime"] = other, ["files"] = "" }, GameHostChecks.Generous);
        Assert.Equal(3, refused.ExitCode);
        Assert.True(File.Exists(Path.Combine(other, "w.db")));
        var wrongRun = await host.RunAsync(HostedRunScripts.Retire, new Dictionary<string, string>(variables) { ["run"] = "another-run", ["files"] = "" }, GameHostChecks.Generous);
        Assert.Equal(3, wrongRun.ExitCode);

        // The keep destination is as constrained as the directory being removed.
        string second = Path.Combine(run, "runtime"); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "a.txt"), "kept");
        string unrelated = Path.Combine(_root, "unrelated"); Directory.CreateDirectory(unrelated);
        var wrongKeep = await host.RunAsync(HostedRunScripts.Retire,
            new Dictionary<string, string>(variables) { ["keep"] = unrelated, ["files"] = B64("a.txt") }, GameHostChecks.Generous);
        Assert.Equal(3, wrongKeep.ExitCode);
        Assert.True(File.Exists(Path.Combine(second, "a.txt")));
        Assert.False(File.Exists(Path.Combine(unrelated, "a.txt")));

        var existingKeep = await host.RunAsync(HostedRunScripts.Retire,
            new Dictionary<string, string>(variables) { ["files"] = B64("a.txt") }, GameHostChecks.Generous);
        Assert.Equal(3, existingKeep.ExitCode);
        Assert.True(File.Exists(Path.Combine(second, "a.txt")));
        Directory.Delete(keep, recursive: true);
        Directory.CreateSymbolicLink(keep, unrelated);
        var linkedKeep = await host.RunAsync(HostedRunScripts.Retire,
            new Dictionary<string, string>(variables) { ["files"] = B64("a.txt") }, GameHostChecks.Generous);
        Assert.Equal(3, linkedKeep.ExitCode);
        Assert.True(File.Exists(Path.Combine(second, "a.txt")));
        Assert.False(File.Exists(Path.Combine(unrelated, "a.txt")));
    }
}
