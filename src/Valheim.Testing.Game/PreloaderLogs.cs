using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// BepInEx's preloader crash logs (<c>preloader_*.log</c> beside the game), the one account of why a client wrote no BepInEx log
/// line (#254, #409): the ones this launch wrote (not older than the launch) with the first <c>[Error</c> or <c>[Fatal</c> line of
/// each, and the older ones, which an earlier start left and which explain nothing, only named. A client on this machine is
/// read here; a hosted client's host script reports the same facts for its host, and both fail with <see cref="Explain"/>.
/// </summary>
internal static class PreloaderLogs
{
    /// <summary>What the preloader logs beside a game showed for one launch.</summary>
    internal sealed record Reading(IReadOnlyList<(string Name, string? FirstError)> Fresh, IReadOnlyList<string> Stale);

    internal const string Pattern = "preloader_*.log";
    private static readonly Regex ErrorLine = new(@"\[(Error|Fatal)", RegexOptions.CultureInvariant);

    /// <summary>The preloader logs beside <paramref name="install"/> for a launch at <paramref name="launchedUtc"/>; null when they cannot be read.</summary>
    internal static Reading? Read(string install, DateTime launchedUtc)
    {
        try
        {
            var fresh = new List<(string, string?)>();
            var stale = new List<string>();
            if (!Directory.Exists(install)) return new Reading(fresh, stale);
            foreach (string file in Directory.GetFiles(install, Pattern).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(file);
                if (File.GetLastWriteTimeUtc(file) < launchedUtc) { stale.Add(name); continue; }
                fresh.Add((name, FirstError(file)));
            }
            return new Reading(fresh, stale);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    // A log the game still holds open may not be readable: it is still this launch's, with no line to quote.
    private static string? FirstError(string file)
    {
        try { return File.ReadLines(file).FirstOrDefault(line => ErrorLine.IsMatch(line))?.Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Copies each log this launch wrote into <paramref name="directory"/> as <c>&lt;prefix&gt;game-2.preloader-N.log</c>, as a hosted
    /// client's evidence keeps them; best effort, since the failure it explains is already the run's result.
    /// </summary>
    internal static void Keep(string install, Reading reading, string directory, string prefix)
    {
        int n = 0;
        foreach (var (name, _) in reading.Fresh)
            try { File.Copy(Path.Combine(install, name), Path.Combine(directory, $"{prefix}game-2.preloader-{++n}.log"), overwrite: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The failure's explanation: the first error of a log this launch wrote (kept as <paramref name="keptAs"/>), or that one was
    /// written with no error line, else <paramref name="otherwise"/>; then the older logs, named as not this launch's.
    /// </summary>
    internal static string Explain(Reading? reading, string keptAs, string otherwise)
    {
        var failed = reading?.Fresh.Where(log => log.FirstError != null).Select(log => ((string Name, string Error)?)(log.Name, log.FirstError!)).FirstOrDefault();
        string why = failed is { } hit
            ? $"BepInEx's preloader failed: {hit.Error} (from {hit.Name}, which the client's evidence keeps as {keptAs}). "
            : reading?.Fresh.Count > 0 ? $"BepInEx's preloader wrote {string.Join(", ", reading.Fresh.Select(log => log.Name))} with no error line (the client's evidence keeps it as {keptAs}). "
            : otherwise;
        string stale = reading?.Stale.Count > 0 ? $"Older preloader logs beside the game ({string.Join(", ", reading.Stale)}) predate this launch and are not its. " : "";
        return why + stale;
    }
}
