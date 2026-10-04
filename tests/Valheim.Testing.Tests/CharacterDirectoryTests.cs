using Valheim.Testing.Game;
using Xunit;

// A campaign character's characters_local and Steam userdata folders, left out of the manifest, resolved on the client host from
// its platform's standard paths: the real scripts through this machine's shells, against a temporary home.
public sealed class CharacterDirectoryTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly TempDirectory _home = new();
    public void Dispose() => _home.Dispose();

    private static HostedCampaignCharacter Character(string characters = "", string userdata = "") => new()
    {
        Store = "store", RegisteredName = "tester", FileName = "vt-player", CharactersLocalDirectory = characters, SteamUserDataDirectory = userdata,
    };

    private string Make(string relative)
    {
        string path = Path.Combine([_home.Path, .. relative.Split('/')]);
        Directory.CreateDirectory(path);
        return path;
    }

    private Task<CharacterDirectories> Resolve(string shell, string platform, HostedCampaignCharacter character, string steamRoot = "") =>
        HostedCharacterStage.ResolveDirectoriesAsync(new LocalGameHost("client", HostShell.Parse(shell)), platform, character, Timeout, default, _home.Path, steamRoot);

    private static string Standard(string platform) => platform switch
    {
        "windows" => "AppData/LocalLow/IronGate/Valheim/characters_local",
        "macos" => "Library/Application Support/IronGate/Valheim/characters_local",
        _ => ".config/unity3d/IronGate/Valheim/characters_local",
    };

    // Every platform's search through every shell this machine has: Windows PowerShell or pwsh for a Windows host, bash or pwsh
    // for Linux and macOS.
    public static TheoryData<string, string> Platforms
    {
        get
        {
            var rows = new TheoryData<string, string>();
            foreach (var shell in LocalGameHostShellTests.Shells)
            {
                string name = (string)shell[0];
                // The Windows search only on Windows: it turns Steam's forward slashes into the platform's backslashes.
                if (name != "bash" && OperatingSystem.IsWindows()) rows.Add(name, "windows");
                if (OperatingSystem.IsWindows() && name == "powershell") continue; // Windows PowerShell runs only the Windows search here.
                rows.Add(name, "linux");
                rows.Add(name, "macos");
            }
            return rows;
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public async Task LeftOutFoldersResolveToThePlatformsStandardPaths(string shell, string platform)
    {
        string characters = Make(Standard(platform));
        // Linux: the second standard place, so the search order is exercised. Windows: Steam's registered SteamPath, written with
        // forward slashes as Steam writes it (the registry value itself is this machine's, so the test passes it as steamroot).
        string steam = Make("Program Files/Steam");
        string userdata = platform switch
        {
            "macos" => Make("Library/Application Support/Steam/userdata"), "linux" => Make(".steam/steam/userdata"), _ => Make("Program Files/Steam/userdata"),
        };
        var resolved = await Resolve(shell, platform, Character(), platform == "windows" ? steam.Replace('\\', '/') : "");
        Assert.Null(resolved.Missing);
        Assert.Equal(Path.GetFullPath(characters), Path.GetFullPath(resolved.Characters!));
        Assert.Equal(Path.GetFullPath(userdata), Path.GetFullPath(resolved.UserData!));
    }

    // A Flatpak Steam's game keeps its saves inside the Flatpak's own home: the characters_local beside the userdata found.
    [Theory, MemberData(nameof(Platforms))]
    public async Task AFlatpakSteamsCharactersAreItsOwn(string shell, string platform)
    {
        if (platform != "linux") return;
        Make(".config/unity3d/IronGate/Valheim/characters_local"); // A non-Flatpak leftover that must not win.
        string userdata = Make(".var/app/com.valvesoftware.Steam/.local/share/Steam/userdata");
        string characters = Make(".var/app/com.valvesoftware.Steam/.config/unity3d/IronGate/Valheim/characters_local");
        var resolved = await Resolve(shell, platform, Character());
        Assert.Null(resolved.Missing);
        Assert.Equal((Path.GetFullPath(characters), Path.GetFullPath(userdata)), (Path.GetFullPath(resolved.Characters!), Path.GetFullPath(resolved.UserData!)));
    }

    [Theory, MemberData(nameof(Platforms))]
    public async Task AMissingFolderIsReportedWithEveryPathTried(string shell, string platform)
    {
        var resolved = await Resolve(shell, platform, Character(), platform == "windows" ? Path.Combine(_home.Path, "no-steam").Replace('\\', '/') : "");
        Assert.NotNull(resolved.Missing);
        Assert.Null(resolved.Characters); Assert.Null(resolved.UserData); // Nothing missing is reported as resolved.
        Assert.Contains("characters_local (tried " + _home.Path, resolved.Missing);
        Assert.Contains("characters_local", resolved.Missing);
        Assert.Contains("Steam userdata (tried ", resolved.Missing);
        if (platform == "linux")
            foreach (string tried in new[] { ".local/share/Steam/userdata", ".steam/steam/userdata", ".var/app/com.valvesoftware.Steam/.local/share/Steam/userdata" })
                Assert.Contains(_home.Path + "/" + tried, resolved.Missing);
    }

    [Theory, MemberData(nameof(Platforms))]
    public async Task GivenFoldersAreCheckedNotReplaced(string shell, string platform)
    {
        string characters = Make("elsewhere/characters_local"), userdata = Make("elsewhere/userdata");
        Make(Standard(platform)); // The platform's standard folder, which must not win over the given one.
        var resolved = await Resolve(shell, platform, Character(characters, userdata));
        Assert.Equal((characters, userdata, (string?)null), (resolved.Characters, resolved.UserData, resolved.Missing));
        // A given folder of the wrong shape is refused before anything is staged into it.
        string steam = Make("elsewhere/Steam");
        Assert.Throws<ArgumentException>(() => Resolve(shell, platform, Character(characters, steam)).GetAwaiter().GetResult());
        Directory.Delete(characters);
        Assert.Contains("characters_local (tried " + characters + ")", (await Resolve(shell, platform, Character(characters, userdata))).Missing);
    }
}
