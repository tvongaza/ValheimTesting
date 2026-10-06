using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Valheim.Testing.Game;
using Xunit;

// The BepInExPack valheim-test ships for an install whose own Doorstop proxy and configuration do not match (#256): which
// installs take it, how it is extracted once and reused, and what is refused. Fake files only; nothing launches.
public sealed class ShippedLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shipped-loader-" + Guid.NewGuid().ToString("N"));
    private const string Doorstop4 = "[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n";
    private const string Doorstop3 = "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n";

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private string Install(string name, string proxy, string config)
    {
        string install = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.dll"), "core");
        File.WriteAllText(Path.Combine(install, "winhttp.dll"), proxy);
        File.WriteAllText(Path.Combine(install, "doorstop_config.ini"), config);
        return install;
    }

    // A BepInExPack-shaped zip: Thunderstore metadata at the top, the game files under the pack's folder.
    private static byte[] Pack(params (string Name, string Text)[] extra)
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

    private static ShippedLoader.LoaderPin Pin(byte[] zip) =>
        new("BepInExPack_Valheim", "5.4.2351", Convert.ToHexStringLower(SHA256.HashData(zip)), "BepInExPack_Valheim");

    [Fact] public void OnlyAMismatchedOrUnrecognisedDoorstopPairIsTheShippedLoadersCase()
    {
        Assert.Null(BepInExLoaderPackage.DoorstopMismatch(Install("coherent4", "MZ target_assembly", Doorstop4)));
        Assert.Null(BepInExLoaderPackage.DoorstopMismatch(Install("coherent3", "MZ targetAssembly", Doorstop3)));
        // Gale's Doorstop 4 proxy beside BepInExPack's Doorstop 3 file, the PC's own case.
        Assert.Contains("Doorstop 4", BepInExLoaderPackage.DoorstopMismatch(Install("gale", "MZ target_assembly", Doorstop3)));
        Assert.Contains("Doorstop 3", BepInExLoaderPackage.DoorstopMismatch(Install("old-proxy", "MZ targetAssembly", Doorstop4)));
        Assert.Contains("not a Doorstop proxy this check recognises", BepInExLoaderPackage.DoorstopMismatch(Install("unknown", "MZ neither", Doorstop4)));
        // Any other loader fault is the install's own refusal, not this case: a disabled loader, a redirected target, no pair at all.
        Assert.Null(BepInExLoaderPackage.DoorstopMismatch(Install("disabled", "MZ target_assembly", Doorstop4.Replace("enabled = true", "enabled = false"))));
        Assert.Null(BepInExLoaderPackage.DoorstopMismatch(Install("redirected", "MZ target_assembly", Doorstop4.Replace("BepInEx.Preloader.dll", "Other.dll"))));
        string bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);
        Assert.Null(BepInExLoaderPackage.DoorstopMismatch(bare));
    }

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

    [Fact] public void AnotherZipOrAnEntryOutsideThePackFolderIsRefused()
    {
        byte[] zip = Pack();
        var wrong = Pin(zip) with { Sha256 = new string('0', 64) };
        Assert.Contains("not the pinned", Assert.Throws<InvalidDataException>(() => ShippedLoader.Extract(new MemoryStream(zip), wrong, Path.Combine(_root, "d1"))).Message);
        byte[] escaping = Pack(("BepInExPack_Valheim/../escaped.txt", "x"));
        Assert.Contains("outside its folder", Assert.Throws<InvalidDataException>(() => ShippedLoader.Extract(new MemoryStream(escaping), Pin(escaping), Path.Combine(_root, "d2"))).Message);
        Assert.False(File.Exists(Path.Combine(_root, "d2", "loader", "escaped.txt")));
    }

    // A real BepInEx.dll of that assembly version in the install's core (the fakes above are text, which reads as unknown).
    private static string WithCore(string install, Version version)
    {
        var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("BepInEx") { Version = version }, typeof(object).Assembly);
        builder.DefineDynamicModule("BepInEx.dll");
        builder.Save(Path.Combine(install, "BepInEx", "core", "BepInEx.dll"));
        return install;
    }

    // BepInEx before 5.4.23.5 cannot reach Unity 6's log writer (BepInEx/BepInEx#755, fixed by #1264): its install takes the
    // shipped pack even with a coherent Doorstop pair; 5.4.23.5 and later, another major and an unreadable core keep their own.
    [Theory]
    [InlineData("5.4.22.0", true)]
    [InlineData("5.4.23.4", true)]
    [InlineData("5.4.23.5", false)]
    [InlineData("5.4.24.0", false)]
    [InlineData("6.0.0.0", false)]
    public void AnInstallWithBepInExBefore54235TakesTheShippedPack(string version, bool takes)
    {
        byte[] zip = Pack();
        string install = WithCore(Install("core-" + version, "MZ target_assembly", Doorstop4), Version.Parse(version));
        var choice = ShippedLoader.Instead("server", install, () => (new MemoryStream(zip), Pin(zip)), Path.Combine(_root, "data"));
        Assert.Equal(takes, choice != null);
        if (choice == null) return;
        Assert.Contains($"BepInEx {version} is older than 5.4.23.5", choice.Reason);
        Assert.Contains("BepInEx/BepInEx#755", choice.Reason);
        Assert.DoesNotContain("Doorstop proxy", choice.Reason);
        Assert.Contains("the install is not changed", choice.Reason);
    }

    // A macOS install keeps its own (native arm64) loader whatever its BepInEx: the pack's Doorstop library is x64.
    [Fact] public void AMacInstallWithAnOldBepInExKeepsItsOwnLoader()
    {
        byte[] zip = Pack();
        string install = WithCore(Install("mac-old", "MZ target_assembly", Doorstop4), new Version(5, 4, 22, 0));
        Directory.CreateDirectory(Path.Combine(install, GameLaunch.ClientMacBundle));
        Assert.Null(ShippedLoader.Instead("client", install, () => (new MemoryStream(zip), Pin(zip)), Path.Combine(_root, "data")));
    }

    [Fact] public void AnOldBepInExBehindAMismatchedPairNamesBothReasons()
    {
        byte[] zip = Pack();
        string install = WithCore(Install("gale-old", "MZ target_assembly", Doorstop3), new Version(5, 4, 22, 0));
        var choice = ShippedLoader.Instead("client", install, () => (new MemoryStream(zip), Pin(zip)), Path.Combine(_root, "data"));
        Assert.Contains("Doorstop proxy and configuration do not match", choice!.Reason);
        Assert.Contains("BepInEx 5.4.22.0 is older than 5.4.23.5", choice.Reason);
    }

    [Fact] public void AMismatchedInstallTakesTheShippedPackAndACoherentOneKeepsItsOwn()
    {
        byte[] zip = Pack();
        string data = Path.Combine(_root, "data");
        int asked = 0;
        (Stream, ShippedLoader.LoaderPin)? Shipped() { asked++; return (new MemoryStream(zip), Pin(zip)); }
        var choice = ShippedLoader.Instead("client", Install("gale", "MZ target_assembly", Doorstop3), Shipped, data);
        Assert.NotNull(choice);
        Assert.Contains("Doorstop 4", choice!.Reason);
        Assert.Contains("BepInExPack_Valheim 5.4.2351", choice.Reason);
        Assert.Contains("the install is not changed", choice.Reason);
        Assert.Equal("MZ target_assembly", File.ReadAllText(Path.Combine(_root, "gale", "winhttp.dll")));
        Assert.Equal("doorstop_config.ini", Path.GetFileName(Directory.GetFiles(BepInExLoaderPackage.Read(choice.Manifest).Root, "*.ini").Single()));

        Assert.Null(ShippedLoader.Instead("client", Install("coherent", "MZ target_assembly", Doorstop4), Shipped, data));
        Assert.Equal(1, asked); // a coherent install never opens the shipped pack
        // A build that carries no pack leaves the mismatch to the preflight's own refusal.
        Assert.Null(ShippedLoader.Instead("client", Install("gale2", "MZ target_assembly", Doorstop3), () => null, data));
    }
}
