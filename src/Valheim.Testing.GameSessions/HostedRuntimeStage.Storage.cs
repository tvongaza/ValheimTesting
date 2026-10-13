using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

internal static partial class HostedRuntimeStage
{
    /// <summary>
    /// Checks the two roots a Windows profile will hard-link between, without creating either profile or probe file. Unix
    /// profiles launch the source executable and need no hard-link storage check.
    /// </summary>
    internal static async Task<string?> RequireProfileStorageAsync(IGameHost host, string source, string runtime,
        TimeSpan timeout, CancellationToken cancellation = default, bool explicitRuntime = false, bool inPlaceAvailable = false)
    {
        if (host.Shell.Kind != HostShellKind.PowerShell) return null;
        ProfileStorageVerdict verdict;
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
            verdict = ProfileRuntimeStorage.Unknown(source, error);
        }
        verdict.Require();
        if (verdict.Warning != null) Console.WriteLine("warning: " + verdict.Warning);
        return verdict.Description;
    }

    /// <summary>
    /// Chooses a Windows default profile root from a caller-supplied host. Production's real local host uses the native probe;
    /// wrappers and controlled hosts answer through their own script, so a test's paths never consult the runner machine.
    /// </summary>
    internal static async Task<ProfileRuntimeRoot> DefaultProfileRootAsync(IGameHost host, string source, string dataRoot,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        string dataRuns = HostPath.Join(dataRoot, "runs");
        if (host.Shell.Kind != HostShellKind.PowerShell)
            return new(dataRuns, "Unix profiles launch the source game and keep their loader under ValheimTesting's data folder");
        try
        {
            if (host is LocalGameHost && OperatingSystem.IsWindows())
                return ProfileRuntimeStorage.DefaultRoot(EnvironmentInventory.ThisMachine, source, dataRoot);
            var volumes = await ProbeProfileStorageAsync(host, source, dataRoot, timeout, cancellation).ConfigureAwait(false);
            return ProfileRuntimeStorage.DefaultRoot(dataRuns, volumes.Source, volumes.Runtime);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException or HostOperationException)
        {
            return new(dataRuns, $"the Windows storage probe could not identify the install volume ({error.Message}); using ValheimTesting's data folder, and preflight will check it before staging");
        }
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
