using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// The pinned BepInExPack every Windows and Linux profile runs on (#711): which platform takes which manifest, how the pack is
// extracted once and reused, and what is refused. Fake files only; nothing launches.
public sealed class ShippedLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shipped-loader-" + Guid.NewGuid().ToString("N"));
    private const string Doorstop4 = "[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n";

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    // A BepInExPack-shaped zip: Thunderstore metadata at the top, the game files under the pack's folder.
    internal static byte[] Pack(params (string Name, string Text)[] extra)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
            {
                ("manifest.json", "{}"), ("BepInExPack_Valheim/", ""),
                ("BepInExPack_Valheim/winhttp.dll", "MZ target_assembly"), ("BepInExPack_Valheim/doorstop_config.ini", Doorstop4),
                ("BepInExPack_Valheim/BepInEx/core/BepInEx.Preloader.dll", "preloader"), ("BepInExPack_Valheim/BepInEx/core/BepInEx.dll", "core"),
                ("BepInExPack_Valheim/BepInEx/config/BepInEx.cfg", "[Logging]\n"),
                ("BepInExPack_Valheim/doorstop_libs/libdoorstop_x64.so", "linux doorstop"),
            }.Concat(extra))
            {
                var entry = zip.CreateEntry(name);
                if (name.EndsWith('/')) continue;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    internal static ShippedLoader.LoaderPin Pin(byte[] zip) =>
        new("BepInExPack_Valheim", "5.4.2351", Convert.ToHexStringLower(SHA256.HashData(zip)), "BepInExPack_Valheim");

    [Fact] public void TheShippedPackIsExtractedOnceReusedAndReplacedWhenAFileChanged()
    {
        byte[] zip = Pack(("BepInExPack_Valheim/changelog.txt", "not a loader file"));
        string data = Path.Combine(_root, "data");
        string manifest = ShippedLoader.Extract(new MemoryStream(zip), Pin(zip), data);
        var package = BepInExLoaderPackage.Read(manifest);
        Assert.Equal("BepInExPack_Valheim", package.Name);
        Assert.Equal(Path.GetDirectoryName(manifest), package.Root); // the folder it lives in, so it can move whole
        Assert.False(File.Exists(Path.Combine(package.Root, "manifest.json"))); // only the pack's own folder is extracted
        Assert.True(File.Exists(Path.Combine(package.Root, "winhttp.dll")));
        string stamp = Path.Combine(package.Root, "reused");
        File.WriteAllText(stamp, "");
        Assert.Equal(manifest, ShippedLoader.Extract(new MemoryStream(zip), Pin(zip), data));
        Assert.True(File.Exists(stamp)); // reused, not extracted again

        // A changed loader file fails the package's own check: the pack is extracted again and swapped in.
        File.WriteAllText(Path.Combine(package.Root, "winhttp.dll"), "MZ target_assembly tampered");
        Assert.Equal(manifest, ShippedLoader.Extract(new MemoryStream(zip), Pin(zip), data));
        Assert.False(File.Exists(stamp));
        Assert.Equal("MZ target_assembly", File.ReadAllText(Path.Combine(package.Root, "winhttp.dll")));
        BepInExLoaderPackage.Read(manifest);
        Assert.Single(Directory.GetDirectories(Path.Combine(data, "loader"))); // no staging or old copies left
    }

    // Two valheim-test runs starting together on one machine (#419, the race #418 fixed in CliBundle): every run is handed
    // the one copy, none throws, the copy is intact, and no staging or set-aside copy is left beside it. Alternate rounds
    // start from no copy and from a damaged one.
    [Fact] public void ManyRunsExtractingAtOnceNeverFailAndLeaveOneIntactCopy()
    {
        byte[] zip = Pack(("BepInExPack_Valheim/changelog.txt", "not a loader file"));
        var pin = Pin(zip);
        const int Runs = 8, Rounds = 24;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var wrong = new List<string>();
        for (int round = 0; round < Rounds; round++)
        {
            string data = Path.Combine(_root, "stress-" + round);
            string expected = Path.Combine(data, "loader", $"{pin.Name}-{pin.Version}-{pin.Sha256[..12]}", "loader.json");
            if (round % 2 == 1)
            {
                ShippedLoader.Extract(new MemoryStream(zip), pin, data);
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(expected)!, "winhttp.dll"), "damaged");
            }
            using var start = new Barrier(Runs);
            var manifests = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var threads = Enumerable.Range(0, Runs).Select(_ => new Thread(() =>
            {
                start.SignalAndWait();
                try { manifests.Enqueue(ShippedLoader.Extract(new MemoryStream(zip), pin, data)); }
                catch (Exception error) { failures.Enqueue(error); }
            })).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());
            if (manifests.Any(manifest => manifest != expected)) wrong.Add($"round {round}: a run was handed another copy");
            try
            {
                var package = BepInExLoaderPackage.Read(expected);
                if (File.ReadAllText(Path.Combine(package.Root, "winhttp.dll")) != "MZ target_assembly") wrong.Add($"round {round}: the copy is not intact");
            }
            catch (Exception error) { wrong.Add($"round {round}: the copy is not usable: {error.Message}"); }
            var left = Directory.GetDirectories(Path.Combine(data, "loader")).Where(folder => folder != Path.GetDirectoryName(expected)).Select(Path.GetFileName).ToList();
            if (left.Count != 0) wrong.Add($"round {round}: left beside the copy: {string.Join(", ", left)}");
        }
        Assert.True(failures.IsEmpty && wrong.Count == 0, $"{failures.Count} of {Runs * Rounds} runs threw; first: {failures.FirstOrDefault()}\n" + string.Join("\n", wrong.Take(10)));
    }

    [Fact] public void AnotherZipOrAnEntryOutsideThePackFolderIsRefused()
    {
        byte[] zip = Pack();
        var wrong = Pin(zip) with { Sha256 = new string('0', 64) };
        Assert.Contains("not the pinned", Assert.Throws<InvalidDataException>(() => ShippedLoader.Extract(new MemoryStream(zip), wrong, Path.Combine(_root, "d1"))).Message);
        byte[] escaping = Pack(("BepInExPack_Valheim/../escaped.txt", "x"));
        Assert.Contains("outside its folder", Assert.Throws<InvalidDataException>(() => ShippedLoader.Extract(new MemoryStream(escaping), Pin(escaping), Path.Combine(_root, "d2"))).Message);
        Assert.False(File.Exists(Path.Combine(_root, "d2", "loader", "escaped.txt")));
    }

    // #711: a Windows profile takes the pack with its proxy; a Linux profile the same pack without the Windows proxy and its
    // configuration (a bash host refuses them); a macOS profile keeps its own. The line names the pin, its SHA-256 and source.
    [Fact] public void WindowsAndLinuxProfilesRunOnThePinnedPackAndMacOSKeepsItsOwn()
    {
        byte[] zip = Pack();
        string data = Path.Combine(_root, "data");
        int opened = 0;
        var pinned = new ShippedLoader.Pinned(Pin(zip), "the test pin", () => { opened++; return new MemoryStream(zip); });
        var windows = ShippedLoader.For("client", "C:/game", "windows", pinned, data)!;
        var package = BepInExLoaderPackage.Read(windows.Manifest);
        Assert.Contains("winhttp.dll", package.Files.Keys);
        Assert.Contains("C:/game's disposable profile runs on the pinned BepInExPack_Valheim 5.4.2351", windows.Reason);
        Assert.Contains($"sha256 {Pin(zip).Sha256}, from the test pin", windows.Reason);
        Assert.Contains("the install's own loader is not used", windows.Reason);

        var linux = ShippedLoader.For("server", "/srv/valheim", "linux", pinned, data)!;
        Assert.Equal(ShippedLoader.LinuxManifest, Path.GetFileName(linux.Manifest));
        var linuxPackage = BepInExLoaderPackage.Read(linux.Manifest);
        Assert.DoesNotContain("winhttp.dll", linuxPackage.Files.Keys);
        Assert.DoesNotContain("doorstop_config.ini", linuxPackage.Files.Keys);
        Assert.Contains("doorstop_libs/libdoorstop_x64.so", linuxPackage.Files.Keys);
        linuxPackage.RequireFor(ClientPlatform.Linux, "test");

        Assert.Null(ShippedLoader.For("client", "/Applications/Valheim", "macos", pinned, data));
        Assert.Equal(2, opened); // macOS never opens the pack
        Assert.Throws<ArgumentException>(() => ShippedLoader.For("client", "x", "beos", pinned, data));
    }

    // A build without the pack refuses a Windows or Linux profile rather than falling back to the install's loader.
    [Fact] public void APinWithoutItsZipIsRefusedNotReplacedByTheInstallsLoader()
    {
        var pinned = new ShippedLoader.Pinned(Pin(Pack()), "the test pin", () => null);
        Assert.Contains("never runs on its install's own loader", Assert.Throws<InvalidDataException>(() =>
            ShippedLoader.For("client", "C:/game", "windows", pinned, Path.Combine(_root, "data"))).Message);
        Assert.Null(ShippedLoader.For("client", "/game", "macos", pinned, Path.Combine(_root, "data")));
    }

    // The pin is loader-dependency.json, embedded with the build.
    [Fact] public void ThePinIsTheEmbeddedLoaderDependency()
    {
        var pinned = ShippedLoader.BuiltIn();
        using var repository = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureProjects.RepositoryRoot(), "loader-dependency.json")));
        string Text(string name) => repository.RootElement.GetProperty(name).GetString()!;
        Assert.Equal(new ShippedLoader.LoaderPin(Text("name"), Text("version"), Text("sha256"), Text("root")), pinned.Pin);
        Assert.Equal("loader-dependency.json", pinned.Source);
    }
}
