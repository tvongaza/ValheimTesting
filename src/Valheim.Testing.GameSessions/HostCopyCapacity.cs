using System.Globalization;
using System.Text;

namespace Valheim.Testing.Game;

/// <summary>A read-only estimate of one source install's copy size and the target volume's available space.</summary>
internal sealed record HostCopyCapacity(long SourceBytes, long FreeBytes, string Volume);

/// <summary>Checks the combined space for actors sharing a volume before campaign preparation copies a game.</summary>
internal static class HostCopyCapacityProbe
{
    internal static async Task<HostCopyCapacity> InspectAsync(IGameHost host, string source, string runtime,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? Windows : Bash,
            new Dictionary<string, string> { ["source"] = source, ["runtime"] = runtime }, timeout, cancellation)
            .ConfigureAwait(false)).EnsureSuccess($"Checking copy space on {host.Name}");
        string line = InteractiveClient.Line(result.Stdout, "VT-STORAGE ") ??
            throw new HostOperationException($"No storage verdict from {host.Name}", result);
        string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long free) ||
            bytes < 0 || free < 0)
            throw new HostOperationException($"Invalid storage verdict from {host.Name}", result);
        string volume;
        try { volume = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])); }
        catch (FormatException) { throw new HostOperationException($"Invalid storage volume from {host.Name}", result); }
        if (string.IsNullOrWhiteSpace(volume)) throw new HostOperationException($"Empty storage volume from {host.Name}", result);
        return new HostCopyCapacity(bytes, free, volume);
    }

    /// <summary>Refuse when all actors copying onto one volume together exceed available space and headroom.</summary>
    internal static void RequireCombined(string host, IEnumerable<(string Actor, HostCopyCapacity Capacity)> actors)
    {
        foreach (var group in actors.GroupBy(item => item.Capacity.Volume, StringComparer.OrdinalIgnoreCase))
        {
            long bytes = group.Sum(item => item.Capacity.SourceBytes);
            long free = group.Min(item => item.Capacity.FreeBytes);
            long needed = checked(bytes + DiskSpace.Headroom(bytes));
            if (free < needed)
                throw new IOException($"Host {host} has {DiskSpace.Format(free)} free on {group.Key}; copying " +
                    $"{string.Join(", ", group.Select(item => item.Actor))} needs about {DiskSpace.Format(needed)} " +
                    $"({DiskSpace.Format(bytes)} of installs plus {DiskSpace.Format(DiskSpace.Headroom(bytes))} headroom).");
        }
    }

    internal static readonly string Bash = """
        test -d "$source" || { printf 'missing source install: %s\n' "$source" >&2; exit 4; }
        parent=$runtime
        while ! test -d "$parent"; do
          next=$(dirname "$parent")
          test "$next" != "$parent" || { printf 'no existing target volume\n' >&2; exit 4; }
          parent=$next
        done
        source_kb=$(du -sk "$source" | awk '{print $1}') || exit 4
        disk=$(df -Pk "$parent" | awk 'NR==2 {print $4 " " $1}') || exit 4
        free_kb=${disk%% *}
        device=${disk#* }
        case "$source_kb:$free_kb" in *[!0-9:]*|:*|*:) exit 4;; esac
        volume=$(printf '%s' "$device" | base64 | tr -d '\n')
        printf 'VT-STORAGE %s %s %s\n' "$((source_kb * 1024))" "$((free_kb * 1024))" "$volume"
        """;

    internal static readonly string Windows = """
        if (-not [IO.Directory]::Exists($source)) { throw "missing source install: $source" }
        $size = (Get-ChildItem -LiteralPath $source -File -Recurse -Force -ErrorAction Stop | Measure-Object -Property Length -Sum).Sum
        if ($null -eq $size) { $size = 0 }
        $volume = [IO.Path]::GetPathRoot($runtime)
        if (-not $volume) { throw "no target volume for $runtime" }
        $free = ([IO.DriveInfo]::new($volume)).AvailableFreeSpace
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($volume))
        'VT-STORAGE ' + [long]$size + ' ' + [long]$free + ' ' + $encoded
        """;
}
