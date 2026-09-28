// Build the exact pinned ValheimCLI transport into the local feed as Valheim.Testing.Cli.
// Needs only the .NET 10 SDK and Git; never needs a game install.
//
//   dotnet run scripts/bootstrap-cli.cs
//   dotnet run scripts/bootstrap-cli.cs -- --source /path/to/valheimCLI
//
// --source uses an existing Git repository, but exports the pinned commit
// only, never its working tree.
using System.Diagnostics;
using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Text.Json;

string root = FindRoot();
string? sourceArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--source" && i + 1 < args.Length) sourceArg = args[++i];
    else { Console.Error.WriteLine("usage: dotnet run scripts/bootstrap-cli.cs [-- --source <git repository>]"); return 2; }
}

using JsonDocument pinDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cli-dependency.json")));
string repository = pinDoc.RootElement.GetProperty("repository").GetString()!;
string commit = pinDoc.RootElement.GetProperty("commit").GetString()!;
string version = pinDoc.RootElement.GetProperty("version").GetString()!;
string packageId = pinDoc.RootElement.GetProperty("publishedAs").GetString()!;

string feed = Path.Combine(root, ".packages");
Directory.CreateDirectory(feed);
string temp = Path.Combine(Path.GetTempPath(), "valheim-cli-dependency-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    string repo = sourceArg != null ? Path.GetFullPath(sourceArg) : Path.Combine(temp, "checkout");
    if (sourceArg == null)
    {
        Run("git", "init", "-q", repo);
        Run("git", "-C", repo, "fetch", "--depth=1", repository, commit);
    }
    string actual = Capture("git", "-C", repo, "rev-parse", commit + "^{commit}").Trim();
    if (actual != commit) throw new InvalidOperationException($"CLI revision mismatch: {actual} != {commit}");

    // git archive contains tracked source only, never a dirty worktree or artifacts.
    string archive = Path.Combine(temp, "source.tar");
    Run("git", "-C", repo, "archive", "--format=tar", "-o", archive, actual);
    string source = Path.Combine(temp, "source");
    Directory.CreateDirectory(source);
    TarFile.ExtractToDirectory(archive, source, overwriteFiles: false);

    string project = Path.Combine(source, "Toolkit", "Valheim.Cli.Testing", "Valheim.Cli.Testing.csproj");
    if (!File.ReadAllText(project).Contains("<Version>" + version + "</Version>"))
        throw new InvalidOperationException("CLI package version mismatch: expected " + version);
    // The package README describes this packaging; the transport's own client-library.md talks about
    // building it from source. Only the packed README is replaced; no code changes.
    File.Copy(Path.Combine(root, "docs", "packages", "Valheim.Testing.Cli.md"), Path.Combine(source, "docs", "client-library.md"), overwrite: true);
    // Published by ValheimTesting under its own package family: this is the upstream ValheimCLI
    // transport at the pinned commit, packaged unchanged. Its MIT license (copyright warp) is packed with it.
    Run("dotnet", "pack", project, "-c", "Release", "-m:1", "-o", feed,
        Property("PackageId", packageId),
        Property("Authors", "warp and ValheimCLI contributors"),
        Property("Description", "ValheimCLI's external client and YAML test-plan runner, packaged by ValheimTesting from ValheimCLI source at commit " + commit + ". No game assemblies. Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB."),
        Property("PackageProjectUrl", "https://github.com/tvongaza/ValheimTesting"),
        Property("RepositoryUrl", repository),
        Property("RepositoryType", "git"),
        Property("RepositoryCommit", commit),
        Property("PackageTags", "valheim modding testing valheimcli"));
}
finally
{
    DeleteTree(temp);
}
Console.WriteLine($"Pinned CLI dependency ready as {packageId} {version}: {feed}");
return 0;

static string ScriptPath([CallerFilePath] string path = "") => path;

// MSBuild splits -p: values on ';' and ','; escape them (and '%') so each value arrives whole.
static string Property(string name, string value) =>
    "-p:" + name + "=" + value.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C");

// The repository root holds cli-dependency.json. Search upward from the working directory first:
// CI path mapping can rewrite the compile-time source path CallerFilePath reports.
static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}

static void Run(string file, params string[] arguments)
{
    using Process process = Start(file, arguments, capture: false);
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{file} exited {process.ExitCode}");
}

static string Capture(string file, params string[] arguments)
{
    using Process process = Start(file, arguments, capture: true);
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{file} exited {process.ExitCode}");
    return output;
}

static Process Start(string file, string[] arguments, bool capture)
{
    var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = capture };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    return Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
}

// Git marks pack files read-only; Windows refuses to delete those until the attribute is cleared.
static void DeleteTree(string path)
{
    if (!Directory.Exists(path)) return;
    try
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"warning: could not remove temporary directory {path}: {e.Message}");
    }
}
