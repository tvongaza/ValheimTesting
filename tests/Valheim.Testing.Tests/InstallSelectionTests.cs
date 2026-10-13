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

    private static BepInExLoaderPackage Package(string root, params string[] files)
    {
        foreach (string relative in files) Put(root, relative, "package " + relative);
        return BepInExLoaderPackage.Capture(root, "BepInExPack_Valheim", "5.4.2333");
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
            Assert.Equal(Path.Combine(game, "BepInEx", "config", "BepInEx.cfg"), files[BepInExSettings.RelativePath]);
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
        Put(install, "BepInEx/plugins/Mod/._Mod.dll", "AppleDouble"); // never part of a selection
        var before = WorldFixture.Manifest(install);
        string work;
        using (var selection = await InstallSelection.ReadAsync(host, HostedRuntimeKind.Server, source, manifest, location, null, 5560, null, Timeout))
        {
            // Each folder the install holds was fetched once; scripts has nothing, so nothing was fetched for it.
            var fetched = host.Runs.Where(run => run.Script == "fetch").Select(run => run.Variables["dir"]).ToList();
            Assert.Equal(new[] { "/game/server/BepInEx/plugins", "/game/server/BepInEx/config", "/game/server/BepInEx/patchers" }, fetched);
            var files = selection.Files.ToDictionary(file => file.RelativePath, file => file.Source);
            Assert.Equal(new[] { "BepInEx/config/BepInEx.cfg", "BepInEx/config/example.mod.cfg", CliServerConfig.RelativePath,
                "BepInEx/patchers/Patch.dll", "BepInEx/plugins/Mod/Mod.dll", "BepInEx/plugins/valheimCLI.dll" }, files.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "BepInEx/plugins/valheimCLI.dll" }, selection.Replaced);
            // Every install file is a local copy, with the listing's hash.
            foreach (string relative in new[] { "BepInEx/plugins/Mod/Mod.dll", "BepInEx/config/BepInEx.cfg", "BepInEx/config/example.mod.cfg", "BepInEx/patchers/Patch.dll" })
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

    // Whether an install's own BepInEx.cfg stays beside a loader package that has none is the install's platform's rule: a macOS
    // install keeps it, a Linux one does not. The platform is read from the install on its host, so either answer holds on
    // every runner (a runner-platform rule gets one of the two wrong wherever it runs).
    [Fact] public async Task TheInstallsPlatformOnItsHostDecidesNotTheRunners()
    {
        var (manifest, location) = CliSet();
        var mac = new FakeServerHost("mac-server", Path.Combine(Mirror, "mac"));
        string macInstall = mac.Local("/game/mac-server");
        Put(macInstall, GameLaunch.ServerMacExecutable, "mac server");
        Put(macInstall, "valheim_server/Data/Managed/" + InstallPins.GameAssemblyName, "mac game assembly");
        FakeInstalls.MacLoader(macInstall);
        Put(macInstall, "BepInEx/core/BepInEx.dll", "core");
        Put(macInstall, "BepInEx/core/BepInEx.Preloader.dll", "preloader");
        Put(macInstall, "BepInEx/config/BepInEx.cfg", "[Logging]\nsource = true\n");
        var macPackage = Package(Path.Combine(_rig.Root, "mac-package"), "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll", "libdoorstop.dylib");
        using (var selection = await InstallSelection.ReadAsync(mac, HostedRuntimeKind.Server, "/game/mac-server", manifest, location, macPackage, 5560, null, Timeout))
            Assert.Contains(selection.Files, file => file.RelativePath == BepInExSettings.RelativePath);

        var linux = new FakeServerHost("linux-server", Path.Combine(Mirror, "linux"));
        LinuxServer(linux, "/game/server");
        var linuxPackage = Package(Path.Combine(_rig.Root, "linux-package"), "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll", "doorstop_libs/libdoorstop_x64.so");
        using (var selection = await InstallSelection.ReadAsync(linux, HostedRuntimeKind.Server, "/game/server", manifest, location, linuxPackage, 5560, null, Timeout))
        {
            Assert.DoesNotContain(selection.Files, file => file.RelativePath == BepInExSettings.RelativePath);
            Assert.Contains(selection.Files, file => file.RelativePath == "BepInEx/config/example.mod.cfg");
        }
    }
}
