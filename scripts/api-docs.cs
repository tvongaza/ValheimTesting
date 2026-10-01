// Build the preview public-surface reference after bootstrap-cli.cs and validate.cs.
// No game process or game assemblies are needed.
//
//   dotnet tool restore
//   dotnet run scripts/bootstrap-cli.cs
//   dotnet run scripts/validate.cs
//   dotnet run scripts/api-docs.cs
using System.Diagnostics;
using System.Runtime.CompilerServices;

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

Console.WriteLine("Preview API reference ready: docs/reference/_site/index.html");

static string SourcePath([CallerFilePath] string path = "") => path;

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(SourcePath()) ?? "" })
        for (DirectoryInfo? directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "cli-dependency.json"))) return directory.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}
