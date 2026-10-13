// The SDK's file-based-app cache does not notice edits to this shared file. After editing it,
// rebuild each script with `dotnet run --no-cache scripts/SCRIPT.cs`.
using System.Runtime.CompilerServices;

internal static class RepositoryPaths
{
    public static string FindRoot([CallerFilePath] string scriptPath = "")
    {
        foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(scriptPath) ?? "" })
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (DirectoryInfo? directory = new(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "cli-dependency.json"))) return directory.FullName;
        }
        throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
    }

    public static string[] Resolve(params string[] paths)
    {
        string root = paths.Any(path => !string.IsNullOrWhiteSpace(path) && !Path.IsPathFullyQualified(path))
            ? FindRoot() : Environment.CurrentDirectory;
        return paths.Select(path => ResolvePath(root, path)).ToArray();
    }

    // An empty argument is a mistake (an unset variable), never "the repository root".
    public static string ResolvePath(string root, string path) => string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A path argument is empty.")
        : Path.GetFullPath(path, root);
}
