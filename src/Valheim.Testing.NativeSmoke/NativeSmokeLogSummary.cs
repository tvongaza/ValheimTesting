using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>A compact console view of the structured teardown log scan kept in <c>result.json</c>.</summary>
internal static class NativeSmokeLogSummary
{
    private const int MostFrequent = 3;

    internal static string Format(IEnumerable<LogFileScan> logs, string? arm = null)
    {
        var scans = logs.ToList();
        string prefix = arm == null ? "" : arm + ": ";
        if (scans.Count == 0) return prefix + "log scan: not run";
        var counts = scans.SelectMany(scan => scan.Counts.Select(count => (scan.Role, Count: count)))
            .Where(item => item.Count.Count > 0).ToList();
        int failures = counts.Where(item => item.Count.Severity == LogSeverity.Failure).Sum(item => item.Count.Count);
        int warnings = counts.Where(item => item.Count.Severity == LogSeverity.Warning).Sum(item => item.Count.Count);
        int expected = scans.SelectMany(scan => scan.Counts).Sum(count => count.Expected);
        int missing = scans.Count(scan => scan.Problem != null);
        string frequent = counts.Count == 0 ? "none" : string.Join(", ", counts
            .OrderByDescending(item => item.Count.Count)
            .ThenBy(item => item.Role, StringComparer.Ordinal)
            .ThenBy(item => item.Count.Pattern, StringComparer.Ordinal)
            .Take(MostFrequent)
            .Select(item => $"{item.Role}/{item.Count.Pattern} x{item.Count.Count}"));
        return $"{prefix}log scan: {scans.Count(scan => scan.Present)}/{scans.Count} logs present; " +
            $"failure={failures} warning={warnings} expected={expected} required-missing={missing}; most frequent: {frequent}";
    }

    internal static void WriteReport(string directory, string? arm = null, TextWriter? output = null)
    {
        string file = Path.Combine(directory, "result.json");
        IReadOnlyList<LogFileScan> logs = [];
        if (File.Exists(file))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.TryGetProperty("Logs", out var value))
                logs = JsonSerializer.Deserialize<List<LogFileScan>>(value.GetRawText()) ?? [];
        }
        (output ?? Console.Out).WriteLine(Format(logs, arm));
    }
}
