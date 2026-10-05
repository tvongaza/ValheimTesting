using System.Runtime.CompilerServices;
using Valheim.Testing.Game;
using Xunit;

/// <summary>An in-memory machine for the inventory's this-machine default: a registry value, folders and files, any platform.</summary>
internal sealed class FakeMachine(string platform = "windows") : ISteamLocator
{
    public string Platform { get; } = platform;
    public string Home { get; init; } = platform == "windows" ? @"C:\Users\tester" : platform == "macos" ? "/Users/tester" : "/home/tester";
    public string DataRoot { get; init; } = platform == "windows" ? @"C:\Users\tester\AppData\Local\ValheimTesting"
        : platform == "macos" ? "/Users/tester/Library/Application Support/ValheimTesting" : "/home/tester/.local/share/ValheimTesting";
    public string? SteamPath { get; set; }
    public string? ProgramFilesX86 { get; init; } = platform == "windows" ? @"C:\Program Files (x86)" : null;
    public HashSet<string> Directories { get; } = new(platform == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public Dictionary<string, string> Files { get; } = new(platform == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public string? RegistrySteamPath() => SteamPath;
    public bool DirectoryExists(string path) => Directories.Contains(path);
    public bool FileExists(string path) => Files.ContainsKey(path);
    public string? ReadText(string path) => Files.GetValueOrDefault(path);

    private char Separator => Platform == "windows" ? '\\' : '/';
    public void File(string path, string text = "")
    {
        Files[path] = text;
        for (int at = path.LastIndexOf(Separator); at > 0; at = path.LastIndexOf(Separator, at - 1)) Directories.Add(path[..at]);
    }
    /// <summary>A Steam app installed in <paramref name="library"/>: its manifest, folder and executable.</summary>
    public string App(string library, string app, string folder, string executable)
    {
        string s = Separator.ToString();
        File(library + s + "steamapps" + s + $"appmanifest_{app}.acf", $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{app}\"\n\t\"installdir\"\t\t\"{folder}\"\n}}\n");
        string install = library + s + "steamapps" + s + "common" + s + folder;
        File(install + s + executable.Replace('/', Separator), "game");
        return install;
    }

    /// <summary>The machine every test detects unless it names its own: no Steam, so the test host's installs never decide a result.</summary>
    [ModuleInitializer]
    internal static void NoSteamByDefault() => EnvironmentInventory.ThisMachine = new FakeMachine(HostProfile.CurrentPlatform)
    {
        Home = Path.Combine(Path.GetTempPath(), "vt-no-steam-home"), DataRoot = Path.Combine(Path.GetTempPath(), "vt-no-steam-data"),
    };
}

public sealed class ThisMachineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("this-machine-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The common case: one Windows PC, Steam registered at a custom path, the game in a second library, the server in Steam's own.
    private static FakeMachine WindowsPc(out string game, out string server)
    {
        var pc = new FakeMachine { SteamPath = "d:/games/steam" }; // Steam writes its registered path with forward slashes.
        pc.File(@"d:\games\steam\steamapps\libraryfolders.vdf",
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\Games\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n\t}\n}\n");
        game = pc.App(@"E:\SteamLibrary", "892970", "Valheim", "valheim.exe");
        server = pc.App(@"d:\games\steam", "896660", "Valheim dedicated server", "valheim_server.exe");
        return pc;
    }

    [Fact] public void WithNoFileAWindowsPcIsAServerAndAClientOnThisMachine()
    {
        var pc = WindowsPc(out string game, out string server);
        var inventory = EnvironmentInventory.Read(null, pc);
        var local = inventory.Hosts["local"];
        Assert.Equal(("local", "windows", "powershell", @"C:\Users\tester\AppData\Local\ValheimTesting\lock"), (local.Kind, local.Platform, local.Shell, local.Lock));
        var serverRecipe = Assert.Single(inventory.Environments, recipe => recipe.Name == "local-server");
        var clientRecipe = Assert.Single(inventory.Environments, recipe => recipe.Name == "local-client");
        Assert.Equal((server, 5688, 2486, @"C:\Users\tester\AppData\Local\ValheimTesting\runs\local-server"),
            (serverRecipe.Install, serverRecipe.CliPort, serverRecipe.GamePort, serverRecipe.Runtime));
        Assert.Equal((game, 5689, 0), (clientRecipe.Install, clientRecipe.CliPort, clientRecipe.GamePort));
        Assert.Equal(("local", @"C:\Users\tester\AppData\Local\ValheimTesting\leases"), (inventory.LeaseHost, inventory.LeaseDirectory));
        Assert.Empty(inventory.Missing);
        Assert.Contains(inventory.Detected, line => line.Contains(@"from registry HKCU\Software\Valve\Steam SteamPath") && line.Contains(@"E:\SteamLibrary"));
        Assert.Contains(inventory.Detected, line => line == $"Valheim (Steam app 892970): {game}");
        Assert.Contains(inventory.Detected, line => line.StartsWith("local-server (server) on local: install " + server) && line.Contains("assumed") && line.Contains("game port 2486"));
        // It resolves a one-server, one-client campaign with no file at all.
        var resolved = inventory.Resolve(new HostedCampaignManifest { Server = new(), Clients = new() { ["client"] = new() } });
        Assert.Equal(["local-server", "local-client"], resolved.Assignments.Select(assignment => assignment.Environment));
    }

    [Fact] public void WithoutTheRegistryValueSteamIsFoundInProgramFiles()
    {
        var pc = new FakeMachine();
        string game = pc.App(@"C:\Program Files (x86)\Steam", "892970", "Valheim", "valheim.exe");
        var inventory = EnvironmentInventory.Read(null, pc);
        Assert.Equal(game, Assert.Single(inventory.Environments).Install);
        Assert.Contains(inventory.Detected, line => line.Contains("(from Program Files (x86))"));
    }

    // No dedicated server installed: the client stays, the server is reported missing with every manifest tried and what to do.
    [Fact] public void AMissingDedicatedServerIsNamedWithEveryPathTriedAndTheAlternatives()
    {
        var pc = WindowsPc(out _, out string server);
        pc.Files.Remove(@"d:\games\steam\steamapps\appmanifest_896660.acf");
        var inventory = EnvironmentInventory.Read(null, pc);
        Assert.Equal("local-client", Assert.Single(inventory.Environments).Name);
        string missing = Assert.Single(inventory.Missing);
        Assert.Contains(@"d:\games\steam\steamapps\appmanifest_896660.acf", missing); // the registered root, the same library as the vdf's first
        Assert.Contains(@"E:\SteamLibrary\steamapps\appmanifest_896660.acf", missing);
        Assert.Contains("free", missing);
        Assert.Contains("valheim-test start", missing);
        var refused = Assert.Throws<ArgumentException>(() => inventory.Resolve(new HostedCampaignManifest { Server = new(), Clients = new() { ["client"] = new() } }));
        Assert.Contains("appmanifest_896660.acf", refused.Message);
    }

    // A manifest whose folder is gone, or an install without its executable, is not an install.
    [Fact] public void AnInstallWithoutItsExecutableIsNotDetected()
    {
        var pc = WindowsPc(out string game, out _);
        pc.Files.Remove(game + @"\valheim.exe");
        var inventory = EnvironmentInventory.Read(null, pc);
        Assert.DoesNotContain(inventory.Environments, recipe => recipe.Name == "local-client");
        Assert.Contains(inventory.Missing, line => line.Contains(game + " has no valheim.exe"));
    }

    [Fact] public void NoSteamAtAllRefusesWithTheRootsTried()
    {
        var error = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(null, new FakeMachine()));
        Assert.Contains("This machine has none", error.Message);
        Assert.Contains("Steam was not found", error.Message);
        var inventory = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(null, new FakeMachine("linux")));
        Assert.Contains("Valheim (Steam app 892970)", inventory.Message);
    }

    // The station's case: a Linux host and a Mac. A Mac never gets a local dedicated server; Linux finds both through ~/.steam.
    [Fact] public void LinuxAndMacInstallsAreDetectedToo()
    {
        var linux = new FakeMachine("linux");
        linux.Directories.Add("/home/tester/.steam/steam");
        linux.App("/home/tester/.steam/steam", "892970", "Valheim", "valheim.x86_64");
        linux.App("/home/tester/.steam/steam", "896660", "Valheim dedicated server", "valheim_server.x86_64");
        var inventory = EnvironmentInventory.Read(null, linux);
        Assert.Equal(["local-server", "local-client"], inventory.Environments.Select(recipe => recipe.Name));
        Assert.Equal("bash", inventory.Hosts["local"].Shell);

        var mac = new FakeMachine("macos");
        mac.App("/Users/tester/Library/Application Support/Steam", "892970", "Valheim", "Valheim.app/Contents/MacOS/Valheim");
        mac.App("/Users/tester/Library/Application Support/Steam", "896660", "Valheim dedicated server", "valheim_server/Valheim");
        var macInventory = EnvironmentInventory.Read(null, mac);
        Assert.Equal("local-client", Assert.Single(macInventory.Environments).Name);
        Assert.Contains(macInventory.Missing, line => line.Contains("macOS dedicated server"));
    }

    // A file lists the environments to use: one on this machine needs only its name and role, and the file gains nothing else.
    [Fact] public void AFileListsItsEnvironmentsAndALocalOneNeedsOnlyItsNameAndRoles()
    {
        var pc = WindowsPc(out string game, out string server);
        string file = Path.Combine(_root, "environments.json");
        File.WriteAllText(file, """
            {
              "hosts": { "lab": { "kind": "ssh", "platform": "windows", "shell": "powershell", "destination": "tester@lab", "lock": "C:\\vt\\lock" } },
              "environments": [
                { "name": "lab-client", "host": "lab", "roles": ["client"], "install": "C:\\Valheim", "runtime": "C:\\vt\\runs", "cliPort": 5590 },
                { "name": "local-server", "roles": ["server"], "gamePort": 2466 },
                { "name": "local-client", "roles": ["client"] }
              ],
              "leaseHost": "lab", "leaseDirectory": "C:\\vt\\leases"
            }
            """);
        var inventory = EnvironmentInventory.Read(file, pc);
        Assert.Equal(["lab-client", "local-server", "local-client"], inventory.Environments.Select(recipe => recipe.Name));
        var serverRecipe = inventory.Environments[1];
        Assert.Equal(("local", server, 2466, 5688), (serverRecipe.Host, serverRecipe.Install, serverRecipe.GamePort, serverRecipe.CliPort));
        var clientRecipe = inventory.Environments[2];
        Assert.Equal(("local", game, 5689), (clientRecipe.Host, clientRecipe.Install, clientRecipe.CliPort));
        Assert.Equal("C:\\Valheim", inventory.Environments[0].Install); // a remote environment is never filled in
        Assert.Equal(("lab", "C:\\vt\\leases"), (inventory.LeaseHost, inventory.LeaseDirectory)); // a file's leases are kept
    }

    // An inventory of other machines is read as written: this machine's installs never become a fallback for it.
    [Fact] public void AFileWithoutALocalEnvironmentGainsNothingFromThisMachine()
    {
        var pc = WindowsPc(out _, out _);
        string file = Path.Combine(_root, "environments.json");
        const string lab = """
            "hosts": { "lab": { "kind": "ssh", "platform": "windows", "shell": "powershell", "destination": "tester@lab", "lock": "C:\\vt\\lock" } },
            "environments": [ { "name": "lab-client", "host": "lab", "roles": ["client"], "install": "C:\\Valheim", "runtime": "C:\\vt\\runs", "cliPort": 5590 } ]
            """;
        File.WriteAllText(file, "{" + lab + """, "leaseHost": "lab", "leaseDirectory": "C:\\vt\\leases" }""");
        var inventory = EnvironmentInventory.Read(file, pc);
        Assert.Equal(["lab"], inventory.Hosts.Keys);
        Assert.Equal("lab-client", Assert.Single(inventory.Environments).Name);
        Assert.Empty(inventory.Detected);
        var refused = Assert.Throws<ArgumentException>(() => inventory.Resolve(new HostedCampaignManifest { Server = new(), Clients = new() { ["client"] = new() } }));
        Assert.Contains("No environment assignment for server", refused.Message);
        // Remote clients still need the file to name where their leases are shared.
        File.WriteAllText(file, "{" + lab + "}");
        Assert.Contains("leaseHost", Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(file, pc)).Message);
    }

    // A file's own host for this machine keeps its name: an environment without a host goes there, never to a second host.
    [Fact] public void AFilesOwnLocalHostIsTheOneDefaulted()
    {
        var pc = WindowsPc(out string game, out _);
        string file = Path.Combine(_root, "environments.json");
        File.WriteAllText(file, """
            {
              "hosts": { "this-pc": { "kind": "local", "platform": "windows", "shell": "pwsh", "lock": "C:\\vt\\lock" } },
              "environments": [ { "name": "pc-client", "host": "this-pc", "roles": ["client"], "install": "C:\\Other\\Valheim", "runtime": "C:\\vt\\runs", "cliPort": 5600 },
                                { "name": "local-client", "roles": ["client"] } ]
            }
            """);
        var inventory = EnvironmentInventory.Read(file, pc);
        Assert.Equal(["this-pc"], inventory.Hosts.Keys);
        Assert.Equal(("this-pc", game), (inventory.Environments[1].Host, inventory.Environments[1].Install));
        Assert.Equal("pwsh", inventory.Hosts["this-pc"].Shell);
        Assert.Equal("this-pc", inventory.LeaseHost); // every client is on this machine
        // Both clients are on one host, so a two-client campaign is refused as before.
        var refused = Assert.Throws<ArgumentException>(() => inventory.Resolve(new HostedCampaignManifest
            { Server = new(), Clients = new() { ["a"] = new(), ["b"] = new() } }));
        Assert.Contains("No environment assignment", refused.Message);
    }

    // A chosen port avoids every port reached on this machine: a later local environment's and an ssh tunnel's.
    [Fact] public void ChosenPortsAvoidEveryPortReachedOnThisMachine()
    {
        var pc = WindowsPc(out _, out _);
        string file = Path.Combine(_root, "environments.json");
        File.WriteAllText(file, """
            {
              "hosts": { "lab": { "kind": "ssh", "platform": "linux", "shell": "bash", "destination": "tester@lab", "lock": "/vt/lock" } },
              "environments": [
                { "name": "local-client", "roles": ["client"] },
                { "name": "lab-server", "host": "lab", "roles": ["server"], "install": "/opt/valheim", "runtime": "/vt/runs", "cliPort": 5577, "localCliPort": 5689, "gamePort": 2486 },
                { "name": "local-server", "roles": ["server"], "cliPort": 5688 }
              ]
            }
            """);
        var inventory = EnvironmentInventory.Read(file, pc);
        Assert.Equal(5690, inventory.Environments[0].CliPort);
        Assert.Equal(2486, inventory.Environments[2].GamePort); // another host's game port is its own
    }

    // A server binds its game port and the next two: a chosen one keeps clear of an off-grid local one and of a
    // host-network container's, which binds on this machine too.
    [Fact] public void ChosenGamePortsKeepClearOfEveryServerRangeOnThisMachine()
    {
        var pc = WindowsPc(out _, out _);
        string file = Path.Combine(_root, "environments.json");
        File.WriteAllText(file, """
            {
              "hosts": { "docker": { "kind": "container", "platform": "linux", "shell": "bash", "container": "vt-server", "lock": "/vt/lock" } },
              "environments": [
                { "name": "box", "host": "docker", "roles": ["server"], "install": "/opt/valheim", "runtime": "/vt/runs", "cliPort": 5700, "gamePort": 2496 },
                { "name": "pinned", "roles": ["server"], "gamePort": 2487 },
                { "name": "local-server", "roles": ["server"] }
              ]
            }
            """);
        var inventory = EnvironmentInventory.Read(file, pc);
        Assert.Equal(2506, inventory.Environments[2].GamePort);
    }

    // A test's own machine applies to its flow only and nests: leaving an inner one restores the outer one.
    [Fact] public void AFlowsMachineNestsAndRestores()
    {
        var linux = new FakeMachine("linux");
        linux.Directories.Add("/home/tester/.steam/steam");
        linux.App("/home/tester/.steam/steam", "892970", "Valheim", "valheim.x86_64");
        var windows = WindowsPc(out _, out _);
        using (EnvironmentInventory.UseMachine(linux))
        {
            using (EnvironmentInventory.UseMachine(windows)) Assert.Same(windows, EnvironmentInventory.ThisMachine);
            Assert.Same(linux, EnvironmentInventory.ThisMachine);
        }
        Assert.IsType<FakeMachine>(EnvironmentInventory.ThisMachine);
        Assert.NotSame(linux, EnvironmentInventory.ThisMachine);
    }

    // A file's local host with a field written as null is filled in or refused as invalid, never a crash.
    [Fact] public void ANullFieldIsFilledInOrRefusedAsInvalid()
    {
        var pc = WindowsPc(out _, out _);
        string file = Path.Combine(_root, "environments.json");
        File.WriteAllText(file, """{ "hosts": { "local": { "kind": "local", "platform": null, "shell": null, "lock": null } }, "environments": [ { "name": "local-client", "roles": ["client"] } ] }""");
        var inventory = EnvironmentInventory.Read(file, pc);
        Assert.Equal(("windows", "powershell"), (inventory.Hosts["local"].Platform, inventory.Hosts["local"].Shell));
        File.WriteAllText(file, """{ "hosts": { "local": { "kind": null } }, "environments": [ { "name": "x", "roles": null } ] }""");
        var thrown = Record.Exception(() => EnvironmentInventory.Read(file, pc)); Assert.True(thrown is ArgumentException, thrown?.ToString());
    }

    // The registry and the file system are this machine's own: the real locator answers on every platform without throwing.
    [Fact] public void TheRealMachineIsReadWithoutError()
    {
        var machine = new LocalSteamLocator();
        Assert.Equal(HostProfile.CurrentPlatform, machine.Platform);
        Assert.True(Path.IsPathRooted(machine.DataRoot));
        _ = SteamDetection.Find(machine, SteamDetection.GameApp, SteamDetection.DedicatedServerApp);
    }
}
