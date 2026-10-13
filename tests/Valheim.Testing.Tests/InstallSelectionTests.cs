using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #730: the one owner that reads a profile's selection from a modded install, on any host, for a client or a dedicated server.
// This machine's install is read in place (the real host scripts on a fake install); another host's is fetched here and checked
// against the install's listing (FakeServerHost, which keeps its folders in a local mirror). The platform is the install's on its
// host, never the runner's.
public sealed class InstallSelectionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    private string Mirror => Path.Combine(_rig.Root, "host");

    // The rig's ValheimCLI set, its manifest beside its DLLs as a bundle holds them.
    private (CliCapabilityManifest Manifest, CliSetLocation Location) CliSet()
    {
        string path = Path.Combine(_rig.Root, "cli", CliBundle.ManifestFile);
        File.Copy(_rig.CliManifest(save: true), path, overwrite: true);
        var manifest = CliCapabilityManifest.Read(path);
        var location = manifest.Locate(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.dll"), "beside its manifest", othersLoad: false);
        Assert.Empty(location.Problems);
        return (manifest, location);
    }

    private static void Put(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Put(string root, string relative, byte[] bytes)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // A Linux dedicated server on a host: the game, BepInEx's core and Doorstop, and the install's own mods and settings.
    private static string LinuxServer(FakeServerHost host, string source)
    {
        string install = host.Local(source);
        FakeInstalls.Server(install);
        FakeInstalls.LinuxLoader(install);
        Put(install, GameLaunch.ServerLinuxExecutable, "server");
        Put(install, "BepInEx/plugins/Mod/Mod.dll", "the install's mod");
        Put(install, "BepInEx/config/BepInEx.cfg", "[Logging]\nsource = true\n");
        Put(install, "BepInEx/config/example.mod.cfg", "mod settings");
        Put(install, "BepInEx/patchers/Patch.dll", "the install's patcher");
        return install;
    }

    // A Windows client and dedicated server on one host, each with its Doorstop pair.
    private static void WindowsInstalls(FakeServerHost host, string client, string server)
    {
        foreach (var (source, executable) in new[] { (client, GameLaunch.ClientWindowsExecutable), (server, GameLaunch.ServerWindowsExecutable) })
        {
            string install = host.Local(source);
            if (source == client) FakeInstalls.Client(install); else FakeInstalls.Server(install);
            Put(install, executable, "game");
            Put(install, "winhttp.dll", "MZ target_assembly");
            Put(install, "doorstop_config.ini", "[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n");
            Put(install, "BepInEx/plugins/Mod.dll", "the install's mod");
        }
    }

    [ClientHostFact] public async Task ThisMachinesInstallIsReadInPlace()
    {
        var (manifest, location) = CliSet();
        string game = _rig.Game;
        _rig.Write("game/BepInEx/plugins/valheimCLI.dll", System.Text.Encoding.UTF8.GetBytes("an older ValheimCLI core")); // by name
        _rig.Write("game/BepInEx/scripts/Renamed.dll", RegressionRig.Assembly("Renamed", new("valheimCLI.valheimCLI", "0.3.0"), marker: "Renamed")); // by its plugin
        var host = new LocalGameHost("this machine", HostShell.ThisMachine);
        string generated;
        using (var selection = await InstallSelection.ReadAsync(host, HostedRuntimeKind.Client, game, manifest, location, null, 5556, null, Timeout))
        {
            Assert.Equal(game, selection.Source.Root);
            var files = selection.Files.ToDictionary(file => file.RelativePath, file => file.Source);
            // The install's own files are read where they are; the staged set's from beside its manifest.
            Assert.Equal(Path.Combine(game, "BepInEx", "plugins", "Unrelated.dll"), files["BepInEx/plugins/Unrelated.dll"]);
            Assert.DoesNotContain(BepInExSettings.RelativePath, files.Keys); // The profile stage decides the install's BepInEx.cfg.
            Assert.Equal(Path.Combine(_rig.Root, "cli", "valheimCLI.dll"), files["BepInEx/plugins/valheimCLI.dll"]);
            Assert.DoesNotContain("BepInEx/scripts/Renamed.dll", files.Keys);
            Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll", "BepInEx/scripts/Renamed.dll" }, selection.Replaced.Order(StringComparer.Ordinal));
            // ValheimCLI's port, generated beside the selection, never in the install.
            generated = files[CliServerConfig.RelativePath];
            Assert.False(generated.StartsWith(game, StringComparison.Ordinal));
            Assert.Contains("Port = 5556", File.ReadAllText(generated));
        }
        Assert.False(File.Exists(generated));
        Assert.True(File.Exists(Path.Combine(game, "BepInEx", "plugins", "Unrelated.dll")));
    }

    [Fact] public async Task AnotherHostsInstallIsFetchedAndCheckedAgainstItsListing()
    {
        var (manifest, location) = CliSet();
        var host = new FakeServerHost("linux-server", Mirror);
        const string source = "/game/server";
        string install = LinuxServer(host, source);
        Put(install, "BepInEx/plugins/valheimCLI.dll", "the install's own ValheimCLI");
        // Another ValheimCLI build under another name, found by its plugin in the fetched bytes.
        Put(install, "BepInEx/scripts/Renamed.dll", RegressionRig.Assembly("Renamed", new("valheimCLI.valheimCLI", "0.3.0"), marker: "Renamed"));
        Put(install, "BepInEx/plugins/Mod/._Mod.dll", "AppleDouble"); // never part of a selection
        // A reviewed loader package replaces the install's loader; its plugins and settings stay the install's to select.
        string packageRoot = Path.Combine(_rig.Root, "linux-package");
        foreach (string relative in new[] { "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll", "doorstop_libs/libdoorstop_x64.so" })
            Put(packageRoot, relative, "package " + relative);
        var package = BepInExLoaderPackage.Capture(packageRoot, "BepInExPack_Valheim", "5.4.2333");
        var before = WorldFixture.Manifest(install);
        string work;
        using (var selection = await InstallSelection.ReadAsync(host, HostedRuntimeKind.Server, source, manifest, location, package, 5560, null, Timeout))
        {
            // Each folder the install holds was fetched once.
            var fetched = host.Runs.Where(run => run.Script == "fetch").Select(run => run.Variables["dir"]).ToList();
            Assert.Equal(new[] { "/game/server/BepInEx/plugins", "/game/server/BepInEx/scripts", "/game/server/BepInEx/config", "/game/server/BepInEx/patchers" }, fetched);
            var files = selection.Files.ToDictionary(file => file.RelativePath, file => file.Source);
            Assert.Equal(new[] { "BepInEx/config/example.mod.cfg", CliServerConfig.RelativePath,
                "BepInEx/patchers/Patch.dll", "BepInEx/plugins/Mod/Mod.dll", "BepInEx/plugins/valheimCLI.dll" }, files.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll", "BepInEx/scripts/Renamed.dll" }, selection.Replaced);
            // Every install file is a local copy, with the listing's hash.
            foreach (string relative in new[] { "BepInEx/plugins/Mod/Mod.dll", "BepInEx/config/example.mod.cfg", "BepInEx/patchers/Patch.dll" })
            {
                Assert.False(files[relative].StartsWith(Mirror, StringComparison.Ordinal));
                Assert.Equal(selection.Source.Files[relative], FileHash.Sha256(files[relative]));
            }
            Assert.Equal(Path.Combine(_rig.Root, "cli", "valheimCLI.dll"), files["BepInEx/plugins/valheimCLI.dll"]);
            Assert.Contains("Port = 5560", File.ReadAllText(files[CliServerConfig.RelativePath]));
            work = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(files["BepInEx/patchers/Patch.dll"])!)!)!;
        }
        Assert.False(Directory.Exists(work)); // Disposed: the fetched files and the generated config are gone.
        Assert.Equal(before, WorldFixture.Manifest(install));
    }

    [Fact] public async Task AFetchedFileThatDiffersFromTheListingIsRefused()
    {
        var (manifest, location) = CliSet();
        var host = new FakeServerHost("linux-server", Mirror);
        LinuxServer(host, "/game/server");
        var arrived = new List<string>();
        host.AfterFetch = local =>
        {
            arrived.Add(local);
            string mod = Path.Combine(local, "Mod", "Mod.dll");
            if (File.Exists(mod)) File.AppendAllText(mod, "changed on the way");
        };
        var changed = await Assert.ThrowsAsync<IOException>(() =>
            InstallSelection.ReadAsync(host, HostedRuntimeKind.Server, "/game/server", manifest, location, null, 5560, null, Timeout));
        Assert.Contains("BepInEx/plugins/Mod/Mod.dll", changed.Message);
        Assert.Contains("differs from the install's listing", changed.Message);
        Assert.All(arrived, local => Assert.False(Directory.Exists(local)));

        // A file the listing did not hold is refused too.
        arrived.Clear();
        host.AfterFetch = local => { arrived.Add(local); File.WriteAllText(Path.Combine(local, "Extra.dll"), "not listed"); };
        var extra = await Assert.ThrowsAsync<IOException>(() =>
            InstallSelection.ReadAsync(host, HostedRuntimeKind.Server, "/game/server", manifest, location, null, 5560, null, Timeout));
        Assert.Contains("BepInEx/plugins/Extra.dll", extra.Message);
        Assert.Contains("does not hold it", extra.Message);
        Assert.All(arrived, local => Assert.False(Directory.Exists(local)));
    }

    // A dedicated server has no client, so it needs no ValheimCLI mutation access; a joined client does, a hosting client does not.
    [Fact] public async Task TheRoleDecidesWhetherAnExplicitCliConfigNeedsMutationAccess()
    {
        var (manifest, location) = CliSet();
        var host = new FakeServerHost("windows-host", Mirror, windows: true);
        const string client = @"C:\game\client", server = @"C:\game\server";
        WindowsInstalls(host, client, server);
        const string chosen = "[Server]\nEnabled = true\nPort = 5556\n";
        foreach (string source in new[] { client, server }) Put(host.Local(source), CliServerConfig.RelativePath, chosen);

        using (var selection = await InstallSelection.ReadAsync(host, HostedRuntimeKind.Server, server, manifest, location, null, 5560, null, Timeout))
            Assert.Equal(chosen, File.ReadAllText(selection.Files.Single(file => file.RelativePath == CliServerConfig.RelativePath).Source));
        var joined = await Assert.ThrowsAsync<InvalidDataException>(() =>
            InstallSelection.ReadAsync(host, HostedRuntimeKind.Client, client, manifest, location, null, 5556, null, Timeout));
        Assert.Contains("AllowOnServerClients = true", joined.Message);
        Assert.Contains("Client's selected", joined.Message);
        using (var hosting = await InstallSelection.ReadAsync(host, HostedRuntimeKind.Client, client, manifest, location, null, 5556,
            new ClientRunPlan { HostWorld = new HostWorldPlan() }, Timeout))
            Assert.Equal(chosen, File.ReadAllText(hosting.Files.Single(file => file.RelativePath == CliServerConfig.RelativePath).Source));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            InstallSelection.ReadAsync(host, HostedRuntimeKind.Server, server, manifest, location, null, 5560, new ClientRunPlan(), Timeout));
    }

    // An install's paths compare as its host lists them: a Windows install's BepInEx\Scripts is its scripts folder, a Linux
    // install's BepInEx/Scripts is not one BepInEx reads. Either answer holds on every runner, whatever its filesystem.
    [Fact] public async Task TheInstallsHostDecidesHowItsPathsCompareNotTheRunner()
    {
        var (manifest, location) = CliSet();
        var windows = new FakeServerHost("windows-host", Path.Combine(Mirror, "windows"), windows: true);
        const string server = @"C:\game\server";
        WindowsInstalls(windows, @"C:\game\client", server);
        Put(windows.Local(server), "BepInEx/Scripts/Tool.dll", "the install's script");
        using (var selection = await InstallSelection.ReadAsync(windows, HostedRuntimeKind.Server, server, manifest, location, null, 5560, null, Timeout))
            Assert.Equal("the install's script", File.ReadAllText(selection.Files.Single(file => file.RelativePath == "BepInEx/scripts/Tool.dll").Source));
        Assert.Contains(windows.Runs, run => run.Script == "fetch" && run.Variables["dir"] == @"C:\game\server\BepInEx\Scripts");

        var linux = new FakeServerHost("linux-server", Path.Combine(Mirror, "linux"));
        string linuxInstall = LinuxServer(linux, "/game/server");
        Put(linuxInstall, "BepInEx/Scripts/Ignored.dll", "a folder BepInEx does not read on Linux");
        using (var selection = await InstallSelection.ReadAsync(linux, HostedRuntimeKind.Server, "/game/server", manifest, location, null, 5560, null, Timeout))
        {
            Assert.DoesNotContain(selection.Files, file => file.Source.EndsWith("Ignored.dll", StringComparison.Ordinal));
            Assert.Contains(selection.Files, file => file.RelativePath == "BepInEx/plugins/Mod/Mod.dll");
        }
        Assert.DoesNotContain(linux.Runs, run => run.Script == "fetch" && run.Variables["dir"].Contains("Scripts", StringComparison.Ordinal));
    }
}
