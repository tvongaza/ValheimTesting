using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// A reviewed set of SteamCMD's macOS server libraries. The files stay in the operator's SteamCMD installation;
/// a run copies only the pinned dependency closure into its disposable server profile.
/// </summary>
internal sealed class MacSteamRuntimePackage
{
    internal const string SignedProfileFormat = "thin-arm64-adhoc-sign-v1";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Root { get; set; } = "";
    public string SourceManifestSha256 { get; set; } = "";
    public string ProfileFormat { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] public string Identity => Name + " " + Version + " (" +
        FileHash.Listing(Files.Select(file => (file.Key, file.Value))) + ")";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    };

    /// <summary>Discover and pin the exact local dependency closure from one SteamCMD installation.</summary>
    public static MacSteamRuntimePackage Capture(string root)
    {
        RequireMac();
        root = Path.GetFullPath(root);
        var (version, sha) = SteamCmdManifest(root);
        var package = new MacSteamRuntimePackage { Name = "SteamCMD", Version = version, Root = root,
            SourceManifestSha256 = sha, ProfileFormat = SignedProfileFormat };
        foreach (string file in DependencyClosure(package.Root))
            package.Files.Add(file, FileHash.Sha256(Path.Combine(package.Root, file)));
        package.Validate();
        return package;
    }

    public void Write(string path)
    {
        Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");
    }

    /// <summary>Read the manifest and refuse any changed file or newly required native dependency.</summary>
    public static MacSteamRuntimePackage Read(string path)
    {
        var package = JsonSerializer.Deserialize<MacSteamRuntimePackage>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("The Mac Steam runtime manifest is empty.");
        if (package.Root == null) throw new InvalidDataException("The Mac Steam runtime package has no source root.");
        if (!Path.IsPathFullyQualified(package.Root))
            package.Root = Path.GetFullPath(package.Root, Path.GetDirectoryName(Path.GetFullPath(path))!);
        package.Validate();
        return package;
    }

    /// <summary>Verify all pins, arm64 availability and the complete current @loader_path closure.</summary>
    public void Validate()
    {
        RequireMac();
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Version) ||
            Name.Any(char.IsControl) || Version.Any(char.IsControl))
            throw new InvalidDataException("Name the Mac Steam runtime and its SteamCMD build.");
        if (string.IsNullOrWhiteSpace(Root) || !Path.IsPathFullyQualified(Root) || !Directory.Exists(Root))
            throw new DirectoryNotFoundException("Mac Steam runtime root is missing: " + Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The SteamCMD source root is linked; choose its real directory.");
        var (version, manifestSha) = SteamCmdManifest(Root);
        if (Name != "SteamCMD" || Version != version ||
            !string.Equals(SourceManifestSha256, manifestSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The SteamCMD source manifest changed; recapture the Mac Steam runtime package.");
        if (ProfileFormat != SignedProfileFormat)
            throw new InvalidDataException("The Mac Steam runtime profile format changed; recapture its package.");
        if (Files == null || Files.Count == 0)
            throw new InvalidDataException("The Mac Steam runtime package lists no files.");
        foreach (var (relative, sha) in Files)
        {
            string path = SafeFile(Root, relative);
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit) ||
                !FileHash.Sha256(path).Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mac Steam runtime file changed: " + relative + "; recapture its SteamCMD build.");
            string archs = Tool("/usr/bin/lipo", ["-archs", path]);
            if (!archs.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("arm64"))
                throw new InvalidDataException("Mac Steam runtime file lacks arm64: " + relative);
        }
        var closure = DependencyClosure(Root);
        if (!Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(closure))
            throw new InvalidDataException("Mac Steam runtime dependencies changed: " +
                string.Join(", ", closure.Except(Files.Keys, StringComparer.Ordinal).Concat(Files.Keys.Except(closure, StringComparer.Ordinal))));
    }

    /// <summary>
    /// Make arm64-only, locally signed copies for a disposable profile. Valve's SteamCMD binaries on macOS
    /// can have signatures that dyld rejects even at their source path; neither signing nor thinning touches
    /// the reviewed SteamCMD installation. The returned hashes pin the derived files used by this run.
    /// </summary>
    internal IReadOnlyDictionary<string, string> StageSignedFiles(string profileRoot)
    {
        Validate();
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string relative in Files.Keys.Order(StringComparer.Ordinal))
        {
            string source = SafeFile(Root, relative);
            string target = Path.Combine(profileRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string[] slices = Tool("/usr/bin/lipo", ["-archs", source]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (slices.Length == 1 && slices[0] == "arm64") File.Copy(source, target);
            else Tool("/usr/bin/lipo", ["-thin", "arm64", source, "-output", target]);
            Tool("/usr/bin/codesign", ["--force", "--sign", "-", "--timestamp=none", target]);
            Tool("/usr/bin/codesign", ["--verify", target]);
            staged.Add(relative, FileHash.Sha256(target));
        }
        Validate();
        return staged;
    }

    private static IReadOnlyList<string> DependencyClosure(string root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(["steamclient.dylib", "crashhandler.dylib"]);
        while (pending.TryDequeue(out string? relative))
        {
            if (!found.Add(relative)) continue;
            string path = SafeFile(root, relative);
            string directory = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
            // The first indented line in each Mach-O slice is its own install name, not a dependency.
            // Framework install names need not be relative to the framework binary's actual directory.
            bool firstInSlice = true;
            foreach (string line in Tool("/usr/bin/otool", ["-L", path]).Split('\n'))
            {
                if (!char.IsWhiteSpace(line.FirstOrDefault())) { firstInSlice = true; continue; }
                if (firstInSlice) { firstInSlice = false; continue; }
                string dependency = line.TrimStart().Split(' ', 2)[0];
                if (dependency.StartsWith("@loader_path/", StringComparison.Ordinal))
                {
                    string name = Path.GetFullPath(Path.Combine(root, directory, dependency[13..]));
                    string child = Path.GetRelativePath(root, name).Replace('\\', '/');
                    SafeFile(root, child);
                    pending.Enqueue(child);
                }
                else if (dependency.StartsWith('@'))
                    throw new InvalidDataException($"Mac Steam runtime dependency {dependency} in {relative} is unresolved.");
            }
        }
        return found.Order(StringComparer.Ordinal).ToArray();
    }

    private static string SafeFile(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.StartsWith('/') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Unsafe Mac Steam runtime path: " + relative);
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Mac Steam runtime file escapes its root: " + relative);
        string part = root;
        foreach (string component in relative.Split('/'))
        {
            part = Path.Combine(part, component);
            if (File.Exists(part) || Directory.Exists(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Mac Steam runtime path is linked: " + relative);
        }
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("Mac Steam runtime file is missing or linked: " + relative, path);
        return path;
    }

    private static string Tool(string command, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not run " + command);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Mac Steam runtime inspection timed out: " + command);
        }
        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidDataException($"Mac Steam runtime inspection failed: {command}: {error.Trim()}");
        return output.Trim();
    }

    private static (string Version, string Sha256) SteamCmdManifest(string root)
    {
        string path = SafeFile(root, "package/steam_cmd_osx.manifest");
        string text = File.ReadAllText(path);
        var match = Regex.Match(text, "\"version\"\\s+\"(?<version>[0-9]+)\"", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("The SteamCMD source has no macOS build version in package/steam_cmd_osx.manifest.");
        return (match.Groups["version"].Value, FileHash.Sha256(path));
    }

    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac Steam runtime packages are inspected on macOS.");
    }
}
