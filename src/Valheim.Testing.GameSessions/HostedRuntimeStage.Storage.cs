using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

internal static partial class HostedRuntimeStage
{
    /// <summary>
    /// A profile's layout on its host (<see cref="ProfileLayouts.For"/>), the host's platform (<c>windows</c>, <c>linux</c> or
    /// <c>macos</c>) and, for a linked one, what its storage verdict found.
    /// </summary>
    internal sealed record ProfileStorageCheck(ProfileLayout Layout, string Platform, string? Description);

    /// <summary>
    /// Decides a profile's layout from its host's platform and, for a linked profile, checks the two roots it will hard-link
    /// between, without creating the profile. PowerShell is Windows; a bash host names its system through the Unix storage probe
    /// (<see cref="UnixStorageProbe"/>), which only a server's profile asks: Linux runs no client, so a bash host's client profile
    /// is a Mac's, launched from its source. A runtime on another volume or mount, on a filesystem without hard links, or where
    /// this user cannot link the install's executable (the probe's one trial link, removed at once) is refused.
    /// </summary>
    internal static async Task<ProfileStorageCheck> RequireProfileStorageAsync(IGameHost host, HostedRuntimeKind kind, string source,
        string runtime, TimeSpan timeout, CancellationToken cancellation = default, bool explicitRuntime = false, bool inPlaceAvailable = false)
    {
        bool server = kind == HostedRuntimeKind.Server;
        ProfileStorageVerdict verdict;
        if (host.Shell.Kind == HostShellKind.PowerShell)
        {
            try
            {
                if (host is LocalGameHost && OperatingSystem.IsWindows())
                    verdict = ProfileRuntimeStorage.Inspect(EnvironmentInventory.ThisMachine, source, runtime, explicitRuntime, inPlaceAvailable);
                else
                {
                    var volumes = await ProbeProfileStorageAsync(host, source, runtime, timeout, cancellation).ConfigureAwait(false);
                    verdict = ProfileRuntimeStorage.Verdict(source, volumes.Source, runtime, volumes.Runtime, explicitRuntime, inPlaceAvailable);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException or HostOperationException)
            {
                verdict = ProfileRuntimeStorage.Unknown(ProfileLinkRules.Windows, source, error);
            }
        }
        else
        {
            var probe = server ? await ProbeUnixStorageAsync(host, [source, runtime], timeout, cancellation, linkTest: true).ConfigureAwait(false) : default;
            string platform = server ? probe.Platform : "macos";
            if (ProfileLayouts.For(platform, server) == ProfileLayout.SourceExecutable)
                return new(ProfileLayout.SourceExecutable, platform, null);
            verdict = probe.Volumes is { } volumes
                ? ProfileRuntimeStorage.Verdict(source, volumes[0], runtime, volumes[1], explicitRuntime, inPlaceAvailable, probe.LinkError)
                : ProfileRuntimeStorage.Unknown(ProfileLinkRules.For(platform), source, probe.Error!);
            verdict.Require();
            if (verdict.Warning != null) Console.WriteLine("warning: " + verdict.Warning);
            return new(ProfileLayout.Linked, platform, verdict.Description);
        }
        verdict.Require();
        if (verdict.Warning != null) Console.WriteLine("warning: " + verdict.Warning);
        return new(ProfileLayout.Linked, "windows", verdict.Description);
    }

    /// <summary>
    /// Chooses a default profile root from a caller-supplied host, by the rule <see cref="ProfileRuntimeStorage.DefaultRoot(string, IProfileStorageVolume, IProfileStorageVolume)"/>
    /// gives this machine. Production's real local Windows host uses the native probe; wrappers and controlled hosts answer
    /// through their own script, so a test's paths never consult the runner machine.
    /// </summary>
    internal static async Task<ProfileRuntimeRoot> DefaultProfileRootAsync(IGameHost host, HostedRuntimeKind kind, string source, string dataRoot,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        string dataRuns = HostPath.Join(dataRoot, "runs");
        bool windows = host.Shell.Kind == HostShellKind.PowerShell, server = kind == HostedRuntimeKind.Server;
        var rules = windows ? ProfileLinkRules.Windows : ProfileLinkRules.Linux;
        try
        {
            if (windows && host is LocalGameHost && OperatingSystem.IsWindows())
                return ProfileRuntimeStorage.DefaultRoot(EnvironmentInventory.ThisMachine, source, dataRoot, server);
            if (windows)
            {
                var volumes = await ProbeProfileStorageAsync(host, source, dataRoot, timeout, cancellation).ConfigureAwait(false);
                return ProfileRuntimeStorage.DefaultRoot(dataRuns, volumes.Source, volumes.Runtime);
            }
            // As RequireProfileStorageAsync: only a server's profile asks a bash host its system.
            var probe = server ? await ProbeUnixStorageAsync(host, [source, dataRoot], timeout, cancellation).ConfigureAwait(false) : default;
            if (ProfileLayouts.For(server ? probe.Platform : "macos", server) == ProfileLayout.SourceExecutable)
                return ProfileRuntimeStorage.SourceExecutableRoot(dataRuns);
            rules = ProfileLinkRules.For(probe.Platform);
            if (probe.Error != null) throw probe.Error;
            return ProfileRuntimeStorage.DefaultRoot(dataRuns, probe.Volumes![0], probe.Volumes[1]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException or HostOperationException)
        {
            return new(dataRuns, $"the {rules.Platform} storage probe could not identify the install {rules.Unit} ({error.Message}); using ValheimTesting's data folder, and preflight will check it before staging");
        }
    }

    // A bash host's system and each path's mount, with the trial link's error when linkTest asked for one. A reply that names no
    // system refuses (the layout is unknown); one whose mounts could not be read returns why, for the verdict's warning.
    private static async Task<(string Platform, IReadOnlyList<UnixStorageVolume>? Volumes, string? LinkError, Exception? Error)> ProbeUnixStorageAsync(
        IGameHost host, IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken cancellation, bool linkTest = false)
    {
        var result = await host.RunAsync(UnixStorageProbe.Script, new Dictionary<string, string>
        {
            ["paths"] = string.Join('\n', paths), ["linktest"] = linkTest ? "1" : "",
        }, timeout, cancellation).ConfigureAwait(false);
        if (UnixStorageProbe.SystemName(result.Stdout) is not { } system)
        {
            result.EnsureSuccess($"Inspecting the profile storage on {host.Name}");
            throw new HostOperationException($"No profile-storage verdict from {host.Name}", result);
        }
        string platform = UnixStorageProbe.Platform(system, host.Name);
        try
        {
            result.EnsureSuccess($"Inspecting the {ProfileLinkRules.For(platform).Platform} profile storage on {host.Name}");
            return (platform, UnixStorageProbe.Volumes(result.Stdout, paths.Count, platform), UnixStorageProbe.LinkRefusal(result.Stdout), null);
        }
        catch (Exception error) when (error is HostOperationException or InvalidDataException) { return (platform, null, null, error); }
    }

    private static async Task<(WindowsStorageVolume Source, WindowsStorageVolume Runtime)> ProbeProfileStorageAsync(IGameHost host,
        string source, string runtime, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = await host.RunAsync(WindowsProfileStorage,
            new Dictionary<string, string> { ["source"] = source, ["runtime"] = runtime }, timeout, cancellation).ConfigureAwait(false);
        result.EnsureSuccess($"Inspecting Windows profile storage on {host.Name}");
        string? line = InteractiveClient.Line(result.Stdout, "VT-PROFILE-STORAGE");
        if (line == null) throw new HostOperationException($"No profile-storage verdict from {host.Name}", result);
        StorageReply reply;
        try
        {
            string encoded = line.Trim();
            reply = JsonSerializer.Deserialize<StorageReply>(Convert.FromBase64String(encoded))
                ?? throw new InvalidDataException("The profile-storage reply was empty.");
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidDataException)
        { throw new HostOperationException($"Malformed profile-storage reply from {host.Name}: {error.Message}", result); }
        return (reply.Source.ToVolume(), reply.Runtime.ToVolume());
    }

    private sealed record StorageReply(StorageItem Source, StorageItem Runtime);
    private sealed record StorageItem(string Path, string Root, uint DriveType, string? Identity, string? FileSystem)
    {
        internal WindowsStorageVolume ToVolume() => WindowsStorageClassifier.Classify(new(Path, Root, DriveType, Identity, FileSystem));
    }

    internal static readonly string WindowsProfileStorage = $$"""
        function Find-ProfileVolume([string] $path) {
            $candidate = [IO.Path]::GetFullPath($path)
            while (-not [IO.Directory]::Exists($candidate) -and -not [IO.File]::Exists($candidate)) {
                $trimmed = $candidate.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                $parent = [IO.Path]::GetDirectoryName($trimmed)
                if (-not $parent -or $parent -eq $candidate) { throw "No existing parent could identify the Windows volume for $path." }
                $candidate = $parent
            }
            $full = [IO.Path]::GetFullPath($candidate)
            $root = [IO.Path]::GetPathRoot($full)
            $networkSyntax = {{WindowsStorageClassifier.PowerShellNetworkSyntax("$full")}} -or {{WindowsStorageClassifier.PowerShellNetworkSyntax("$root")}}
            $extendedSyntax = {{WindowsStorageClassifier.PowerShellExtendedSyntax("$full")}} -or {{WindowsStorageClassifier.PowerShellExtendedSyntax("$root")}}
            if ($networkSyntax) {
                return @{ Path = $full; Root = $root; DriveType = [uint32][IO.DriveType]::Network; Identity = $null; FileSystem = $null }
            }
            # DriveInfo rejects UNC and extended roots. UNC was classified above; Get-Volume handles local extended paths.
            $driveType = if ($extendedSyntax) { [uint32][IO.DriveType]::Fixed } else { [uint32]([IO.DriveInfo]::new($root).DriveType) }
            if ($driveType -eq [uint32][IO.DriveType]::Network) {
                return @{ Path = $full; Root = $root; DriveType = $driveType; Identity = $null; FileSystem = $null }
            }
            $volume = Get-Volume -FilePath $full -ErrorAction Stop
            $identity = if ($volume.UniqueId) { [string]$volume.UniqueId } else { [string]$volume.Path }
            return @{ Path = $full; Root = $root; DriveType = $driveType; Identity = $identity; FileSystem = [string]$volume.FileSystem }
        }
        $reply = @{ Source = Find-ProfileVolume $source; Runtime = Find-ProfileVolume $runtime } | ConvertTo-Json -Compress -Depth 3
        'VT-PROFILE-STORAGE ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($reply))
        """.ReplaceLineEndings("\n");
}
