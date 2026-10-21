using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

/// <summary>An in-memory machine for the inventory's this-machine default: a registry value, folders and files, any platform.</summary>
internal sealed class FakeMachine(string platform = "windows") : ISteamLocator
{
    public string Platform { get; } = platform;
    public Architecture OsArchitecture { get; set; } = Architecture.X64;
    public string Home { get; init; } = platform == "windows" ? @"C:\Users\tester" : platform == "macos" ? "/Users/tester" : "/home/tester";
    public string DataRoot { get; init; } = platform == "windows" ? @"C:\Users\tester\AppData\Local\ValheimTesting"
        : platform == "macos" ? "/Users/tester/Library/Application Support/ValheimTesting" : "/home/tester/.local/share/ValheimTesting";
    public string? SteamPath { get; set; }
    public HashSet<string> Directories { get; } = new(platform == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public Dictionary<string, string> Files { get; } = new(platform == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public string? RegistrySteamPath() => SteamPath;
    public bool DirectoryExists(string path) => Directories.Contains(path);
    public bool FileExists(string path) => Files.ContainsKey(path);
    public string? ReadText(string path) => Files.GetValueOrDefault(path);
    public string OnDiskPath(string path) => Directories.TryGetValue(path, out string? actual) ? actual : path;
    /// <summary>Links: a link's path and the folder it finally points to.</summary>
    public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);
    public string? LinkTarget(string path) => Links.GetValueOrDefault(path);
    public Exception? StorageError { get; set; }
    public string FinalPath(string path)
    {
        string current = path.Replace('/', Separator);
        var suffix = new Stack<string>();
        while (current.Length != 0)
        {
            if (Links.TryGetValue(current, out string? target))
            {
                string resolved = target;
                foreach (string part in suffix) resolved = HostPath.Join(resolved, part);
                return resolved;
            }
            string trimmed = current.TrimEnd(Separator);
            int slash = trimmed.LastIndexOf(Separator);
            if (slash < 0 || Platform == "windows" && slash == 2) break;
            suffix.Push(trimmed[(slash + 1)..]);
            current = trimmed[..slash];
            if (Platform == "windows" && current.Length == 2 && current[1] == ':') current += Separator;
        }
        return path;
    }
    public Dictionary<string, WindowsStorageVolume> Storage { get; } = new(platform == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public WindowsStorageVolume StorageVolume(string path)
    {
        if (StorageError != null) throw StorageError;
        var match = Storage.Where(item => path.StartsWith(item.Key, Platform == "windows" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(item => item.Key.Length).Select(item => item.Value).FirstOrDefault();
        if (match != null) return match;
        string root = Platform == "windows" && path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + @":\" : Path.GetPathRoot(path) ?? path;
        return new WindowsStorageVolume(root, root, Platform == "windows" ? "NTFS" : "", path.StartsWith(@"\\", StringComparison.Ordinal));
    }
    /// <summary>Linux mounts or Mac volumes by mount point; a path under none of them is on the root's ext4 (APFS on a Mac).</summary>
    public Dictionary<string, UnixStorageVolume> UnixStorage { get; } = new(StringComparer.Ordinal);
    public int UnixProbes { get; private set; }
    public IReadOnlyList<UnixStorageVolume> UnixStorageVolumes(IReadOnlyList<string> paths)
    {
        if (StorageError != null) throw StorageError;
        UnixProbes++;
        return paths.Select(path => UnixStorage.Where(item => path == item.Key || path.StartsWith(item.Key.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(item => item.Key.Length).Select(item => item.Value).FirstOrDefault()
            ?? new UnixStorageVolume("/", "2049", Platform == "macos" ? "apfs" : "ext4", Writable: true) { Rules = ProfileLinkRules.For(Platform) }).ToList();
    }

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
    internal static void NoSteamByDefault()
    {
        EnvironmentInventory.ThisMachine = new FakeMachine(HostProfile.CurrentPlatform)
        {
            OsArchitecture = RuntimeInformation.OSArchitecture,
            Home = Path.Combine(Path.GetTempPath(), "vt-no-steam-home"), DataRoot = Path.Combine(Path.GetTempPath(), "vt-no-steam-data"),
        };
        // ValheimTesting's own folder on this machine (the pinned bundle and pack, the run journal) is a temporary one too, so no
        // test writes the test machine's real folder. It is the real machine's (LocalSteamLocator), not the simulated
        // inventory machine's above, which may describe another platform's folders.
        LocalSteamLocator.DataRootDefault = Path.Combine(Path.GetTempPath(), "vt-no-steam-data");
    }
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

    [Fact]
    public void WindowsProfileDefaultsStayInDataOnTheInstallVolumeAndRelocateAcrossVolumes()
    {
        var same = new FakeMachine { DataRoot = @"C:\Users\tester\AppData\Local\ValheimTesting" };
        var sameRoot = ProfileRuntimeStorage.DefaultRoot(same, @"C:\Steam\Valheim");
        Assert.Equal(@"C:\Users\tester\AppData\Local\ValheimTesting\runs", sameRoot.Path);
        Assert.Contains("data folder is on the install volume", sameRoot.Reason);

        var other = new FakeMachine { DataRoot = @"C:\Users\tester\AppData\Local\ValheimTesting" };
        var moved = ProfileRuntimeStorage.DefaultRoot(other, @"D:\SteamLibrary\Valheim");
        Assert.Equal(@"D:\ValheimTesting\runs", moved.Path);
        Assert.Contains("profiles follow the install volume", moved.Reason);

        var mounted = new FakeMachine { DataRoot = @"C:\Users\tester\AppData\Local\ValheimTesting" };
        mounted.Storage[@"C:\Users"] = new WindowsStorageVolume(@"C:\", "system", "NTFS", false);
        mounted.Storage[@"C:\MountedGames"] = new WindowsStorageVolume(@"C:\MountedGames\", "games", "NTFS", false);
        Assert.Equal(@"C:\MountedGames\ValheimTesting\runs",
            ProfileRuntimeStorage.DefaultRoot(mounted, @"C:\MountedGames\Steam\Valheim").Path);

        var junction = new FakeMachine { DataRoot = @"C:\Users\tester\AppData\Local\ValheimTesting" };
        junction.Links[@"C:\Steam"] = @"D:\Games\Steam";
        junction.Storage[@"D:\Games"] = new WindowsStorageVolume(@"D:\", "games", "NTFS", false);
        Assert.Equal(@"D:\ValheimTesting\runs",
            ProfileRuntimeStorage.DefaultRoot(junction, @"C:\Steam\steamapps\common\Valheim").Path);
    }

    [Fact]
    public void AFailedWindowsVolumeProbeKeepsInventoryReadableAndUsesTheDataFolder()
    {
        string file = Path.Combine(_root, "subst-inventory.json");
        File.WriteAllText(file, """
            {
              "environments": [
                { "name": "server", "roles": ["server"], "install": "S:\\Valheim server", "cliPort": 5577, "gamePort": 2456 }
              ]
            }
            """);
        var machine = new FakeMachine { StorageError = new IOException("SUBST volume has no mount-point identity") };

        var inventory = EnvironmentInventory.Read(file, machine);

        var recipe = Assert.Single(inventory.Environments);
        Assert.Equal(@"C:\Users\tester\AppData\Local\ValheimTesting\runs\server", recipe.Runtime);
        Assert.Contains("using ValheimTesting's data folder", recipe.RuntimeReason);
        Assert.False(recipe.RuntimeExplicit);
        Assert.Null(recipe.ProfileStorage!.Refusal);
        Assert.Contains(@"install S:\Valheim server", recipe.ProfileStorage.Warning);
        Assert.Contains("SUBST volume has no mount-point identity", recipe.ProfileStorage.Warning);

        if (OperatingSystem.IsWindows())
        {
            var plan = new ServerRunPlan { Executable = GameLaunch.ServerWindowsExecutable, Port = 5577, Arguments = ["-port", "2456"] };
            Assert.Equal("server", inventory.PlaceServer(plan).Assignment.Environment);
        }
    }

    [Fact]
    public void WindowsProfileStorageRefusesAnExplicitOtherVolumeAndFilesystemsWithoutLinks()
    {
        var ntfsC = new WindowsStorageVolume(@"C:\", "c", "NTFS", false);
        var ntfsD = new WindowsStorageVolume(@"D:\", "d", "NTFS", false);
        string cross = Assert.IsType<string>(ProfileRuntimeStorage.Refusal(@"C:\game", ntfsC, @"D:\runs", ntfsD));
        Assert.Contains("explicit inventory runtime folder", cross);
        Assert.Contains("Put the inventory runtime on the install's NTFS volume", cross);
        Assert.Contains("hard link cannot cross volumes", cross); // #821: the reason, not only the two volumes

        // The path-syntax verdict (a remote Windows host, no probe) is the same rule in the same words.
        string syntax = Assert.IsType<string>(ProfileRuntimeStorage.PlacementVerdict(@"C:\game", @"D:\runs", explicitRuntime: true).Refusal);
        Assert.Equal(cross.Replace("D:\\ (NTFS)", @"D:\").Replace("C:\\ (NTFS)", @"C:\"), syntax);
        Assert.Null(ProfileRuntimeStorage.PlacementVerdict(@"C:\game", @"D:\runs", explicitRuntime: false).Refusal);

        foreach (var unsupported in new[]
        {
            new WindowsStorageVolume(@"E:\", "e", "exFAT", false),
            new WindowsStorageVolume(@"F:\", "f", "FAT32", false),
            new WindowsStorageVolume(@"\\nas\games\", "share", "NTFS", true),
        })
        {
            string refusal = Assert.IsType<string>(ProfileRuntimeStorage.Refusal(@"E:\game", unsupported, @"E:\runs", unsupported));
            Assert.Contains("cannot hold the hard links", refusal);
            Assert.Contains("Move the Steam library to NTFS", refusal);
            Assert.DoesNotContain("--in-place", refusal);
            Assert.Contains("--in-place", ProfileRuntimeStorage.Refusal(@"E:\game", unsupported, @"E:\runs", unsupported,
                explicitRuntime: false, inPlaceAvailable: true));
        }
    }

    // #612: a Linux server profile hard-links the install too, so the same verdict refuses a runtime the links cannot reach: another
    // device, a bind mount of the same device (rename and link refuse EXDEV across mounts), FAT or exFAT, or a folder this user
    // cannot write. Each refusal names the inventory runtime or the default root and where to put it.
    [Fact]
    public void LinuxProfileStorageRefusesAnotherMountABindMountFilesystemsWithoutLinksAndAnUnwritableRoot()
    {
        var games = new UnixStorageVolume("/mnt/games", "2065", "ext4", Writable: true);
        Assert.Null(ProfileRuntimeStorage.Refusal("/mnt/games/valheim", games, "/mnt/games/runs", games));
        var verdict = ProfileRuntimeStorage.Verdict("/mnt/games/valheim", games, "/mnt/games/runs", games, explicitRuntime: true);
        Assert.Equal("source /mnt/games/valheim and runtime /mnt/games/runs are on /mnt/games (ext4, device 2065)", verdict.Description);

        string other = Assert.IsType<string>(ProfileRuntimeStorage.Refusal("/mnt/games/valheim", games, "/home/tester/runs",
            new UnixStorageVolume("/", "2049", "ext4", Writable: true)));
        Assert.Equal("the explicit inventory runtime folder /home/tester/runs is on / (ext4, device 2049), not the source install's mount " +
            "/mnt/games (ext4, device 2065). A Linux profile hard-links the install's game files, and a hard link cannot cross mounts. " +
            "Put the inventory runtime on the install's mount /mnt/games.", other);

        // A bind mount shares the device; only its mount point differs, and the link still fails.
        string bind = Assert.IsType<string>(ProfileRuntimeStorage.Refusal("/mnt/games/valheim", games, "/srv/runs",
            new UnixStorageVolume("/srv", "2065", "ext4", Writable: true), explicitRuntime: false));
        Assert.Contains("the default runtime folder /srv/runs is on /srv (ext4, device 2065), not the source install's mount", bind);
        Assert.Contains("Put the default runtime on the install's mount /mnt/games.", bind);
        // A btrfs subvolume has its own device under the same mount point.
        Assert.NotNull(ProfileRuntimeStorage.Refusal("/data/valheim", new UnixStorageVolume("/data", "40", "btrfs", true), "/data/runs",
            new UnixStorageVolume("/data", "41", "btrfs", true)));

        foreach (string fileSystem in new[] { "vfat", "exfat", "msdos" })
        {
            var noLinks = new UnixStorageVolume("/media/usb", "2081", fileSystem, Writable: true);
            string source = Assert.IsType<string>(ProfileRuntimeStorage.Refusal("/media/usb/valheim", noLinks, "/media/usb/runs", noLinks));
            Assert.Equal($"the source install /media/usb/valheim is on /media/usb ({fileSystem}, device 2081), which cannot hold the hard links " +
                "a Linux profile requires. Move the Steam library to a filesystem with hard links (ext4, xfs or btrfs).", source);
            string runtime = Assert.IsType<string>(ProfileRuntimeStorage.Refusal("/mnt/games/valheim", games, "/media/usb/runs", noLinks));
            Assert.Contains("which cannot hold the hard links a Linux profile requires. Put the inventory runtime on the install's mount /mnt/games.", runtime);
        }

        string unwritable = Assert.IsType<string>(ProfileRuntimeStorage.Refusal("/mnt/games/valheim", games, "/mnt/games/ValheimTesting/runs/server",
            games with { Writable = false }, explicitRuntime: false));
        Assert.Equal("the default runtime folder /mnt/games/ValheimTesting/runs/server on /mnt/games (ext4, device 2065) is not writable by this user. " +
            "Create it with write access for this user, or put the default runtime in a writable folder on the install's mount /mnt/games.", unwritable);
    }

    // #612: a Linux server's default root follows Windows' rule. ValheimTesting's data folder when it is on the install's mount, else
    // ValheimTesting/runs at the install's mount point; a probe that fails keeps the data folder for preflight to check. A Mac's
    // profiles launch their source executable, so they keep the data folder whatever the mounts.
    [Fact]
    public void LinuxProfileDefaultsStayInDataOnTheInstallMountAndFollowTheInstallElsewhere()
    {
        var same = new FakeMachine("linux");
        var sameRoot = ProfileRuntimeStorage.DefaultRoot(same, "/home/tester/.steam/steam/steamapps/common/Valheim dedicated server");
        Assert.Equal("/home/tester/.local/share/ValheimTesting/runs", sameRoot.Path);
        Assert.Equal("ValheimTesting's data folder is on the install mount / (ext4, device 2049)", sameRoot.Reason);

        var other = new FakeMachine("linux");
        other.UnixStorage["/mnt/games"] = new UnixStorageVolume("/mnt/games", "2065", "xfs", Writable: true);
        var moved = ProfileRuntimeStorage.DefaultRoot(other, "/mnt/games/steamapps/common/Valheim dedicated server");
        Assert.Equal("/mnt/games/ValheimTesting/runs", moved.Path);
        Assert.Equal("the install is on /mnt/games (xfs, device 2065), not the data folder's / (ext4, device 2049), so Linux profiles follow the install mount", moved.Reason);

        var failed = new FakeMachine("linux") { StorageError = new IOException("df: unrecognized option '--output'") };
        Assert.Equal("/home/tester/.local/share/ValheimTesting/runs", ProfileRuntimeStorage.DefaultRoot(failed, "/mnt/games/valheim").Path);
        Assert.Contains("the Linux storage probe could not identify the install mount (df: unrecognized option", ProfileRuntimeStorage.DefaultRoot(failed, "/mnt/games/valheim").Reason);

        // A Mac server's profile is linked too and follows its volume; a Mac client's launches its source bundle and is never probed.
        var mac = new FakeMachine("macos");
        mac.UnixStorage["/Volumes/Games"] = new UnixStorageVolume("/Volumes/Games", "16777240", "apfs", Writable: true) { Rules = ProfileLinkRules.MacOS };
        var macServer = ProfileRuntimeStorage.DefaultRoot(mac, "/Volumes/Games/Valheim dedicated server");
        Assert.Equal(("/Volumes/Games/ValheimTesting/runs", "the install is on /Volumes/Games (apfs, device 16777240), not the data folder's " +
            "/ (apfs, device 2049), so macOS profiles follow the install volume"), (macServer.Path, macServer.Reason));
        Assert.Equal(1, mac.UnixProbes); // one probe for both paths
        var client = new FakeMachine("macos") { StorageError = new InvalidOperationException("a Mac client is never probed") };
        var clientRoot = ProfileRuntimeStorage.DefaultRoot(client, "/Volumes/Games/Valheim", server: false);
        Assert.Equal(("/Users/tester/Library/Application Support/ValheimTesting/runs",
            "macOS client profiles launch the source game and keep their loader under ValheimTesting's data folder"), (clientRoot.Path, clientRoot.Reason));
    }

    // The inventory's this-machine server on Linux gets that root when it is read, from one read-only probe of both paths. Its
    // verdict is not read here (#612 review 4): it needs the storage probe's trial link, which only a preflight makes, so every
    // route to a Unix verdict is the host's (HostedServerRunTests.ALinuxRuntimeOnAnotherMountIsRefusedBeforeTheLockOrAnyHostWrite).
    [Fact]
    public void ALinuxServerOnAnotherMountDefaultsToItsMountAndItsVerdictIsLeftToThePreflight()
    {
        var linux = new FakeMachine("linux");
        string server = linux.App("/mnt/games", "896660", "Valheim dedicated server", "valheim_server.x86_64");
        string file = Path.Combine(_root, "linux-server.json");
        File.WriteAllText(file, $$"""{ "environments": [ { "name": "server", "roles": ["server"], "install": "{{server}}" } ] }""");
        linux.UnixStorage["/mnt/games"] = new UnixStorageVolume("/mnt/games", "2065", "ext4", Writable: true);

        var recipe = Assert.Single(EnvironmentInventory.Read(file, linux).Environments);
        Assert.Equal("/mnt/games/ValheimTesting/runs/server", recipe.Runtime);
        Assert.False(recipe.RuntimeExplicit);
        Assert.Null(recipe.ProfileStorage);
        Assert.Equal(1, linux.UnixProbes);
        Assert.Throws<PlatformNotSupportedException>(() => ProfileRuntimeStorage.Inspect(linux, server, recipe.Runtime, explicitRuntime: false));
    }

    // #612 review: an install that is its mount's root (a Docker volume, a dedicated disk) leaves no folder on that mount outside
    // it, so the default never goes inside the install and the verdict refuses with what to do, naming `runtime`. A link the
    // kernel refuses (fs.protected_hardlinks, for an install this user does not own) is refused before staging too.
    [Fact]
    public void AnInstallAtItsMountPointAndALinkTheKernelRefusesAreRefusedBeforeStaging()
    {
        var install = new UnixStorageVolume("/srv/valheim", "2065", "ext4", Writable: true) { AtMountPoint = true };
        var data = new UnixStorageVolume("/", "2049", "ext4", Writable: true);
        var root = ProfileRuntimeStorage.DefaultRoot("/home/tester/.local/share/ValheimTesting/runs", install, data);
        Assert.Equal("/home/tester/.local/share/ValheimTesting/runs", root.Path);
        Assert.Equal("the install is the mount point of /srv/valheim (ext4, device 2065), so no folder on its mount lies outside it", root.Reason);
        Assert.Equal("the source install /srv/valheim is the mount point of /srv/valheim (ext4, device 2065), so a Linux profile, which must be on " +
            "the install's mount and outside the install, has nowhere to go. Move the install into a folder of that filesystem " +
            "(for example /srv/valheim/valheim) and set the inventory `runtime` to another folder on it.",
            ProfileRuntimeStorage.Refusal("/srv/valheim", install, root.Path, data, explicitRuntime: false));

        var games = new UnixStorageVolume("/mnt/games", "2065", "ext4", Writable: true);
        string protectedLink = Assert.IsType<string>(ProfileRuntimeStorage.Verdict("/mnt/games/valheim", games, "/mnt/games/runs", games, explicitRuntime: true,
            linkError: "ln: failed to create hard link '/mnt/games/runs/.vt-link-probe.X/game' => '/mnt/games/valheim/valheim_server.x86_64': Operation not permitted").Refusal);
        Assert.StartsWith("this user cannot hard-link the install's game files from /mnt/games/valheim into the explicit inventory runtime folder /mnt/games/runs: ln: failed", protectedLink);
        Assert.Contains("fs.protected_hardlinks (on by default) allows a link only to a file this user owns or can write", protectedLink);
        Assert.Null(ProfileRuntimeStorage.Verdict("/mnt/games/valheim", games, "/mnt/games/runs", games, explicitRuntime: true).Refusal);
    }

    // The probe's reply: the host's system first, then each path's mount. Anything else is refused, never read as a mount.
    [Fact]
    public void TheUnixStorageProbeReplyIsReadStrictly()
    {
        string mount = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("/mnt/my games"));
        string reply = $"VT-PROFILE-OS Linux\nVT-PROFILE-VOLUME 0 2065 1 0 ext4 {mount}\nVT-PROFILE-VOLUME 1 2049 0 1 tmpfs {Convert.ToBase64String("/tmp"u8)}\n" +
            $"VT-PROFILE-LINK refused {Convert.ToBase64String("ln: failed to create hard link: Operation not permitted"u8)}\nVT-PROFILE-VOLUMES\n";
        Assert.Equal("Linux", UnixStorageProbe.SystemName(reply));
        Assert.Equal("linux", UnixStorageProbe.Platform("Linux", "lab"));
        Assert.Equal("macos", UnixStorageProbe.Platform("Darwin", "lab"));
        Assert.Contains("lab runs FreeBSD", Assert.Throws<PlatformNotSupportedException>(() => UnixStorageProbe.Platform("FreeBSD", "lab")).Message);
        Assert.Equal([new UnixStorageVolume("/mnt/my games", "2065", "ext4", true), new UnixStorageVolume("/tmp", "2049", "tmpfs", false) { AtMountPoint = true }],
            UnixStorageProbe.Volumes(reply, 2));
        Assert.Equal(ProfileLinkRules.MacOS, UnixStorageProbe.Volumes(reply, 2, "macos")[0].Rules);
        Assert.Equal("ln: failed to create hard link: Operation not permitted", UnixStorageProbe.LinkRefusal(reply));
        Assert.Null(UnixStorageProbe.LinkRefusal(reply.Replace("VT-PROFILE-LINK refused", "VT-PROFILE-LINK ok x")));
        Assert.Throws<InvalidDataException>(() => UnixStorageProbe.Volumes(reply, 3));
        Assert.Throws<InvalidDataException>(() => UnixStorageProbe.Volumes(reply.Replace("VT-PROFILE-VOLUMES\n", ""), 2));
        Assert.Throws<InvalidDataException>(() => UnixStorageProbe.Volumes(reply.Replace("VT-PROFILE-VOLUME 1", "VT-PROFILE-VOLUME 2"), 2));
        Assert.Null(UnixStorageProbe.SystemName("VT-PROFILE-VOLUMES\n"));
    }

    [Theory]
    [InlineData(@"\\?\D:\Steam\Valheim", @"\\?\D:\", 3, false)]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\Steam\Valheim", @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\", 3, false)]
    [InlineData(@"\\.\X:\Steam\Valheim", @"\\.\X:\", 3, false)]
    [InlineData(@"\\server\share\Steam\Valheim", @"\\server\share\", 4, true)]
    [InlineData(@"\\?\UNC\server\share\Steam\Valheim", @"\\?\UNC\server\share\", 4, true)]
    public void WindowsStorageClassifierDistinguishesLocalDevicePathsFromNetworkShares(string path, string root,
        uint driveType, bool network)
    {
        var volume = WindowsStorageClassifier.Classify(new(path, root, driveType, "volume", "NTFS"));
        Assert.Equal(network, volume.Network);
        Assert.Equal(network ? @"\\server\share\" : root, volume.Root);
    }

    [Fact]
    public void WindowsStorageProbeClassifiesAMappedDriveBeforeRequestingItsVolumeGuid()
    {
        int volumeNameCalls = 0, fileSystemCalls = 0;
        var probes = new WindowsStorageProbe.Probes(path => path, _ => true, _ => null, _ => @"Z:\", _ => 4,
            _ => { volumeNameCalls++; throw new InvalidOperationException("SMB has no volume GUID"); },
            _ => { fileSystemCalls++; throw new InvalidOperationException("SMB has no local filesystem query"); });

        var volume = WindowsStorageProbe.Inspect(@"Z:\Steam\Valheim", probes);

        Assert.True(volume.Network);
        Assert.Equal(@"Z:\", volume.Root);
        Assert.Equal(0, volumeNameCalls);
        Assert.Equal(0, fileSystemCalls);
        string refusal = Assert.IsType<string>(ProfileRuntimeStorage.Refusal(@"Z:\Steam\Valheim", volume, @"Z:\runs", volume));
        Assert.Contains("Move the Steam library to NTFS", refusal);
    }

    [Fact] public void WithNoFileAWindowsPcIsAServerAndAClientOnThisMachine()
    {
        var pc = WindowsPc(out string game, out string server);
        var inventory = EnvironmentInventory.Read(null, pc);
        var local = inventory.Hosts["local"];
        Assert.Equal(("local", "windows", "pwsh", @"C:\Users\tester\AppData\Local\ValheimTesting\lock"), (local.Kind, local.Platform, local.Shell, local.Lock));
        var serverRecipe = Assert.Single(inventory.Environments, recipe => recipe.Name == "local-server");
        var clientRecipe = Assert.Single(inventory.Environments, recipe => recipe.Name == "local-client");
        Assert.False(serverRecipe.RuntimeExplicit);
        Assert.False(clientRecipe.RuntimeExplicit);
        Assert.Equal((server, 5688, 2486, @"D:\ValheimTesting\runs\local-server"),
            (serverRecipe.Install, serverRecipe.CliPort, serverRecipe.GamePort, serverRecipe.Runtime));
        Assert.Equal((game, 5689, 0, @"E:\ValheimTesting\runs\local-client"),
            (clientRecipe.Install, clientRecipe.CliPort, clientRecipe.GamePort, clientRecipe.Runtime));
        Assert.Equal(("local", @"C:\Users\tester\AppData\Local\ValheimTesting\leases"), (inventory.LeaseHost, inventory.LeaseDirectory));
        Assert.Empty(inventory.Missing);
        Assert.Contains(inventory.Detected, line => line.Contains(@"from registry HKCU\Software\Valve\Steam SteamPath") && line.Contains(@"E:\SteamLibrary"));
        Assert.Contains(inventory.Detected, line => line == $"Valheim (Steam app 892970): {game}");
        Assert.Contains(inventory.Detected, line => line.StartsWith("local-server (server) on local: install " + server) && line.Contains("assumed") && line.Contains("game port 2486"));
        Assert.Contains(inventory.Detected, line => line.Contains(@"runtime D:\ValheimTesting\runs\local-server") && line.Contains("profiles follow the install volume"));
        // It resolves a one-server, one-client campaign with no file at all.
        var resolved = inventory.Resolve(new HostedCampaignManifest { Server = new(), Clients = new() { ["client"] = new() } });
        Assert.Equal(["local-server", "local-client"], resolved.Assignments.Select(assignment => assignment.Environment));
    }

    // Steam registers its path in lower case with forward slashes; detection prints the folders as the disk spells them.
    [Fact] public void PathsArePrintedAsTheDiskSpellsThem()
    {
        var pc = new FakeMachine { SteamPath = "c:/program files (x86)/steam" };
        pc.Directories.Add(@"C:\Program Files (x86)\Steam");
        string game = pc.App(@"C:\Program Files (x86)\Steam", "892970", "Valheim", "valheim.exe");
        var inventory = EnvironmentInventory.Read(null, pc);
        Assert.Contains(inventory.Detected, line => line.StartsWith(@"Steam: C:\Program Files (x86)\Steam (from registry", StringComparison.Ordinal));
        Assert.Equal(game, inventory.Environments.Single().Install);
        Assert.StartsWith(@"C:\Program Files (x86)\Steam\steamapps\common", game);

        // The real file system: a case-insensitive one (Windows, macOS) gives the folder's own spelling back.
        string dir = Path.Combine(_root, "CasedFolder", "Inner");
        Directory.CreateDirectory(dir);
        string lower = Path.Combine(_root, "casedfolder", "inner");
        string spelled = new LocalSteamLocator().OnDiskPath(lower);
        Assert.Equal(OperatingSystem.IsLinux() ? lower : dir, spelled);
        Assert.Equal(Path.Combine(_root, "missing"), new LocalSteamLocator().OnDiskPath(Path.Combine(_root, "missing")));
    }

    // #709: the root is only ever the native default. On Windows that is the registered SteamPath alone: Steam in Program Files
    // (x86) that never registered is not searched, and a registered folder that is gone is named as such.
    [Fact] public void OnWindowsOnlyTheRegisteredSteamPathIsTheRoot()
    {
        var pc = new FakeMachine();
        pc.App(@"C:\Program Files (x86)\Steam", "892970", "Valheim", "valheim.exe");
        var refused = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(null, pc));
        Assert.Contains(@"Steam was not found: HKCU\Software\Valve\Steam SteamPath is not set (Steam has not run for this user).", refused.Message);
        pc.SteamPath = "d:/gone/steam";
        refused = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(null, pc));
        Assert.Contains(@"HKCU\Software\Valve\Steam SteamPath names d:\gone\steam, which does not exist", refused.Message);
    }

    // Each platform's one default root, and nothing else: Linux's ~/.local/share/Steam and the Flatpak's are not searched.
    [Theory]
    [InlineData("windows", @"C:\Games\Steam")]
    [InlineData("macos", "/Users/tester/Library/Application Support/Steam")]
    [InlineData("linux", "/home/tester/.steam/steam")]
    public void EachPlatformFindsSteamAtItsDefaultRootOnly(string platform, string root)
    {
        var machine = new FakeMachine(platform) { SteamPath = platform == "windows" ? root.Replace('\\', '/') : null };
        machine.Directories.Add(root);
        var steam = SteamDetection.Find(machine);
        Assert.Equal((root, (string?)null, (string?)null), (steam.Root, steam.Reason, steam.NotFound));
        Assert.Equal(platform == "windows" ? @"registry HKCU\Software\Valve\Steam SteamPath" : "standard path", steam.RootRule);

        var elsewhere = new FakeMachine(platform);
        foreach (string other in new[] { @"C:\Program Files (x86)\Steam", "/home/tester/.local/share/Steam", "/home/tester/.var/app/com.valvesoftware.Steam/.local/share/Steam" })
            elsewhere.Directories.Add(other);
        var missing = SteamDetection.Find(elsewhere);
        Assert.Null(missing.Root);
        // One actionable message: why, and how to give Steam in another folder explicitly.
        Assert.StartsWith("Steam was not found: ", missing.NotFound);
        Assert.EndsWith("Steam in another folder (a Flatpak or a custom one) is not searched: list the environment in an inventory file with its install.", missing.NotFound);
        if (platform != "windows") Assert.Contains($"its standard path {root} does not exist", missing.NotFound);
    }

    // Linux's ~/.steam/steam links to the folder Steam's libraryfolders.vdf lists under its own name: one library, not two.
    [Fact] public void ALinkedRootIsOneLibrary()
    {
        var linux = new FakeMachine("linux");
        linux.Directories.Add("/home/tester/.steam/steam");
        linux.Links["/home/tester/.steam/steam"] = "/home/tester/.local/share/Steam";
        linux.File("/home/tester/.steam/steam/steamapps/libraryfolders.vdf",
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/tester/.local/share/Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/mnt/games\"\n\t}\n}\n");
        Assert.Equal(["/home/tester/.steam/steam", "/mnt/games"], SteamDetection.Find(linux).Libraries);
    }

    // This machine's Steam connection_log is found under the same root, with no library read.
    [Fact] public void TheLocalSteamLogIsUnderTheDetectedRoot()
    {
        var mac = new FakeMachine("macos");
        string log = "/Users/tester/Library/Application Support/Steam/logs/connection_log.txt";
        using (EnvironmentInventory.UseMachine(mac)) Assert.Null(SteamSessionLog.LocalPath());
        mac.File(log);
        using (EnvironmentInventory.UseMachine(mac)) Assert.Equal(log, SteamSessionLog.LocalPath());
    }

    // Linux hosts run the dedicated server only (#708): an inventory listing a client on one is refused when it is read, so no
    // Steam account check ever asks a Linux host (#740).
    [Fact] public void AClientRecipeOnALinuxHostIsRefused()
    {
        string file = Path.Combine(_root, "linux-client.json");
        File.WriteAllText(file, """
            {
              "hosts": { "lab": { "kind": "ssh", "platform": "linux", "shell": "bash", "destination": "tester@lab", "lock": "/vt/lock" } },
              "environments": [ { "name": "lab-client", "host": "lab", "roles": ["client"], "install": "/opt/valheim", "runtime": "/vt/runs", "cliPort": 5700 } ],
              "leaseHost": "lab", "leaseDirectory": "/vt/leases"
            }
            """);
        var refused = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(file, new FakeMachine()));
        Assert.Contains("The client lab-client: Linux hosts run the dedicated server only; start the client on Windows or macOS.", refused.Message);
    }

    // An inventory file's install is the explicit override: with no Steam root found, the listed environment is used as written.
    [Fact] public void AnInventoryInstallIsUsedWhenSteamIsElsewhere()
    {
        var linux = new FakeMachine("linux");
        string flatpak = "/home/tester/.var/app/com.valvesoftware.Steam/.local/share/Steam";
        string server = linux.App(flatpak, "896660", "Valheim dedicated server", "valheim_server.x86_64");
        string file = Path.Combine(_root, "environments.json");
        File.WriteAllText(file, $$"""{ "environments": [ { "name": "flatpak-server", "roles": ["server"], "install": "{{server}}" } ] }""");
        var inventory = EnvironmentInventory.Read(file, linux);
        Assert.Equal(server, Assert.Single(inventory.Environments).Install);
        Assert.Contains("Steam: not found: its standard path /home/tester/.steam/steam does not exist", inventory.Detected);
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

    [Fact] public void NoSteamAtAllRefusesWithTheRootTried()
    {
        var error = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(null, new FakeMachine()));
        Assert.Contains("This machine has none", error.Message);
        Assert.Contains("Steam was not found", error.Message);
        var inventory = Assert.Throws<ArgumentException>(() => EnvironmentInventory.Read(null, new FakeMachine("macos")));
        Assert.Contains("Valheim (Steam app 892970)", inventory.Message);
    }

    // A Linux host finds its dedicated server only (it runs no client, even one a Steam install has); a Mac finds both through Steam.
    [Fact] public void LinuxAndMacInstallsAreDetectedToo()
    {
        var linux = new FakeMachine("linux");
        linux.Directories.Add("/home/tester/.steam/steam");
        linux.App("/home/tester/.steam/steam", "892970", "Valheim", "valheim.x86_64");
        linux.App("/home/tester/.steam/steam", "896660", "Valheim dedicated server", "valheim_server.x86_64");
        var inventory = EnvironmentInventory.Read(null, linux);
        Assert.Equal(["local-server"], inventory.Environments.Select(recipe => recipe.Name));
        Assert.Contains("Linux hosts run the dedicated server only; start the client on Windows or macOS.", inventory.Missing);
        Assert.Equal("bash", inventory.Hosts["local"].Shell);

        var mac = new FakeMachine("macos") { OsArchitecture = Architecture.Arm64 };
        mac.App("/Users/tester/Library/Application Support/Steam", "892970", "Valheim", "Valheim.app/Contents/MacOS/Valheim");
        mac.App("/Users/tester/Library/Application Support/Steam", "896660", "Valheim dedicated server", "valheim_server/Valheim");
        var macInventory = EnvironmentInventory.Read(null, mac);
        Assert.Equal(["local-server", "local-client"], macInventory.Environments.Select(recipe => recipe.Name));
        Assert.Equal("/Users/tester/Library/Application Support/Steam/steamapps/common/Valheim dedicated server", macInventory.Environments[0].Install);
        Assert.Equal("arm64", macInventory.Environments[1].Architecture);
        Assert.Contains(macInventory.Detected, line => line.Contains("assumed client architecture arm64", StringComparison.Ordinal));

        if (OperatingSystem.IsMacOS())
        {
            var server = macInventory.Environments[0];
            var role = new GameRole { Host = "local", Install = server.Install, Runtime = server.Runtime,
                CliPort = server.CliPort, GamePort = server.GamePort };
            var plan = new ServerRunPlan { Executable = GameLaunch.ServerMacExecutable, Port = role.CliPort,
                Arguments = ["-batchmode", "-nographics", "-port", role.GamePort.ToString(System.Globalization.CultureInfo.InvariantCulture)] };
            Assert.Null(HostedServerRun.Refusal(macInventory.Hosts["local"], role, plan));
            plan.Crossplay = true;
            Assert.Contains("macOS crossplay", HostedServerRun.Refusal(macInventory.Hosts["local"], role, plan));
        }
    }

    // A file lists the environments to use: one on this machine needs only its name and role, and the file gains nothing else.
    [Fact] public void AFileListsItsEnvironmentsAndALocalOneNeedsOnlyItsNameAndRoles()
    {
        var pc = WindowsPc(out string game, out string server);
        string file = Path.Combine(_root, "environments.json");
        File.WriteAllText(file, """
            {
              "hosts": { "lab": { "kind": "ssh", "platform": "windows", "shell": "pwsh", "destination": "tester@lab", "lock": "C:\\vt\\lock" } },
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
            "hosts": { "lab": { "kind": "ssh", "platform": "windows", "shell": "pwsh", "destination": "tester@lab", "lock": "C:\\vt\\lock" } },
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
        Assert.Equal(("windows", "pwsh"), (inventory.Hosts["local"].Platform, inventory.Hosts["local"].Shell));
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

// #709: a host script's one copy of the Steam-root rule per shell, run through this machine's real shell (bash, or pwsh on
// Windows). A root named explicitly wins; with none, bash takes macOS's standard path under HOME (pointed at a temporary folder
// here; a Linux host runs no client script) and pwsh the user's registered SteamPath, read only and compared with the C# owner's own reading of it.
public sealed class SteamRootScriptTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly TempDirectory _home = new();
    public void Dispose() => _home.Dispose();

    private static async Task<string> RootAsync(string steam, string home)
    {
        var host = new LocalGameHost("here", HostShell.ThisMachine);
        string script = host.Shell.Kind == HostShellKind.Bash
            ? "HOME=$fakehome\n" + SteamDetection.BashRoot + "echo \"VT-STEAMROOT [$steam]\"\n"
            : SteamDetection.PowerShellRoot + "'VT-STEAMROOT [' + $steam + ']'\n";
        var reply = (await host.RunAsync(script, new Dictionary<string, string> { ["steam"] = steam, ["fakehome"] = home }, Timeout))
            .EnsureSuccess("Finding the Steam root");
        var match = System.Text.RegularExpressions.Regex.Match(reply.Stdout, @"VT-STEAMROOT \[(.*)\]");
        Assert.True(match.Success, reply.Stdout);
        return match.Groups[1].Value;
    }

    [Fact] public async Task AnExplicitRootWins()
    {
        string given = Path.Combine(_home.Path, "Flatpak Steam");
        // Windows Steam writes its path with forward slashes; the script's root has backslashes either way.
        Assert.Equal(given, await RootAsync(OperatingSystem.IsWindows() ? given.Replace('\\', '/') : given, _home.Path));
    }

    [Fact] public async Task WithNoneGivenTheRootIsThisPlatformsDefault()
    {
        string expected = OperatingSystem.IsWindows() ? new LocalSteamLocator().RegistrySteamPath()?.Replace('/', '\\') ?? ""
            : _home.Path + "/Library/Application Support/Steam";
        Assert.Equal(expected, await RootAsync("", _home.Path));
    }
}
