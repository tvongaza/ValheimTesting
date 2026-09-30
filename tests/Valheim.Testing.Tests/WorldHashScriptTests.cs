using Xunit;

/// <summary>
/// Runs tools/dev-loop/world-hash.sh (macOS/Linux) and its twin world-hash.ps1 (Windows PowerShell) on a fixture folder
/// against an inert valheim-cli; no game. Each case runs on the OS of its script and is reported as skipped elsewhere.
/// </summary>
public sealed class WorldHashScriptTests : IDisposable
{
    // md5 of "B.txt:<md5>\nDev.db:<md5>\nDev.fwl:<md5>\na.b:<md5>\na/nested/deep.txt:<md5>" for the files below, worked out
    // by hand from the recipe (ValheimCLI's docs/expectations.md, "World files hash"): ordinal order puts "B" before "D"
    // before "a", and "a.b" before "a/" ('.' is 0x2E, '/' 0x2F); the empty file's md5 is d41d8cd9...
    private const string KnownVector = "5f4699a22003d6ebbf66e2e89cb659d3";

    private readonly DevLoopScripts _scripts = new();

    public void Dispose() => _scripts.Dispose();

    /// <summary>A world folder with nested files, an empty file and names whose byte order differs from a culture's.</summary>
    private string Fixture(string name = "Dev")
    {
        string world = _scripts.Folder("saves/" + name);
        Directory.CreateDirectory(Path.Combine(world, "a", "nested"));
        File.WriteAllBytes(Path.Combine(world, "Dev.db"), "db bytes\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(world, "Dev.fwl"), "fwl"u8.ToArray());
        File.WriteAllBytes(Path.Combine(world, "a", "nested", "deep.txt"), "deep"u8.ToArray());
        File.WriteAllBytes(Path.Combine(world, "a.b"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(world, "B.txt"), "upper"u8.ToArray());
        return world;
    }

    private static string Script(bool powerShell) => powerShell ? "world-hash.ps1" : "world-hash.sh";

    /// <summary>A fake valheim-cli that records its arguments and prints FAKE_REPLY with exit code FAKE_EXIT.</summary>
    private string FakeCli(bool powerShell)
    {
        if (powerShell)
            return _scripts.WindowsTool("valheim-cli",
                "[IO.File]::WriteAllLines($env:CAPTURE, [string[]]$args)\r\nWrite-Output $env:FAKE_REPLY\r\nexit [int]$env:FAKE_EXIT\r\n");
        _scripts.Folder("bin");
        return _scripts.Tool("bin/valheim-cli", "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$CAPTURE\"\nprintf '%s\\n' \"$FAKE_REPLY\"\nexit \"${FAKE_EXIT:-0}\"\n");
    }

    private Task<ScriptRun> Run(bool powerShell, string[] args, string reply = "", int exit = 0, string? saves = null) =>
        _scripts.Run(Script(powerShell), args, new Dictionary<string, string?>
        {
            ["VALHEIM_CLI"] = FakeCli(powerShell),
            ["VALHEIM_CLI_PORT"] = "5601",
            ["VALHEIM_SAVES"] = saves ?? _scripts.PathOf("saves"),
            ["CAPTURE"] = _scripts.PathOf("args.txt"),
            ["FAKE_REPLY"] = reply,
            ["FAKE_EXIT"] = exit.ToString(),
        });

    private static string World(string files, string hashed = "at_load") =>
        $"WORLD name=Dev seed=AbCdEfGh12 uid=-42 worldgen=2 files={files} files_hashed={hashed} dir=/saves/worlds_local/Dev/";

    private async Task HashesTheKnownVector(bool powerShell)
    {
        string world = Fixture();
        // The game's own implementation, compiled into Valheim.Testing.Cli, agrees with the hand-worked vector.
        Assert.Equal(KnownVector, valheimCLI.Expectations.HashDirectory(world));

        foreach (string target in new[] { world, "Dev" })
        {
            ScriptRun run = await Run(powerShell, new[] { target });
            run.AssertExit(0);
            string line = Assert.Single(DevLoopScripts.Lines(run.Stdout));
            Assert.Equal(KnownVector, line.Split("  ")[0]);
            Assert.False(File.Exists(_scripts.PathOf("args.txt")), "hashing alone must not ask the game");
        }
    }

    private async Task CompareAgreesWithTheGame(bool powerShell)
    {
        Fixture();
        ScriptRun run = await Run(powerShell, new[] { "Dev", "--compare" }, World(KnownVector));
        run.AssertExit(0);
        Assert.Contains("OK: the game loaded this saved state", run.Stdout);
        Assert.Equal(new[] { "--port", "5601", "cli_world" }, DevLoopScripts.Lines(File.ReadAllText(_scripts.PathOf("args.txt"))));
    }

    private async Task CompareRefusesAnotherHash(bool powerShell)
    {
        Fixture();
        ScriptRun run = await Run(powerShell, new[] { "Dev", "--compare" }, World("aaaabbbbccccdddd0000111122223333"));
        run.AssertExit(1);
        Assert.Contains("MISMATCH: the game loaded aaaabbbbccccdddd0000111122223333", run.Stderr);
        Assert.DoesNotContain("OK:", run.Stdout);
    }

    private async Task CompareRefusesAClientWithoutFiles(bool powerShell)
    {
        Fixture();
        ScriptRun run = await Run(powerShell, new[] { "Dev", "--compare" }, World("-", "client_has_no_files"));
        run.AssertExit(1);
        Assert.Contains("no load-time hash", run.Stderr);
    }

    private async Task CompareKeepsTheCliExitCode(bool powerShell)
    {
        Fixture();
        ScriptRun run = await Run(powerShell, new[] { "Dev", "--compare" }, "ERROR: could not connect", exit: 3);
        run.AssertExit(3);
        Assert.DoesNotContain("OK:", run.Stdout);
    }

    private async Task RefusesAMissingFolderAndUnknownArguments(bool powerShell)
    {
        Fixture();
        (await Run(powerShell, new[] { "NoSuchWorld" })).AssertExit(4);
        (await Run(powerShell, new[] { "Dev", "--compair" })).AssertExit(4);
        (await Run(powerShell, Array.Empty<string>())).AssertExit(4);
        Assert.False(File.Exists(_scripts.PathOf("args.txt")));
    }

    [UnixFact] public Task BashHashesTheKnownVector() => HashesTheKnownVector(false);
    [UnixFact] public Task BashCompareAgreesWithTheGame() => CompareAgreesWithTheGame(false);
    [UnixFact] public Task BashCompareRefusesAnotherHash() => CompareRefusesAnotherHash(false);
    [UnixFact] public Task BashCompareRefusesAClientWithoutFiles() => CompareRefusesAClientWithoutFiles(false);
    [UnixFact] public Task BashCompareKeepsTheCliExitCode() => CompareKeepsTheCliExitCode(false);
    [UnixFact] public Task BashRefusesAMissingFolderAndUnknownArguments() => RefusesAMissingFolderAndUnknownArguments(false);

    [WindowsFact] public Task PowerShellHashesTheKnownVector() => HashesTheKnownVector(true);
    [WindowsFact] public Task PowerShellCompareAgreesWithTheGame() => CompareAgreesWithTheGame(true);
    [WindowsFact] public Task PowerShellCompareRefusesAnotherHash() => CompareRefusesAnotherHash(true);
    [WindowsFact] public Task PowerShellCompareRefusesAClientWithoutFiles() => CompareRefusesAClientWithoutFiles(true);
    [WindowsFact] public Task PowerShellCompareKeepsTheCliExitCode() => CompareKeepsTheCliExitCode(true);
    [WindowsFact] public Task PowerShellRefusesAMissingFolderAndUnknownArguments() => RefusesAMissingFolderAndUnknownArguments(true);
}
