// Local test-pyramid layers only; never launches Valheim. Run bootstrap-cli.cs first.
//
//   dotnet run scripts/validate.cs
//
// Runs the library tests, builds every example, executes the two no-game
// examples and packs both libraries into the local feed.
using System.Diagnostics;
using System.Runtime.CompilerServices;

string root = FindRoot();

Run("dotnet", "test", "tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj", "-c", "Release", "-m:1");
foreach (string project in Directory.GetFiles(Path.Combine(root, "examples"), "*.csproj", SearchOption.AllDirectories)
             .Where(p => Path.GetDirectoryName(Path.GetDirectoryName(p)) == Path.Combine(root, "examples"))
             .OrderBy(p => p, StringComparer.Ordinal))
    Run("dotnet", "build", project, "-c", "Release", "-m:1");
Run("dotnet", "run", "--project", "examples/NoGameTerrain", "-c", "Release", "--no-build");
Run("dotnet", "run", "--project", "examples/SharedWorld", "-c", "Release", "--no-build");
foreach (string name in new[] { "Valheim.Testing", "Valheim.Testing.Game" })
    Run("dotnet", "pack", $"src/{name}/{name}.csproj", "-c", "Release", "--no-restore", "-m:1", "-o", Path.Combine(root, ".packages"));
Console.WriteLine("Local validation passed.");
return 0;

static string ScriptPath([CallerFilePath] string path = "") => path;

// The repository root holds cli-dependency.json. Search upward from the working directory first:
// CI path mapping can rewrite the compile-time source path CallerFilePath reports.
static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(ScriptPath()) ?? "" })
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "cli-dependency.json"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}

void Run(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = root };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"FAILED ({process.ExitCode}): {file} {string.Join(' ', arguments)}");
        Environment.Exit(process.ExitCode);
    }
}
