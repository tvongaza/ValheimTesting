using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Valheim.Testing.Game;

/// <summary>The Windows volume and file system that contain a path (or its nearest existing parent).</summary>
internal sealed record WindowsStorageVolume(string Root, string Identity, string FileSystem, bool Network)
{
    internal bool SameVolume(WindowsStorageVolume other) =>
        Identity.Equals(other.Identity, StringComparison.OrdinalIgnoreCase);

    internal bool SupportsProfileLinks => !Network && FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    internal string Description => Network ? $"network share {Root} ({FileSystem})" : $"{Root} ({FileSystem})";
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
/// One owner of the default profile root on this machine. Windows profiles follow the install's volume so their game files
/// can be hard-linked; Unix profiles keep ValheimTesting's data folder because they launch the source executable in place.
/// </summary>
internal static class ProfileRuntimeStorage
{
    internal static ProfileStorageVerdict Unknown(string sourcePath, Exception error) =>
        new(null, null, $"Could not read the Windows volume for install {sourcePath}: {error.Message} Staging will try the hard links and report their error if they fail.");

    internal static ProfileRuntimeRoot DefaultRoot(ISteamLocator machine, string install, string? dataRoot = null)
    {
        dataRoot ??= machine.DataRoot;
        string dataRuns = HostPath.Join(dataRoot, "runs");
        if (machine.Platform != "windows")
            return new(dataRuns, "Unix profiles launch the source game and keep their loader under ValheimTesting's data folder");
        if (string.IsNullOrEmpty(install))
            return new(dataRuns, "the install was not detected, so no install volume could be selected");

        try
        {
            string finalInstall = machine.FinalPath(install);
            var source = machine.StorageVolume(finalInstall);
            var data = machine.StorageVolume(machine.FinalPath(dataRoot));
            return DefaultRoot(dataRuns, source, data);
        }
        catch (Exception error) when (ProbeFailure(error))
        {
            return new(dataRuns, $"the Windows storage probe could not identify the install volume ({error.Message}); using ValheimTesting's data folder, and preflight will check it before staging");
        }
    }

    internal static ProfileRuntimeRoot DefaultRoot(string dataRuns, WindowsStorageVolume source, WindowsStorageVolume data)
    {
        if (source.SameVolume(data))
            return new(dataRuns, $"ValheimTesting's data folder is on the install volume {source.Description}");
        string relocated = HostPath.Join(source.Root, "ValheimTesting", "runs");
        return new(relocated, $"the install is on {source.Description}, not the data folder's {data.Description}, so Windows profiles follow the install volume");
    }

    internal static ProfileStorageVerdict Inspect(ISteamLocator machine, string sourcePath, string runtimePath,
        bool explicitRuntime, bool inPlaceAvailable = false)
    {
        try
        {
            var source = machine.StorageVolume(machine.FinalPath(sourcePath));
            var runtime = machine.StorageVolume(machine.FinalPath(runtimePath));
            return Verdict(sourcePath, source, runtimePath, runtime, explicitRuntime, inPlaceAvailable);
        }
        catch (Exception error) when (ProbeFailure(error))
        {
            return Unknown(sourcePath, error);
        }
    }

    internal static ProfileStorageVerdict Verdict(string sourcePath, WindowsStorageVolume source, string runtimePath,
        WindowsStorageVolume runtime, bool explicitRuntime, bool inPlaceAvailable = false) =>
        new($"source {sourcePath} and runtime {runtimePath} are on {source.Description}",
            Refusal(sourcePath, source, runtimePath, runtime, explicitRuntime, inPlaceAvailable));

    internal static ProfileStorageVerdict PlacementVerdict(string sourcePath, string runtimePath, bool explicitRuntime)
    {
        if (!explicitRuntime) return new(null, null);
        string? source = WindowsStorageClassifier.SyntaxRoot(sourcePath), runtime = WindowsStorageClassifier.SyntaxRoot(runtimePath);
        if (source == null || runtime == null || source.Equals(runtime, StringComparison.OrdinalIgnoreCase)) return new(null, null);
        return new(null, CrossVolumeRefusal(explicitRuntime, runtimePath, runtime, source, source));
    }

    /// <summary>
    /// The one wording of the same-volume rule, for the filesystem verdict and the path-syntax placement verdict alike: a
    /// profile hard-links the install's files, and a hard link cannot cross volumes.
    /// </summary>
    private static string CrossVolumeRefusal(bool explicitRuntime, string runtimePath, string runtimeVolume, string sourceVolume,
        string sourceRoot) =>
        $"the {(explicitRuntime ? "explicit inventory runtime folder" : "default runtime folder")} {runtimePath} is on {runtimeVolume}, " +
        $"not the source install's volume {sourceVolume}. A Windows profile hard-links the install's game files, and a hard link cannot cross volumes. " +
        $"Put {(explicitRuntime ? "the inventory runtime" : "the default runtime")} on the install's NTFS volume {sourceRoot}.";

    internal static string? Refusal(string sourcePath, WindowsStorageVolume source, string runtimePath,
        WindowsStorageVolume runtime, bool explicitRuntime = true, bool inPlaceAvailable = false)
    {
        string alternative = inPlaceAvailable ? " An owned client can instead use --in-place." : "";
        string runtimeKind = explicitRuntime ? "explicit inventory runtime folder" : "default runtime folder";
        if (!source.SupportsProfileLinks)
            return $"the source install {sourcePath} is on {source.Description}, which cannot hold the hard links a Windows profile requires. " +
                "Move the Steam library to NTFS." + alternative;
        if (!runtime.SupportsProfileLinks)
            return $"the {runtimeKind} {runtimePath} is on {runtime.Description}, which cannot hold the hard links a Windows profile requires. " +
                $"Put {(explicitRuntime ? "the inventory runtime" : "the default runtime")} on the install's NTFS volume {source.Root}." + alternative;
        if (!source.SameVolume(runtime))
            return CrossVolumeRefusal(explicitRuntime, runtimePath, runtime.Description, source.Description, source.Root) + alternative;
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
