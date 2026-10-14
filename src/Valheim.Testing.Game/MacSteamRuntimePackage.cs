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
        RequireManifest();
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");
    }

    /// <summary>Read the manifest. Profile preparation verifies its source immediately before and after staging.</summary>
    public static MacSteamRuntimePackage Read(string path)
    {
        var package = JsonSerializer.Deserialize<MacSteamRuntimePackage>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("The Mac Steam runtime manifest is empty.");
        if (package.Root == null) throw new InvalidDataException("The Mac Steam runtime package has no source root.");
        if (!Path.IsPathFullyQualified(package.Root))
            package.Root = Path.GetFullPath(package.Root, Path.GetDirectoryName(Path.GetFullPath(path))!);
        package.RequireManifest();
        return package;
    }

    /// <summary>
    /// Read a manifest copied to the campaign runner while its source root remains on a remote Mac. The host-side stage
    /// validates every source byte and Mach-O slice before and after it changes the disposable profile.
    /// </summary>
    internal static MacSteamRuntimePackage ReadRemote(string path)
    {
        var package = JsonSerializer.Deserialize<MacSteamRuntimePackage>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("The Mac Steam runtime manifest is empty.");
        package.RequireManifest();
        return package;
    }

    private void RequireManifest()
    {
        if (Name != "SteamCMD" || string.IsNullOrWhiteSpace(Version) ||
            Version.Any(c => !char.IsAsciiDigit(c)) || ProfileFormat != SignedProfileFormat ||
            Root is not { Length: > 1 } root || !root.StartsWith('/') || root.Any(char.IsControl) ||
            root.Split('/').Any(part => part is "." or "..") ||
            SourceManifestSha256 is not { Length: 64 } manifestHash || !manifestHash.All(Uri.IsHexDigit) ||
            Files is not { Count: > 0 })
            throw new InvalidDataException("The Mac Steam runtime manifest has invalid source identity or paths.");
        foreach (var (relative, hash) in Files)
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains('\\') ||
                relative.Split('/').Any(part => part is "" or "." or "..") || relative.Any(char.IsControl) ||
                hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("The Mac Steam runtime manifest has an unsafe or unpinned file: " + relative);
        if (!Files.ContainsKey("steamclient.dylib") || !Files.ContainsKey("crashhandler.dylib"))
            throw new InvalidDataException("The Mac Steam runtime manifest lacks its required entry libraries.");
    }

    /// <summary>Keep the reviewed manifest in a run's evidence. A remote source was checked on its Mac host already.</summary>
    internal static MacSteamRuntimePackage Snapshot(string path, string snapshot, bool remote)
    {
        var package = remote ? ReadRemote(path) : Read(path);
        if (remote) File.Copy(path, snapshot);
        else package.Write(snapshot);
        return package;
    }

    /// <summary>Verify all pins, arm64 availability and the complete current @loader_path closure.</summary>
    public void Validate()
    {
        RequireMac();
        RequireManifest();
        string output = Tool("/bin/bash", ["-c", VerificationScript], VerificationVariables());
        RequireVerificationReply(output, "this Mac");
    }

    /// <summary>
    /// Make arm64-only, locally signed copies for a disposable profile. Valve's SteamCMD binaries on macOS
    /// can have signatures that dyld rejects even at their source path; neither signing nor thinning touches
    /// the reviewed SteamCMD installation. The returned hashes pin the derived files used by this run.
    /// </summary>
    internal IReadOnlyDictionary<string, string> StageSignedFiles(string profileRoot)
    {
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
        return staged;
    }

    internal IReadOnlyDictionary<string, string> VerificationVariables() => new Dictionary<string, string>
    {
        ["source"] = Root,
        ["expectedVersion"] = Version,
        ["manifestSha"] = SourceManifestSha256.ToLowerInvariant(),
        ["files"] = string.Join('\n', Files.OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => file.Value.ToLowerInvariant() + " " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(file.Key)))),
    };

    /// <summary>The one pins, arm64-slice and otool-closure implementation, executed on either a local or SSH Mac.</summary>
    internal static readonly string VerificationScript = """
        set -eu
        # macOS's own tools by absolute path (Homebrew's GNU ones may come first on PATH); a trailing slash would hide a linked root.
        source=${source%/}
        [ "$(uname -s)" = Darwin ] || { echo 'VT-STEAM-RUNTIME unsupported'; exit 4; }
        [ -d "$source" ] && [ ! -L "$source" ] || { echo 'VT-STEAM-RUNTIME source'; exit 4; }
        manifest="$source/package/steam_cmd_osx.manifest"
        [ -f "$manifest" ] && [ ! -L "$manifest" ] || { echo 'VT-STEAM-RUNTIME manifest'; exit 4; }
        actual=$(/usr/bin/shasum -a 256 "$manifest" | /usr/bin/awk '{print $1}')
        [ "$actual" = "$manifestSha" ] || { echo 'VT-STEAM-RUNTIME manifest-changed'; exit 4; }
        version=$(/usr/bin/awk '$1 == "\"version\"" && $2 ~ /^"[0-9]+"$/ { gsub(/"/, "", $2); print $2; exit }' "$manifest")
        [ "$version" = "$expectedVersion" ] || { echo 'VT-STEAM-RUNTIME version-changed'; exit 4; }
        while read -r expected encoded; do
          [ -n "$expected" ] || continue
          relative=$(printf '%s' "$encoded" | /usr/bin/base64 -D)
          part="$source"
          IFS=/ read -ra components <<< "$relative"
          for component in "${components[@]}"; do
            part="$part/$component"
            [ ! -L "$part" ] || { echo 'VT-STEAM-RUNTIME linked'; exit 4; }
          done
          [ -f "$part" ] || { echo "VT-STEAM-RUNTIME missing $relative"; exit 4; }
          actual=$(/usr/bin/shasum -a 256 "$part" | /usr/bin/awk '{print $1}')
          [ "$actual" = "$expected" ] || { echo "VT-STEAM-RUNTIME changed $relative"; exit 4; }
          archs=$(/usr/bin/lipo -archs "$part")
          case " $archs " in *' arm64 '*) ;; *) echo "VT-STEAM-RUNTIME architecture $relative"; exit 4 ;; esac
          dependencies=$(/usr/bin/otool -L "$part")
          printf 'VT-STEAM-OTOOL %s %s\n' "$encoded" "$(printf '%s' "$dependencies" | /usr/bin/base64 | /usr/bin/tr -d '\n')"
        done <<< "$files"
        echo 'VT-STEAM-VERIFIED'
        """.ReplaceLineEndings("\n");

    internal void RequireVerificationReply(string stdout, string host)
    {
        if (stdout.Split('\n').All(line => line.TrimEnd('\r') != "VT-STEAM-VERIFIED"))
            throw new InvalidDataException($"The SteamCMD dependency closure on {host} did not report VT-STEAM-VERIFIED.");
        var listings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in stdout.Split('\n').Where(line => line.StartsWith("VT-STEAM-OTOOL ", StringComparison.Ordinal)))
        {
            string[] pair = line.TrimEnd('\r').Split(' ', 3);
            if (pair.Length != 3) throw new InvalidDataException($"The SteamCMD dependency reply from {host} is incomplete.");
            string relative, output;
            try
            {
                relative = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pair[1]));
                output = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pair[2]));
            }
            catch (FormatException error) { throw new InvalidDataException($"The SteamCMD dependency reply from {host} is malformed.", error); }
            if (!listings.TryAdd(relative, output))
                throw new InvalidDataException($"The SteamCMD dependency reply from {host} repeats {relative}.");
        }
        RequireRemoteClosure(listings);
    }

    private static IReadOnlyList<string> DependencyClosure(string root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(["steamclient.dylib", "crashhandler.dylib"]);
        while (pending.TryDequeue(out string? relative))
        {
            if (!found.Add(relative)) continue;
            string path = SafeFile(root, relative);
            foreach (string child in Dependencies(relative, Tool("/usr/bin/otool", ["-L", path]))) pending.Enqueue(child);
        }
        return found.Order(StringComparer.Ordinal).ToArray();
    }

    // The same Mach-O dependency parser checks a local capture and the read-only otool replies from an SSH Mac.
    // Only file inspection differs; the decision about which files form a complete closure has one owner.
    internal void RequireRemoteClosure(IReadOnlyDictionary<string, string> listings)
    {
        if (!listings.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Files.Keys))
            throw new InvalidDataException("The remote SteamCMD dependency inspection did not cover every pinned file.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(["steamclient.dylib", "crashhandler.dylib"]);
        while (pending.TryDequeue(out string? relative))
        {
            if (!found.Add(relative)) continue;
            if (!listings.TryGetValue(relative, out string? output))
                throw new InvalidDataException("The remote SteamCMD dependency closure lacks " + relative + ".");
            foreach (string child in Dependencies(relative, output)) pending.Enqueue(child);
        }
        if (!found.SetEquals(Files.Keys))
            throw new InvalidDataException("The remote SteamCMD dependency closure differs from its manifest: " +
                string.Join(", ", Files.Keys.Except(found, StringComparer.Ordinal).Concat(found.Except(Files.Keys, StringComparer.Ordinal))));
    }

    private static IEnumerable<string> Dependencies(string relative, string listing)
    {
        bool firstInSlice = true;
        foreach (string line in listing.Split('\n'))
        {
            if (!char.IsWhiteSpace(line.FirstOrDefault())) { firstInSlice = true; continue; }
            if (firstInSlice) { firstInSlice = false; continue; }
            string dependency = line.TrimStart().Split(' ', 2)[0];
            if (dependency.StartsWith("@loader_path/", StringComparison.Ordinal))
            {
                // Framework install names need not be relative to the framework binary's actual directory.
                var parts = relative.Split('/')[..^1].ToList();
                foreach (string part in dependency[13..].Split('/'))
                {
                    if (part == ".") continue;
                    if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Mac Steam runtime dependency escapes its root: " + dependency); parts.RemoveAt(parts.Count - 1); }
                    else if (part.Length == 0 || part.Contains('\\') || part.Any(char.IsControl))
                        throw new InvalidDataException("Unsafe Mac Steam runtime dependency: " + dependency);
                    else parts.Add(part);
                }
                yield return string.Join('/', parts);
            }
            else if (dependency.StartsWith('@'))
                throw new InvalidDataException($"Mac Steam runtime dependency {dependency} in {relative} is unresolved.");
            else if (!dependency.StartsWith("/System/Library/", StringComparison.Ordinal) &&
                !dependency.StartsWith("/usr/lib/", StringComparison.Ordinal))
                throw new InvalidDataException($"Mac Steam runtime dependency {dependency} in {relative} is outside macOS system libraries and is not pinned.");
        }
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

    private static string Tool(string command, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) info.Environment[name] = value;
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
        if (process.ExitCode != 0) throw new InvalidDataException($"Mac Steam runtime inspection failed: {command}: " +
            string.Join("; ", new[] { output.Trim(), error.Trim() }.Where(text => text.Length != 0)));
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
