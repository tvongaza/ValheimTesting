using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Valheim.Testing.Game;
using Xunit;

// One owner for the one-plugin ValheimCLI contract (#670): CliCapabilityManifest.Read alone refuses anything but one
// valheimCLI.dll declaring valheimCLI.valheimCLI, so every command that reads a manifest refuses an old split one with the same
// error; CliCapabilityManifest names the plugin once; RegressionInputs.Validate checks fields only. No game, Steam or network.
public sealed class OneManifestRefusalTests : IDisposable
{
    private readonly RegressionRig _rig = new();
    public void Dispose() => _rig.Dispose();

    /// <summary>Turns the one-plugin manifest at <paramref name="path"/> into a split core-and-pack one (two plugin files).</summary>
    internal static void MakeSplit(string path)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["files"]!.AsArray().Add(new JsonObject
        {
            ["file"] = "Valheim.Cli.Standard.dll", ["sha256"] = new string('a', 64), ["plugins"] = new JsonArray("valheimCLI.standard"),
            ["extensions"] = new JsonObject { ["valheim.world"] = new JsonObject { ["terrain"] = 1 } },
        });
        File.WriteAllText(path, manifest.ToJsonString());
    }

    // What Read says about a split manifest, after its "<path> is malformed: " prefix: every other command must say the same.
    private const string SplitRefusal = "files lists valheimCLI.dll (valheimCLI.valheimCLI), Valheim.Cli.Standard.dll (valheimCLI.standard): not a one-plugin build. " +
        "This toolkit reads one valheimCLI.dll declaring valheimCLI.valheimCLI; use one pinned valheimCLI.dll and its matching capability manifest.";

    [Fact] public void ASplitManifestGetsTheSameErrorFromEveryCommand()
    {
        string split = _rig.CliManifest(save: true);
        MakeSplit(split);

        // The one owner.
        var read = Assert.Throws<InvalidDataException>(() => CliCapabilityManifest.Read(split));
        Assert.Equal($"The ValheimCLI capability manifest {split} is malformed: {SplitRefusal}", read.Message);

        // An owned client's plan (valheim-test start's client, a hosted ClientRounds), before a copy is prepared.
        var plan = new ClientRunPlan { Mode = "owned", Install = _rig.Game, Port = 5556, Join = "127.0.0.1:2456", Character = "Tester", CliManifest = split };
        Assert.Equal(read.Message, Assert.Throws<InvalidDataException>(() => plan.CheckCliManifest()).Message);

        // The dependency resolver of the one-shot commands (server-load, smoke and their A/B comparisons).
        var request = Request(_rig.CliManifest(save: true));
        request.CliManifest = split;
        Assert.Equal(read.Message, Assert.Throws<InvalidDataException>(() => NativeDependencyResolver.Resolve(request)).Message);

        // A saved dependency lock holding a split manifest: the same refusal, naming the lock it came from.
        var ready = NativeDependencyResolver.Resolve(Request(_rig.CliManifest(save: true)));
        Assert.True(ready.Ready);
        string lockPath = Path.Combine(_rig.Root, "split-lock.json");
        ready.Write(lockPath);
        var saved = JsonNode.Parse(File.ReadAllText(lockPath))!;
        File.WriteAllText(split + ".plugin.json", saved["cliManifest"]!.ToJsonString());
        MakeSplit(split + ".plugin.json");
        saved["cliManifest"] = JsonNode.Parse(File.ReadAllText(split + ".plugin.json"));
        File.WriteAllText(lockPath, saved.ToJsonString());
        Assert.Equal($"The ValheimCLI capability manifest in the dependency lock {lockPath} is malformed: {SplitRefusal}",
            Assert.Throws<InvalidDataException>(() => NativeDependencyLock.ReadReady(lockPath)).Message);

        // A ValheimCLI bundle whose manifest is split, with the pack beside the plugin.
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(split, CliBundle.ManifestFile);
            archive.CreateEntryFromFile(Path.Combine(_rig.Root, "cli", "valheimCLI.dll"), "valheimCLI.dll");
            archive.CreateEntry("Valheim.Cli.Standard.dll").Open().Dispose();
        }
        byte[] zip = buffer.ToArray();
        string data = Path.Combine(_rig.Root, "data");
        var bundle = Assert.Throws<InvalidDataException>(() => CliBundle.Extract(new MemoryStream(zip), FileHash.Sha256(zip), "80fb6ce", "split", data));
        Assert.StartsWith("The ValheimCLI bundle for 80fb6ce (split) is refused. The ValheimCLI capability manifest ", bundle.Message);
        Assert.EndsWith($" is malformed: {SplitRefusal}", bundle.Message);
        Assert.False(Directory.Exists(Path.Combine(data, "cli", "80fb6ce")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(data, "cli"), "*.extract-*"));
    }

    // A targeted regression reads its cli.manifest through the owner when it is made, before the run changes anything.
    [Fact] public void ATargetedRegressionRefusesASplitOrMissingManifestWhenItIsMade()
    {
        var inputs = _rig.Manifest();
        MakeSplit(inputs.Cli.Manifest!);
        inputs.Validate(); // A field check: it reads no manifest.
        var error = Assert.Throws<InvalidDataException>(() => _rig.Regression(inputs));
        Assert.Equal($"The ValheimCLI capability manifest {inputs.Cli.Manifest} is malformed: {SplitRefusal}", error.Message);
        inputs.Cli.Manifest = Path.Combine(_rig.Root, "missing-cli-manifest.json");
        Assert.Contains("missing-cli-manifest.json does not exist", Assert.Throws<FileNotFoundException>(() => _rig.Regression(inputs)).Message);
    }

    // A one-plugin manifest with a stray pack DLL beside the plugin in the zip: the bundle is that plugin and no other DLL.
    [Fact] public void ABundleWithAStrayDllBesideItsPluginIsRefused()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(_rig.CliManifest(save: true), CliBundle.ManifestFile);
            archive.CreateEntryFromFile(Path.Combine(_rig.Root, "cli", "valheimCLI.dll"), "valheimCLI.dll");
            archive.CreateEntry("Valheim.Cli.Standard.dll").Open().Dispose();
        }
        byte[] zip = buffer.ToArray();
        string data = Path.Combine(_rig.Root, "data");
        Assert.Equal("The ValheimCLI bundle holds Valheim.Cli.Standard.dll beside its manifest's valheimCLI.dll; a bundle is the one plugin, its manifest and its license.",
            Assert.Throws<InvalidDataException>(() => CliBundle.Extract(new MemoryStream(zip), FileHash.Sha256(zip), "80fb6ce", "stray", data)).Message);
        Assert.False(Directory.Exists(Path.Combine(data, "cli", "80fb6ce")));
    }

    // A zip without its manifest is not a bundle: refused as such, not as a missing file in a deleted staging folder.
    [Fact] public void ABundleWithoutItsManifestIsNotABundle()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            archive.CreateEntryFromFile(Path.Combine(_rig.Root, "cli", "valheimCLI.dll"), "valheimCLI.dll");
        byte[] zip = buffer.ToArray();
        string data = Path.Combine(_rig.Root, "data");
        Assert.Equal("The ValheimCLI bundle holds no cli-manifest.json; it is not a bundle.",
            Assert.Throws<InvalidDataException>(() => CliBundle.Extract(new MemoryStream(zip), FileHash.Sha256(zip), "80fb6ce", "no manifest", data)).Message);
        Assert.Empty(Directory.GetDirectories(Path.Combine(data, "cli"), "*.extract-*"));
    }

    [Fact] public void RegressionInputsValidationReadsNoFile()
    {
        var inputs = _rig.Manifest();
        string missing = Path.Combine(_rig.Root, "no-such-folder", "cli-manifest.json");
        inputs.Cli.Manifest = missing;
        inputs.Validate();
        inputs.Cli.Validate();
        Assert.False(File.Exists(missing));
        // Its own field rules still hold.
        inputs.Cli.Manifest = "cli-manifest.json";
        Assert.Contains("cli.manifest: give the capability manifest's path", Assert.Throws<ArgumentException>(inputs.Validate).Message);
        inputs.Cli.Manifest = null;
        Assert.Contains("cli.manifest: name the capability manifest", Assert.Throws<ArgumentException>(inputs.Validate).Message);
    }

    // The manifest models one plugin: no public member hands out a list of files to select from.
    [Fact] public void TheManifestModelsExactlyOnePlugin()
    {
        var members = typeof(CliCapabilityManifest).GetProperties().Where(property => property.PropertyType != typeof(string) &&
            property.PropertyType.GetInterfaces().Any(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                type.GetGenericArguments()[0] == typeof(CliManifestFile))).Select(property => property.Name);
        Assert.Empty(members);
        Assert.Equal(typeof(CliManifestFile), typeof(CliCapabilityManifest).GetProperty(nameof(CliCapabilityManifest.Plugin))!.PropertyType);
        Assert.DoesNotContain(typeof(CliCapabilityManifest).GetMethods(), method => method.Name is "ForCapabilities" or "RequireDefaultPlugin");
        Assert.DoesNotContain(typeof(NativeDependencyRequest).GetProperties(), property => property.Name == "RequireToolkitCommands");
    }

    // One constant names the core plugin and one place decides "one-plugin build": no other toolkit source holds the plugin's
    // GUID in any string, the file name as a value (a literal that is exactly it; prose in a message may mention it), or the refusal. Comments are not code; SessionAdapter is game-side source compiled
    // against the game by SmokeAdapter, which cannot reference the toolkit.
    [Fact] public void NoOtherCallerRepeatsThePluginIdentityOrTheOnePluginRule()
    {
        string root = FixtureProjects.RepositoryRoot(), src = Path.Combine(root, "src");
        string owner = Path.Combine(src, "Valheim.Testing.Game", "CliCapabilityManifest.cs");
        var repeats = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar);
            if (file == owner || parts.Any(part => part is "bin" or "obj" or "SessionAdapter")) continue;
            scanned++;
            foreach (var token in CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken) &&
                    !token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) && !token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)) continue;
                string text = token.ValueText;
                if (text.Contains(CliCapabilityManifest.PluginGuid, StringComparison.Ordinal) || text == CliCapabilityManifest.PluginFile ||
                    text.Contains("one-plugin build", StringComparison.Ordinal))
                    repeats.Add($"{Path.GetRelativePath(root, file)}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {token.Text}");
            }
        }
        Assert.True(scanned > 100, $"scanned only {scanned} files under {src}");
        Assert.True(repeats.Count == 0, "Name ValheimCLI's plugin through CliCapabilityManifest.PluginGuid/PluginFile and leave the one-plugin rule to CliCapabilityManifest.Read: " +
            string.Join("; ", repeats));
        // The control: the owner itself has them.
        string own = File.ReadAllText(owner);
        Assert.Contains("\"" + CliCapabilityManifest.PluginGuid + "\"", own);
        Assert.Contains("not a one-plugin build", own);
    }

    private NativeDependencyRequest Request(string manifest) => new()
    {
        Mods = [_rig.Parent], SearchRoots = [Path.Combine(_rig.Root, "deps")],
        GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(_rig.Game))!,
        BepInExCore = Path.Combine(_rig.Game, "BepInEx", "core"),
        CliManifest = manifest, CliFiles = Path.Combine(_rig.Root, "cli"),
        Capabilities = ["valheim.session/state", "valheim.session/save"],
    };
}
