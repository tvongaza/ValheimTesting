using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Valheim.Testing.Game;

/// <summary>
/// How a disposable profile launches the game. <see cref="ProfileLayouts.For"/> decides it once, from the host's platform.
/// </summary>
internal enum ProfileLayout
{
    /// <summary>
    /// The profile is the launch folder: the game's files are hard-linked into it and the game starts from there, so
    /// <c>Paths.GameRootPath</c> and every write beside the executable are the profile's. Every Windows profile and every
    /// dedicated server's (Linux and macOS).
    /// </summary>
    Linked,
    /// <summary>
    /// The game starts from its source install and its loader from the profile: the macOS client, whose app bundle launched
    /// from a new path trips Gatekeeper.
    /// </summary>
    SourceExecutable,
}

/// <summary>The one decision between <see cref="ProfileLayout.Linked"/> and <see cref="ProfileLayout.SourceExecutable"/>, and where a linked profile's game goes.</summary>
internal static class ProfileLayouts
{
    /// <summary>
    /// The layout of a profile on a <paramref name="platform"/> host (<c>windows</c>, <c>linux</c> or <c>macos</c>) for a dedicated
    /// <paramref name="server"/> or a client. Every server is linked; of the clients, only Windows' (Linux runs none, and a Mac
    /// client's bundle must stay where Gatekeeper accepted it).
    /// </summary>
    internal static ProfileLayout For(string platform, bool server) =>
        platform == "windows" || server ? ProfileLayout.Linked : ProfileLayout.SourceExecutable;

    /// <summary>
    /// The folder, relative to a linked profile, that the install's game files are linked into: the profile itself, except for a
    /// Mac dedicated server. BepInEx on macOS takes <c>Paths.GameRootPath</c> as the executable's fourth parent (it expects
    /// <c>X.app/Contents/MacOS/exe</c>), and the server's executable is <c>valheim_server/Valheim</c>, so its install is linked
    /// two folders down: the executable is <c>&lt;profile&gt;/game/server/valheim_server/Valheim</c> and the game root BepInEx
    /// writes beside is the profile, where its loader is.
    /// </summary>
    internal static string GameFolder(string platform, bool server) => platform == "macos" && server ? MacServerGameFolder : "";

    internal const string MacServerGameFolder = "game/server";
}

/// <summary>
/// The words one platform's hard-link rule is refused in: its name, what a link cannot cross (a Windows or Mac volume, a Linux
/// mount), where the runtime belongs and which file systems hold the links.
/// </summary>
internal sealed record ProfileLinkRules(string Platform, string Unit, string Home, string FileSystems)
{
    internal static readonly ProfileLinkRules Windows = new("Windows", "volume", "NTFS volume", "NTFS");
    internal static readonly ProfileLinkRules Linux = new("Linux", "mount", "mount", "a filesystem with hard links (ext4, xfs or btrfs)");
    internal static readonly ProfileLinkRules MacOS = new("macOS", "volume", "volume", "a filesystem with hard links (APFS or Mac OS Extended)");

    /// <summary>The rules of a linked profile on <paramref name="platform"/> (<c>windows</c>, <c>linux</c> or <c>macos</c>).</summary>
    internal static ProfileLinkRules For(string platform) => platform switch { "windows" => Windows, "macos" => MacOS, _ => Linux };
}

/// <summary>Where a linked profile's game files live: the Windows volume, or the Linux mount or Mac volume, that holds a path.</summary>
internal interface IProfileStorageVolume
{
    /// <summary>The volume's root or the mount point: where a default runtime root moves to follow the install.</summary>
    string Root { get; }
    string Description { get; }
    bool SupportsProfileLinks { get; }
    /// <summary>Whether this user can create the runtime folder there (the probed path or its nearest existing parent).</summary>
    bool Writable { get; }
    /// <summary>Whether the probed path is the mount point itself: an install there leaves no folder on its mount outside it.</summary>
    bool AtMountPoint { get; }
    ProfileLinkRules Rules { get; }
    bool SameVolume(IProfileStorageVolume other);
}

/// <summary>The Windows volume and file system that contain a path (or its nearest existing parent).</summary>
internal sealed record WindowsStorageVolume(string Root, string Identity, string FileSystem, bool Network) : IProfileStorageVolume
{
    internal bool SameVolume(WindowsStorageVolume other) =>
        Identity.Equals(other.Identity, StringComparison.OrdinalIgnoreCase);

    bool IProfileStorageVolume.SameVolume(IProfileStorageVolume other) => other is WindowsStorageVolume windows && SameVolume(windows);

    public bool SupportsProfileLinks => !Network && FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    public string Description => Network ? $"network share {Root} ({FileSystem})" : $"{Root} ({FileSystem})";

    // The Windows probe reads no permissions and no mount point: staging reports a folder it cannot create.
    bool IProfileStorageVolume.Writable => true;
    bool IProfileStorageVolume.AtMountPoint => false;

    ProfileLinkRules IProfileStorageVolume.Rules => ProfileLinkRules.Windows;
}

/// <summary>
/// The Linux mount or Mac volume that contains a path (or its nearest existing parent): its mount point, the device of that
/// path, the filesystem type, and whether this user can write to the path. A hard link needs one device on one mount: a bind
/// mount of the same device, or another btrfs subvolume, still refuses it (<c>EXDEV</c>), so both are compared.
/// </summary>
internal sealed record UnixStorageVolume(string MountPoint, string Device, string FileSystem, bool Writable) : IProfileStorageVolume
{
    // FAT and exFAT have no hard links.
    private static readonly string[] NoLinks = ["vfat", "msdos", "exfat"];

    /// <summary>The platform's wording: Linux's by default, a Mac's for a volume probed on macOS.</summary>
    public ProfileLinkRules Rules { get; init; } = ProfileLinkRules.Linux;

    public bool AtMountPoint { get; init; }

    public string Root => MountPoint;

    public string Description => $"{MountPoint} ({FileSystem}, device {Device})";

    public bool SupportsProfileLinks => !NoLinks.Contains(FileSystem, StringComparer.OrdinalIgnoreCase);

    public bool SameVolume(IProfileStorageVolume other) =>
        other is UnixStorageVolume unix && unix.Device == Device && unix.MountPoint == MountPoint;
}

/// <summary>
/// The one Unix storage probe, a bash script that a local, SSH or container host runs (and this machine, for the inventory's
/// default root). It names the host's system first (<c>VT-PROFILE-OS</c>, from <c>uname -s</c>), then each of the
/// newline-separated <c>paths</c> by its nearest existing parent: its device (<c>stat</c>, GNU or BSD), its mount point
/// (<c>df -P</c>, which matches a bind mount's own entry where GNU <c>stat</c>'s mount point does not, and which BusyBox has
/// too), its filesystem type (<c>/proc/self/mounts</c> on Linux, <c>mount</c> on macOS), whether it is writable and whether it
/// is the mount point itself. With <c>linktest</c> set, it also links the install's server executable once into a new folder
/// in the second path's nearest existing parent and removes both again: Linux's <c>fs.protected_hardlinks</c> refuses a link
/// to a file this user does not own and cannot write, which no listing shows.
/// </summary>
internal static class UnixStorageProbe
{
    internal static readonly string Script = """
        set -u
        os=$(uname -s) || exit 3
        echo "VT-PROFILE-OS $os"
        case "$os" in Linux|Darwin) ;; *) exit 0 ;; esac
        b64() { printf '%s' "$1" | base64 | tr -d '\n'; }
        i=0; existing=()
        while IFS= read -r path; do
          [ -n "$path" ] || continue
          while [ ! -e "$path" ]; do
            parent=$(dirname -- "$path")
            if [ "$parent" = "$path" ]; then echo "No existing parent could identify the mount for $path." >&2; exit 3; fi
            path=$parent
          done
          path=$(readlink -f -- "$path") || exit 3
          existing+=("$path")
          mount=$(df -P -- "$path" | tail -n 1 | sed -E 's|^.*[0-9]+% +(/.*)$|\1|')
          case "$mount" in /*) ;; *) echo "df -P named no mount point for $path." >&2; exit 3 ;; esac
          if [ "$os" = Linux ]; then
            device=$(stat -L -c %d -- "$path") || exit 3
            fs=$(awk -v m="$mount" '$2 == m { t = $3 } END { print t }' /proc/self/mounts 2> /dev/null)
            [ -n "$fs" ] || fs=$(stat -f -L -c %T -- "$path") || exit 3
          else
            device=$(stat -L -f %d -- "$path") || exit 3
            fs=$(mount | awk -v m="$mount" '{ i = index($0, " on " m " ("); if (i) { s = substr($0, i + length(m) + 6); sub(/[,)].*/, "", s); t = s } } END { print t }')
          fi
          [ -n "$fs" ] || fs=unknown
          if [ -w "$path" ]; then writable=1; else writable=0; fi
          if [ "$path" = "$mount" ]; then root=1; else root=0; fi
          printf 'VT-PROFILE-VOLUME %s %s %s %s %s %s\n' "$i" "$device" "$writable" "$root" "${fs// /_}" "$(b64 "$mount")"
          i=$((i + 1))
        done <<< "$paths"
        if [ -n "${linktest:-}" ] && [ "${#existing[@]}" -ge 2 ]; then
          case "$os" in Linux) exe=valheim_server.x86_64 ;; *) exe=valheim_server/Valheim ;; esac
          source=${existing[0]}; target=${existing[1]}
          if [ -f "$source/$exe" ] && [ -d "$target" ] && [ -w "$target" ]; then
            probe=$(mktemp -d "$target/.vt-link-probe.XXXXXX") || exit 3
            if error=$(ln -- "$source/$exe" "$probe/game" 2>&1); then echo 'VT-PROFILE-LINK ok'; else echo "VT-PROFILE-LINK refused $(b64 "$error")"; fi
            rm -f -- "$probe/game"; rmdir -- "$probe"
          fi
        fi
        echo 'VT-PROFILE-VOLUMES'
        """.ReplaceLineEndings("\n");

    /// <summary>The host's system as the probe named it (<c>uname -s</c>), or null when the reply has none.</summary>
    internal static string? SystemName(string stdout) =>
        stdout.Split('\n').Select(line => line.TrimEnd('\r')).FirstOrDefault(line => line.StartsWith("VT-PROFILE-OS ", StringComparison.Ordinal))?["VT-PROFILE-OS ".Length..];

    /// <summary>The platform name (<c>linux</c>, <c>macos</c>) of a probed system; a system the toolkit does not run on is refused.</summary>
    internal static string Platform(string system, string where) => system switch
    {
        "Linux" => "linux",
        "Darwin" => "macos",
        _ => throw new PlatformNotSupportedException($"{where} runs {system}; a profile needs a Linux, macOS or Windows host."),
    };

    /// <summary>The volumes of a probe's reply on <paramref name="platform"/>, in the order of its paths; refuses a reply that is incomplete.</summary>
    internal static IReadOnlyList<UnixStorageVolume> Volumes(string stdout, int count, string platform = "linux")
    {
        var lines = stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (!lines.Contains("VT-PROFILE-VOLUMES")) throw new InvalidDataException("The storage probe did not finish.");
        var volumes = new List<UnixStorageVolume>();
        foreach (string line in lines.Where(line => line.StartsWith("VT-PROFILE-VOLUME ", StringComparison.Ordinal)))
        {
            string[] parts = line.Split(' ');
            if (parts.Length != 7 || parts[1] != volumes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("Malformed storage probe line: " + line);
            string mount;
            try { mount = Encoding.UTF8.GetString(Convert.FromBase64String(parts[6])); }
            catch (FormatException) { throw new InvalidDataException("Malformed storage probe line: " + line); }
            volumes.Add(new UnixStorageVolume(mount, parts[2], parts[5], parts[3] == "1")
                { AtMountPoint = parts[4] == "1", Rules = ProfileLinkRules.For(platform) });
        }
        if (volumes.Count != count) throw new InvalidDataException($"The storage probe named {volumes.Count} of {count} paths.");
        return volumes;
    }

    /// <summary>Why the probe's trial link failed (<c>linktest</c>), or null when it linked or did not try.</summary>
    internal static string? LinkRefusal(string stdout)
    {
        string? line = stdout.Split('\n').Select(item => item.TrimEnd('\r')).FirstOrDefault(item => item.StartsWith("VT-PROFILE-LINK refused ", StringComparison.Ordinal));
        if (line == null) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(line["VT-PROFILE-LINK refused ".Length..])).Trim(); }
        catch (FormatException) { throw new InvalidDataException("Malformed storage probe line: " + line); }
    }

    /// <summary>This machine's mounts for <paramref name="paths"/>, through the same script run once by this machine's bash.</summary>
    internal static IReadOnlyList<UnixStorageVolume> Inspect(IReadOnlyList<string> paths)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix mount inspection runs only on Linux and macOS.");
        if (paths.Count == 0 || paths.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Name each path to inspect.", nameof(paths));
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Script);
        start.Environment["paths"] = string.Join('\n', paths);
        using var process = Process.Start(start) ?? throw new IOException("bash did not start for the storage probe.");
        // Both streams are read while the probe runs, so a stalled mount (a stale NFS server) is bounded by the wait, not by a read.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException($"The storage probe for {string.Join(", ", paths)} did not finish within 30 s.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"The storage probe for {string.Join(", ", paths)} failed (exit {process.ExitCode}): {error.GetAwaiter().GetResult().Trim()}");
        string stdout = output.GetAwaiter().GetResult();
        return Volumes(stdout, paths.Count, Platform(SystemName(stdout) ?? "", "this machine"));
    }
}

/// <summary>Raw Windows storage facts, before path syntax and drive type are classified in one place.</summary>
internal sealed record WindowsStorageFacts(string Path, string Root, uint DriveType, string? Identity, string? FileSystem);

/// <summary>One decision from either the native this-machine probe or the remote-host probe.</summary>
internal sealed record ProfileStorageVerdict(string? Description, string? Refusal, string? Warning = null)
{
    internal void Require()
    {
        if (Refusal != null) throw new InvalidOperationException(Refusal);
    }
}

internal static class WindowsStorageClassifier
{
    private const uint RemoteDrive = 4;
    private const string UncPrefix = @"\\";
    private static readonly string[] ExtendedUncPrefixes = [@"\\?\UNC\", @"\\.\UNC\"];
    private static readonly string[] ExtendedPrefixes = [@"\\?\", @"\\.\"];

    internal static WindowsStorageVolume Classify(WindowsStorageFacts facts)
    {
        bool network = IsNetwork(facts);
        if (network)
        {
            string root = NetworkRoot(facts.Path) ?? NetworkRoot(facts.Root) ?? facts.Root;
            return new(root, facts.Identity ?? root, facts.FileSystem ?? "network", true);
        }
        if (string.IsNullOrWhiteSpace(facts.Identity) || string.IsNullOrWhiteSpace(facts.FileSystem))
            throw new InvalidDataException($"The Windows storage probe returned no local volume identity or filesystem for {facts.Path}.");
        return new(facts.Root, facts.Identity, facts.FileSystem, false);
    }

    internal static bool IsNetwork(WindowsStorageFacts facts) =>
        facts.DriveType == RemoteDrive || NetworkSyntax(facts.Path) || NetworkSyntax(facts.Root);

    internal static bool NetworkSyntax(string path)
    {
        if (ExtendedUncPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        return path.StartsWith(UncPrefix, StringComparison.Ordinal) &&
            !ExtendedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static bool ExtendedSyntax(string path) =>
        ExtendedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

    internal static string PowerShellNetworkSyntax(string expression) =>
        $"({string.Join(" -or ", ExtendedUncPrefixes.Select(prefix => PowerShellStartsWith(expression, prefix, ignoreCase: true)))} -or " +
        $"({PowerShellStartsWith(expression, UncPrefix)} -and " +
        $"{string.Join(" -and ", ExtendedPrefixes.Select(prefix => "-not " + PowerShellStartsWith(expression, prefix)))}))";

    internal static string PowerShellExtendedSyntax(string expression) =>
        $"({string.Join(" -or ", ExtendedPrefixes.Select(prefix => PowerShellStartsWith(expression, prefix)))})";

    private static string PowerShellStartsWith(string expression, string prefix, bool ignoreCase = false) =>
        $"{expression}.StartsWith('{prefix}', [StringComparison]::{(ignoreCase ? "OrdinalIgnoreCase" : "Ordinal")})";

    internal static string? SyntaxRoot(string path)
    {
        string normal = path.Replace('/', '\\');
        if (normal.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            normal.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) normal = @"\\" + normal[8..];
        else if (normal.StartsWith(@"\\?\", StringComparison.Ordinal) && normal.Length >= 7 && normal[5] == ':') normal = normal[4..];
        if (normal.StartsWith(@"\\", StringComparison.Ordinal) &&
            !normal.StartsWith(@"\\?\", StringComparison.Ordinal) && !normal.StartsWith(@"\\.\", StringComparison.Ordinal))
            return NetworkRoot(normal);
        if (normal.Length >= 2 && normal[1] == ':') return char.ToUpperInvariant(normal[0]) + @":\";
        if (normal.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            int end = normal.IndexOf('\\', 4);
            return end >= 0 ? normal[..(end + 1)] : normal;
        }
        if (normal.StartsWith(@"\\.\", StringComparison.Ordinal) && normal.Length >= 7 && normal[5] == ':')
            return char.ToUpperInvariant(normal[4]) + @":\";
        return null;
    }

    private static string? NetworkRoot(string path)
    {
        string rest;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) rest = path[8..];
        else if (path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\.\", StringComparison.Ordinal)) rest = path[2..];
        else return null;
        string[] parts = rest.Split(['\\', '/'], 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] + @"\" : path;
    }
}

/// <summary>The root under which a this-machine profile is made, and the reason preflight prints.</summary>
internal sealed record ProfileRuntimeRoot(string Path, string Reason);

/// <summary>
/// One owner of a linked profile's storage: the default profile root and the verdict on a runtime folder. Linked profiles
/// (<see cref="ProfileLayout.Linked"/>) follow the install's Windows volume, Linux mount or Mac volume so their game files can
/// be hard-linked; a macOS client's profile keeps ValheimTesting's data folder because it launches the source bundle in place.
/// </summary>
internal static class ProfileRuntimeStorage
{
    internal static ProfileStorageVerdict Unknown(ProfileLinkRules rules, string sourcePath, Exception error) =>
        new(null, null, $"Could not read the {rules.Platform} {rules.Unit} for install {sourcePath}: {error.Message} Staging will try the hard links and report their error if they fail.");

    /// <summary>The root of a profile that launches its source executable: ValheimTesting's data folder.</summary>
    internal static ProfileRuntimeRoot SourceExecutableRoot(string dataRuns) =>
        new(dataRuns, "macOS client profiles launch the source game and keep their loader under ValheimTesting's data folder");

    /// <summary>
    /// The default root of this machine's profiles of <paramref name="install"/> (a dedicated server's when
    /// <paramref name="server"/>): ValheimTesting's data folder when it can hold the profile's links, else a folder at the
    /// install's volume root or mount point, never one inside the install.
    /// </summary>
    internal static ProfileRuntimeRoot DefaultRoot(ISteamLocator machine, string install, string? dataRoot = null, bool server = true)
    {
        dataRoot ??= machine.DataRoot;
        string dataRuns = HostPath.Join(dataRoot, "runs");
        if (ProfileLayouts.For(machine.Platform, server) == ProfileLayout.SourceExecutable) return SourceExecutableRoot(dataRuns);
        var rules = ProfileLinkRules.For(machine.Platform);
        if (string.IsNullOrEmpty(install))
            return new(dataRuns, $"the install was not detected, so no install {rules.Unit} could be selected");

        try
        {
            var (source, data) = Volumes(machine, machine.FinalPath(install), machine.FinalPath(dataRoot));
            return DefaultRoot(dataRuns, source, data);
        }
        catch (Exception error) when (ProbeFailure(error))
        {
            return new(dataRuns, $"the {rules.Platform} storage probe could not identify the install {rules.Unit} ({error.Message}); using ValheimTesting's data folder, and preflight will check it before staging");
        }
    }

    internal static ProfileRuntimeRoot DefaultRoot(string dataRuns, IProfileStorageVolume source, IProfileStorageVolume data)
    {
        if (source.SameVolume(data))
            return new(dataRuns, $"ValheimTesting's data folder is on the install {source.Rules.Unit} {source.Description}");
        // An install at its mount point holds every folder of its mount: the default stays in the data folder, which the verdict refuses.
        if (source.AtMountPoint)
            return new(dataRuns, $"the install is the mount point of {source.Description}, so no folder on its {source.Rules.Unit} lies outside it");
        string relocated = HostPath.Join(source.Root, "ValheimTesting", "runs");
        return new(relocated, $"the install is on {source.Description}, not the data folder's {data.Description}, so {source.Rules.Platform} profiles follow the install {source.Rules.Unit}");
    }

    internal static ProfileStorageVerdict Inspect(ISteamLocator machine, string sourcePath, string runtimePath,
        bool explicitRuntime, bool inPlaceAvailable = false)
    {
        try
        {
            var (source, runtime) = Volumes(machine, machine.FinalPath(sourcePath), machine.FinalPath(runtimePath));
            return Verdict(sourcePath, source, runtimePath, runtime, explicitRuntime, inPlaceAvailable);
        }
        catch (Exception error) when (ProbeFailure(error))
        {
            return Unknown(ProfileLinkRules.For(machine.Platform), sourcePath, error);
        }
    }

    // This machine's volumes (Windows) or mounts (Linux, macOS: one probe for both paths) holding two paths.
    private static (IProfileStorageVolume First, IProfileStorageVolume Second) Volumes(ISteamLocator machine, string first, string second)
    {
        if (machine.Platform == "windows") return (machine.StorageVolume(first), machine.StorageVolume(second));
        var volumes = machine.UnixStorageVolumes([first, second]);
        return (volumes[0], volumes[1]);
    }

    /// <summary>
    /// The verdict on linking <paramref name="sourcePath"/> into <paramref name="runtimePath"/>; <paramref name="linkError"/> is
    /// the error of the storage probe's trial link (<see cref="UnixStorageProbe.LinkRefusal"/>), when it failed.
    /// </summary>
    internal static ProfileStorageVerdict Verdict(string sourcePath, IProfileStorageVolume source, string runtimePath,
        IProfileStorageVolume runtime, bool explicitRuntime, bool inPlaceAvailable = false, string? linkError = null) =>
        new($"source {sourcePath} and runtime {runtimePath} are on {source.Description}",
            Refusal(sourcePath, source, runtimePath, runtime, explicitRuntime, inPlaceAvailable, linkError));

    internal static ProfileStorageVerdict PlacementVerdict(string sourcePath, string runtimePath, bool explicitRuntime)
    {
        if (!explicitRuntime) return new(null, null);
        string? source = WindowsStorageClassifier.SyntaxRoot(sourcePath), runtime = WindowsStorageClassifier.SyntaxRoot(runtimePath);
        if (source == null || runtime == null || source.Equals(runtime, StringComparison.OrdinalIgnoreCase)) return new(null, null);
        return new(null, CrossVolumeRefusal(ProfileLinkRules.Windows, explicitRuntime, runtimePath, runtime, source, source));
    }

    /// <summary>
    /// The one wording of the same-volume rule, for the filesystem verdict and the path-syntax placement verdict alike: a
    /// profile hard-links the install's files, and a hard link cannot cross volumes (on Linux, mounts).
    /// </summary>
    private static string CrossVolumeRefusal(ProfileLinkRules rules, bool explicitRuntime, string runtimePath, string runtimeVolume,
        string sourceVolume, string sourceRoot) =>
        $"the {(explicitRuntime ? "explicit inventory runtime folder" : "default runtime folder")} {runtimePath} is on {runtimeVolume}, " +
        $"not the source install's {rules.Unit} {sourceVolume}. A {rules.Platform} profile hard-links the install's game files, and a hard link cannot cross {rules.Unit}s. " +
        $"Put {(explicitRuntime ? "the inventory runtime" : "the default runtime")} on the install's {rules.Home} {sourceRoot}.";

    internal static string? Refusal(string sourcePath, IProfileStorageVolume source, string runtimePath,
        IProfileStorageVolume runtime, bool explicitRuntime = true, bool inPlaceAvailable = false, string? linkError = null)
    {
        var rules = source.Rules;
        string alternative = inPlaceAvailable ? " An owned client can instead use --in-place." : "";
        string runtimeKind = explicitRuntime ? "explicit inventory runtime folder" : "default runtime folder";
        string runtimeName = explicitRuntime ? "the inventory runtime" : "the default runtime";
        if (!source.SupportsProfileLinks)
            return $"the source install {sourcePath} is on {source.Description}, which cannot hold the hard links a {rules.Platform} profile requires. " +
                $"Move the Steam library to {rules.FileSystems}." + alternative;
        if (source.AtMountPoint)
            return $"the source install {sourcePath} is the mount point of {source.Description}, so a {rules.Platform} profile, which must be on " +
                $"the install's {rules.Unit} and outside the install, has nowhere to go. Move the install into a folder of that filesystem " +
                $"(for example {HostPath.Join(source.Root, "valheim")}) and set the inventory `runtime` to another folder on it." + alternative;
        if (!runtime.SupportsProfileLinks)
            return $"the {runtimeKind} {runtimePath} is on {runtime.Description}, which cannot hold the hard links a {rules.Platform} profile requires. " +
                $"Put {runtimeName} on the install's {rules.Home} {source.Root}." + alternative;
        if (!source.SameVolume(runtime))
            return CrossVolumeRefusal(rules, explicitRuntime, runtimePath, runtime.Description, source.Description, source.Root) + alternative;
        if (!runtime.Writable)
            return $"the {runtimeKind} {runtimePath} on {runtime.Description} is not writable by this user. " +
                $"Create it with write access for this user, or put {runtimeName} in a writable folder on the install's {rules.Home} {source.Root}." + alternative;
        if (linkError != null)
            return $"this user cannot hard-link the install's game files from {sourcePath} into the {runtimeKind} {runtimePath}: {linkError}. " +
                (rules == ProfileLinkRules.Linux
                    ? "Linux's fs.protected_hardlinks (on by default) allows a link only to a file this user owns or can write: run as the install's owner, " +
                      "or give this user the install (for example `sudo chown -R` it, or install it with this user's SteamCMD)."
                    : $"Run as the install's owner, or put {runtimeName} where this user can link the install's files.") + alternative;
        return null;
    }

    private static bool ProbeFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException or
        ArgumentException or NotSupportedException;
}

internal static class WindowsStorageProbe
{
    internal sealed record Probes(Func<string, string> FullPath, Func<string, bool> Exists,
        Func<string, string?> Parent, Func<string, string> VolumePath, Func<string, uint> DriveType,
        Func<string, string> VolumeName, Func<string, string> FileSystem);

    internal static WindowsStorageVolume Inspect(string path, Probes? probes = null)
    {
        if (probes == null && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows volume inspection runs only on Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        probes ??= NativeProbes;
        string existing = ExistingPath(probes.FullPath(path), probes);
        string root = probes.VolumePath(existing);
        uint driveType = probes.DriveType(root);
        var raw = new WindowsStorageFacts(existing, root, driveType, null, null);
        if (WindowsStorageClassifier.IsNetwork(raw)) return WindowsStorageClassifier.Classify(raw);
        return WindowsStorageClassifier.Classify(raw with
        {
            Identity = probes.VolumeName(root),
            FileSystem = probes.FileSystem(root),
        });
    }

    private static readonly Probes NativeProbes = new(Path.GetFullPath,
        path => Directory.Exists(path) || File.Exists(path),
        path => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)),
        VolumePath, GetDriveType, VolumeName, FileSystem);

    private static string ExistingPath(string path, Probes probes)
    {
        string? current = path;
        while (current != null && !probes.Exists(current))
            current = probes.Parent(current);
        return current ?? throw new DirectoryNotFoundException($"No existing parent could identify the Windows volume for {path}.");
    }

    private static string VolumePath(string path)
    {
        var volumePath = new StringBuilder(32768);
        if (!GetVolumePathName(path, volumePath, (uint)volumePath.Capacity))
            throw new IOException($"Could not find the Windows volume for {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return volumePath.ToString();
    }

    private static string VolumeName(string root)
    {
        var volumeName = new StringBuilder(32768);
        if (!GetVolumeNameForVolumeMountPoint(root, volumeName, (uint)volumeName.Capacity))
            throw new IOException($"Could not identify the Windows volume for {root}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return volumeName.ToString();
    }

    private static string FileSystem(string root)
    {
        var fileSystem = new StringBuilder(256);
        if (!GetVolumeInformation(root, null, 0, out _, out _, out _, fileSystem, (uint)fileSystem.Capacity))
            throw new IOException($"Could not read the Windows filesystem for {root}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return fileSystem.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string rootPathName);
}
