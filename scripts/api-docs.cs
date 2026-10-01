// Build the preview public-surface reference after bootstrap-cli.cs and validate.cs.
// No game process or game assemblies are needed.
//
//   dotnet tool restore
//   dotnet run scripts/bootstrap-cli.cs
//   dotnet run scripts/validate.cs
//   dotnet run scripts/api-docs.cs
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

string root = FindRoot();
foreach (string path in new[]
{
    "src/Valheim.Testing/bin/Release/netstandard2.0/Valheim.Testing.dll",
    "src/Valheim.Testing.Doubles/bin/Release/netstandard2.0/Valheim.Testing.Doubles.dll",
    "src/Valheim.Testing.Game/bin/Release/net10.0/Valheim.Testing.Game.dll",
    "src/Valheim.Testing.Bindings/bin/Release/netstandard2.0/Valheim.Testing.Bindings.dll",
    "src/Valheim.Testing.Bindings.Tool/bin/Release/net10.0/Valheim.Testing.Bindings.Tool.dll",
    "tests/Valheim.Testing.Adapter.CompileCheck/bin/Release/net48/Valheim.Testing.Adapter.CompileCheck.dll"
})
    if (!File.Exists(Path.Combine(root, path)))
        throw new FileNotFoundException("Run validate.cs first; a release build is missing.", path);

var info = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false,
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
foreach (string argument in new[] { "tool", "run", "docfx", "docs/reference/docfx.json" }) info.ArgumentList.Add(argument);
using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start DocFX.");
Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
Task<string> standardError = process.StandardError.ReadToEndAsync();
process.WaitForExit();
string output = standardOutput.GetAwaiter().GetResult();
string errors = standardError.GetAwaiter().GetResult();
Console.Write(output);
Console.Error.Write(errors);
if (process.ExitCode != 0) throw new InvalidOperationException($"DocFX failed with exit code {process.ExitCode}.");
if (output.Contains("InvalidFileLink", StringComparison.Ordinal) ||
    output.Contains("InvalidCrossReference", StringComparison.Ordinal) ||
    errors.Contains("InvalidFileLink", StringComparison.Ordinal) ||
    errors.Contains("InvalidCrossReference", StringComparison.Ordinal))
    throw new InvalidOperationException("DocFX reported a broken documentation link.");

string site = Path.Combine(root, "docs", "reference", "_site");
foreach (string page in new[]
{
    "index.html",
    "api/Valheim.Testing.ITerrain.html",
    "api/Valheim.Testing.Doubles.TerrainWorld.html",
    "api/Valheim.Testing.Game.html",
    "api/Valheim.Testing.Adapter.html",
    "api/Valheim.Testing.Bindings.html"
})
    if (!File.Exists(Path.Combine(site, page)))
        throw new InvalidOperationException($"DocFX omitted a package's reference page: {page}");

// These selected examples explain the order of operations. A successful metadata build alone does not prove that
// DocFX rendered an XML <example> on the page a mod author will read.
foreach (var (page, excerpt) in new (string Page, string Excerpt)[]
{
    ("api/Valheim.Testing.CompositeTerrain.html", "float terraceHeight = terrain.GetHeight"),
    ("api/Valheim.Testing.Game.WorldFixture.html", "WorldFixture.Verify(fixtureSource, reviewedHashes)"),
    ("api/Valheim.Testing.Game.DisposableCharacterStore.html", "store.Register(\"tester\", localCharacterFile)"),
    ("api/Valheim.Testing.Game.CharacterStartCopy.html", "CharacterStartCopy.Prepare("),
    ("api/Valheim.Testing.Game.GameActor.html", "actor.RequireCapability(\"mymod.testing/session\")"),
    ("api/Valheim.Testing.Game.PinnedServerRun.html", "PinnedServerRun.MainAsync(args"),
})
{
    string file = Path.Combine(site, page);
    if (!File.Exists(file) || !File.ReadAllText(file).Contains(excerpt, StringComparison.Ordinal))
        throw new InvalidOperationException($"DocFX omitted the selected contextual example from {page}.");
}

// DocFX can log "Found project reference without a matching metadata reference" for Valheim.Testing.csproj (Roslyn's
// MSBuild workspace; whether it appears depends on project load order, not on these sources). It costs nothing as long
// as the pages of the projects that reference Valheim.Testing still link its types, which these check.
foreach (var (page, link) in new (string Page, string Link)[]
{
    ("api/Valheim.Testing.Doubles.TerrainWorld.html", "href=\"Valheim.Testing.ITerrain.html\""),
    ("api/Valheim.Testing.Game.TerrainCapture.html", "href=\"Valheim.Testing.ReplayTerrain.html\""),
})
{
    string file = Path.Combine(site, page);
    if (!File.Exists(file) || !File.ReadAllText(file).Contains(link, StringComparison.Ordinal))
        throw new InvalidOperationException($"DocFX did not link {page} to Valheim.Testing ({link}): the project reference was lost.");
}
// DocFX's build manifest records source_base_path as an absolute local path.
// It is build metadata, not a page asset; do not publish it.
File.Delete(Path.Combine(site, "manifest.json"));
string[] privatePaths = [root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
foreach (string file in Directory.EnumerateFiles(site, "*", SearchOption.AllDirectories))
{
    byte[] contents = File.ReadAllBytes(file);
    foreach (string path in privatePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        if (contents.AsSpan().IndexOf(Encoding.UTF8.GetBytes(path)) >= 0)
            throw new InvalidOperationException($"Generated reference contains a build-machine path in {Path.GetRelativePath(site, file)}.");
}

Console.WriteLine("Preview API reference ready: docs/reference/_site/index.html");

static string SourcePath([CallerFilePath] string path = "") => path;

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(SourcePath()) ?? "" })
        for (DirectoryInfo? directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "cli-dependency.json"))) return directory.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}
