using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>
/// A reviewed, extracted BepInEx/UnityDoorstop package: identity and exact loader/core files, independent of a live Steam
/// install. A prepared game may be copied into a disposable install, then this set is applied there before any plugin is
/// staged. Static checks of the package do not claim it will start; the owned client still must reach its menu and write a
/// fresh BepInEx log.
/// </summary>
public sealed class BepInExLoaderPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Root { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The package's loader files as <see cref="InstallPins.Loader"/> hashes them: an install this package was applied to has
    /// this loader pin. <c>BepInEx/config/BepInEx.cfg</c>, which the package may also pin, is configuration and not part of it.
    /// </summary>
    [JsonIgnore] public string Loader => InstallPins.ListingHash(Files.Where(file => InstallPins.IsLoaderFile(file.Key)).Select(file => (file.Key, file.Value.ToLowerInvariant())));
    /// <summary>Name, version and <see cref="Loader"/>, as evidence records the package.</summary>
    [JsonIgnore] public string Identity => Name + " " + Version + " (" + Loader + ")";
    private const string Settings = "BepInEx/config/BepInEx.cfg";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    };

    /// <summary>Records one extracted loader set. The package root is read, never changed.</summary>
    public static BepInExLoaderPackage Capture(string root, string name, string version)
    {
        root = Path.GetFullPath(root);
        var package = new BepInExLoaderPackage { Name = name, Version = version, Root = root };
        foreach (string path in InstallPins.LoaderFiles(root).Append(Path.Combine(root, Settings)).Where(File.Exists))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            package.Files.Add(relative, WorldFixture.Hash(path));
        }
        package.Validate();
        return package;
    }

    public void Write(string path) { Validate(); File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n"); }

    /// <summary>Reads a package manifest and checks every pinned source file before it may be copied.</summary>
    public static BepInExLoaderPackage Read(string path)
    {
        path = Path.GetFullPath(path);
        var package = JsonSerializer.Deserialize<BepInExLoaderPackage>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"The BepInEx loader package {path} is empty.");
        if (!Path.IsPathFullyQualified(package.Root)) package.Root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, package.Root));
        package.Validate();
        return package;
    }

    /// <summary>Checks package identity, allowed paths, pinned contents and static BepInEx loader shape.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Version) || Name.Any(char.IsControl) || Version.Any(char.IsControl))
            throw new ArgumentException("Name the BepInEx package and version so evidence can identify its source.");
        if (!Path.IsPathFullyQualified(Root) || !Directory.Exists(Root)) throw new DirectoryNotFoundException($"BepInEx package root {Root} does not exist.");
        if (Files.Count == 0) throw new InvalidDataException("The BepInEx package lists no loader files.");
        foreach (var (relative, sha256) in Files)
        {
            string path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!Allowed(relative) || !RegressionInputs.Inside(path, Root) ||
                relative != Path.GetRelativePath(Root, path).Replace('\\', '/'))
                throw new InvalidDataException($"{relative} is not a loader/core file inside the BepInEx package.");
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) || !File.Exists(path) || !WorldFixture.Hash(path).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"BepInEx package file {path} is missing or changed; recapture the exact package.");
        }
        foreach (string required in new[] { BepInExLoader.Preloader, BepInExLoader.Core })
            if (!Files.ContainsKey(required.Replace('\\', '/'))) throw new InvalidDataException($"The BepInEx package does not pin {required}.");
        BepInExLoader.RequireCore(Root, "package");
        if (Files.ContainsKey(BepInExLoader.WindowsProxy))
        {
            if (!Files.ContainsKey(BepInExLoader.WindowsConfig)) throw new InvalidDataException("A Windows loader package needs both winhttp.dll and doorstop_config.ini.");
            BepInExLoader.RequireWindowsLoader(Root, "package");
        }
        if (!Files.Keys.Any(key => key == BepInExLoader.WindowsProxy || ClientLaunch.MacDoorstopFiles.Contains(key) || key.StartsWith("doorstop_libs/", StringComparison.Ordinal)))
            throw new InvalidDataException("The BepInEx package has no Doorstop library or Windows proxy.");
    }

    /// <summary>Replaces only the copied install's loader/core files with this pinned package. Caller must own the install.</summary>
    internal void Apply(string install)
    {
        Validate();
        foreach (string file in InstallPins.LoaderRootFiles.Append(Settings))
            if (File.Exists(Path.Combine(install, file))) File.Delete(Path.Combine(install, file));
        foreach (string folder in InstallPins.LoaderFolders)
            if (Directory.Exists(Path.Combine(install, folder))) Directory.Delete(Path.Combine(install, folder), recursive: true);
        foreach (var (relative, sha256) in Files)
        {
            string source = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(install, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            if (!WorldFixture.Hash(target).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"The copied BepInEx loader file {target} changed during staging.");
        }
    }

    /// <summary>
    /// Whether a reusable disposable install still has this exact package's loader files. <c>BepInEx.cfg</c>, which BepInEx
    /// rewrites, is not compared: staging copies it from the package again on every run.
    /// </summary>
    internal bool Matches(string install) => Files.Where(file => InstallPins.IsLoaderFile(file.Key)).All(file => File.Exists(Path.Combine(install, file.Key.Replace('/', Path.DirectorySeparatorChar)))
        && WorldFixture.Hash(Path.Combine(install, file.Key.Replace('/', Path.DirectorySeparatorChar))).Equals(file.Value, StringComparison.OrdinalIgnoreCase));

    private static bool Allowed(string relative) => InstallPins.IsLoaderFile(relative) || relative == Settings;
}
