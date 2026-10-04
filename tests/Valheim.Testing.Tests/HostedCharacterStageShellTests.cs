using Valheim.Testing.Game;
using Xunit;

// The hosted character stage's own scripts in this machine's real shells (bash on macOS and Linux, Windows PowerShell on
// Windows, pwsh where installed), checked against the one character-file rule they get as variables
// (DisposableCharacterStore.IsCharacterFile): a fake host cannot show that a script applies the rule the C# path applies.
public sealed class HostedCharacterStageShellTests : IDisposable
{
    private const string Fresh = "fresh";
    private readonly string _root = Directory.CreateTempSubdirectory("vt-character-shell-").FullName;
    private readonly string _local, _cloud, _account, _userdata, _stage;

    public HostedCharacterStageShellTests()
    {
        string data = Path.Combine(_root, "Valheim");
        _local = Path.Combine(data, "characters_local");
        _cloud = Path.Combine(data, "characters");
        _userdata = Path.Combine(_root, "userdata");
        _account = Path.Combine(_userdata, "12345", "892970", "remote", "characters");
        _stage = Path.Combine(_root, "stage");
        foreach (string directory in new[] { _local, _cloud, _account, _stage }) Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(_stage, Fresh + ".fch"), "the registered character");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static TheoryData<string, string, string> OwnedNames()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string shell in LocalGameHostShellTests.Shells.Select(row => (string)row[0]))
            foreach (string folder in new[] { "local", "cloud", "account" })
                foreach (string name in new[] { "FRESH.fch", "fresh.fch.OLD", "Fresh.fch.new", "Fresh_backup_auto-20261004120000.fch" })
                    data.Add(shell, folder, name);
        return data;
    }

    private static readonly string[] OtherNames = ["fresh2.fch", "afresh.fch", "fresh.fch.bak", "fresh_backup.fch", "fresh.fch.old.txt", "fresh.fch.newer"];

    private HostedCampaignCharacter Character() => new()
    {
        Store = "unused", RegisteredName = "tester", FileName = Fresh, CharactersLocalDirectory = _local, SteamUserDataDirectory = _userdata,
    };

    private async Task<string> Run(string shell, bool install)
    {
        var host = new LocalGameHost("local-" + shell, HostShell.Parse(shell));
        bool powerShell = host.Shell.Kind == HostShellKind.PowerShell;
        string script = install ? (powerShell ? HostedCharacterStage.WindowsInstall : HostedCharacterStage.BashInstall)
            : (powerShell ? HostedCharacterStage.WindowsRetire : HostedCharacterStage.BashRetire);
        var result = await host.RunAsync(script, HostedCharacterStage.Variables(Character(), _stage), GameHostChecks.Generous);
        Assert.True(result.Succeeded, result.Describe());
        Assert.Empty(result.Stderr);
        return result.Stdout;
    }

    private string Folder(string folder) => folder switch { "local" => _local, "cloud" => _cloud, _ => _account };

    [Theory, MemberData(nameof(OwnedNames))]
    public async Task InstallRefusesAFileTheCharacterOwnsInAnyFolderTheGameReads(string shell, string folder, string existing)
    {
        Assert.True(DisposableCharacterStore.IsCharacterFile(existing, Fresh));
        File.WriteAllText(Path.Combine(Folder(folder), existing), "someone else's");
        Assert.Contains("VT-CHAR collision", await Run(shell, install: true));
        Assert.Equal(new[] { existing }, Directory.EnumerateFiles(Folder(folder)).Select(Path.GetFileName));
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(Folder(folder), existing)));
    }

    [Theory, MemberData(nameof(LocalGameHostShellTests.Shells), MemberType = typeof(LocalGameHostShellTests))]
    public async Task InstallStagesBesideFilesTheCharacterDoesNotOwnAndRetireRemovesExactlyItsOwn(string shell)
    {
        // Negative control for the refusals above: the same folders with names the rule does not match.
        foreach (string directory in new[] { _local, _cloud, _account })
            foreach (string name in OtherNames)
            {
                Assert.False(DisposableCharacterStore.IsCharacterFile(name, Fresh));
                File.WriteAllText(Path.Combine(directory, name), "not the run's");
            }
        Assert.Contains("VT-CHAR staged", await Run(shell, install: true));
        Assert.Equal("the registered character", File.ReadAllText(Path.Combine(_local, Fresh + ".fch")));

        // What the game writes for the character while it plays, then the retire after the client stopped.
        File.WriteAllText(Path.Combine(_local, "fresh.fch.old"), "previous");
        File.WriteAllText(Path.Combine(_local, "fresh_backup_auto-20261004120000.fch"), "backup");
        File.WriteAllText(Path.Combine(_local, "fresh.fch.new"), "a save the stop interrupted");
        Assert.Contains("VT-CHAR-RETIRED", await Run(shell, install: false));
        Assert.Equal(OtherNames.Order(StringComparer.Ordinal), Directory.EnumerateFiles(_local).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // Retire touches characters_local only: a cloud folder is never the run's.
        Assert.Equal(OtherNames.Length, Directory.EnumerateFiles(_cloud).Count());
    }
}
