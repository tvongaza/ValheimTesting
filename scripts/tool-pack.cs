// Keep .NET tool packages free of files left by an earlier publish in the same checkout.
// The SDK packs every file under each tool's publish directory, including files it did not create this time.
//
//   dotnet run scripts/tool-pack.cs -- clean             before packing the tools
//   dotnet run scripts/tool-pack.cs -- verify FEED       after packing (one package per tool in FEED)
//   dotnet run scripts/tool-pack.cs -- seed              CI regression: plant stale files before validation
using System.IO.Compression;
using System.Text.RegularExpressions;

string root = FindRoot();
string[] tools = ["Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];
const string sentinel = "Valheim.Testing.StalePackSentinel.dll";

switch (args)
{
    case ["clean"]:
        foreach (string id in tools)
        {
            string path = PublishDirectory(id);
            if (!Directory.Exists(path)) continue;
            try { Directory.Delete(path, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Cannot clear generated tool publish output at {path}; stop other builds or move the checkout out of a synced folder, then retry.", error);
            }
            Console.WriteLine($"Cleared generated publish output for {id}.");
        }
        break;
    case ["seed"]:
        foreach (string id in tools)
        {
            string path = PublishDirectory(id);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, sentinel), "stale publish file for the packaging regression");
        }
        Console.WriteLine("Seeded stale tool publish files for the packaging regression.");
        break;
    case ["verify", var feed]:
        foreach (string id in tools)
        {
            string[] matches = Directory.GetFiles(feed, id + ".*.nupkg");
            if (matches.Length != 1)
                throw new InvalidDataException($"Expected one {id} package in {feed}, found {matches.Length}.");
            using ZipArchive package = ZipFile.OpenRead(matches[0]);
            string[] entries = package.Entries.Select(entry => entry.FullName).ToArray();
            string expected = id + ".dll";
            if (entries.Count(path => path.EndsWith("/" + expected, StringComparison.Ordinal)) != 1)
                throw new InvalidDataException($"{matches[0]} does not contain exactly one {expected}.");
            string[] stale = entries.Where(path => path.EndsWith("/" + sentinel, StringComparison.Ordinal) ||
                Regex.IsMatch(Path.GetFileName(path), @" \d+\.dll$", RegexOptions.IgnoreCase)).ToArray();
            if (stale.Length != 0)
                throw new InvalidDataException($"{matches[0]} includes stale publish output: {string.Join(", ", stale)}");
            Console.WriteLine($"{id}: no stale publish files in {Path.GetFileName(matches[0])}.");
        }
        break;
    default:
        throw new ArgumentException("usage: dotnet run scripts/tool-pack.cs -- clean | seed | verify FEED");
}

string PublishDirectory(string id) => Path.Combine(root, "src", id, "bin", "Release", "net10.0", "publish");

static string FindRoot()
{
    for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "cli-dependency.json"))) return directory.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}
