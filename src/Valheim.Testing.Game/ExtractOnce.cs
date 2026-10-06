namespace Valheim.Testing.Game;

/// <summary>
/// The one owner of "a pinned zip extracted once into a folder under ValheimTesting's own folder on this machine": the
/// ValheimCLI bundle (<see cref="CliBundle.Extract"/>) and valheim-test's shipped BepInExPack (<c>ShippedLoader.Extract</c>).
/// Concurrent runs share one copy: a run decides whether the copy is current, and replaces it, only while it holds an
/// exclusive <c>&lt;folder&gt;.lock</c> beside it, so a copy another run just moved in is used, never set aside, and a current
/// copy is never replaced. valheim-test compiles this same file in (its project links it), so it adds no public type.
/// </summary>
internal static class ExtractOnce
{
    private static readonly TimeSpan SwapWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Makes <paramref name="target"/> a copy <paramref name="current"/> accepts. False: it already was one (checked without
    /// the lock, then again under it). <paramref name="current"/> answers false for a missing or changed copy and lets any
    /// other IO error through: under the lock that error fails the run, so a copy it could not read is never replaced. True: this run extracted it: <paramref name="extract"/> fills a fresh staging folder
    /// beside it (and throws when what it wrote is not a current copy), a damaged copy goes aside, the new one moves in, and
    /// if it cannot, the damaged one goes back, so the folder is never left missing. <paramref name="what"/> names the
    /// content in errors ("ValheimCLI bundle").
    /// </summary>
    internal static bool Ensure(string target, Func<string, bool> current, Action<string> extract, string what)
    {
        // Read without the lock: a copy another run is swapping in or out reads as not current here, never as an error.
        if (Settled(() => current(target))) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? old = null;
        using (SwapLock(target + ".lock", what))
        {
            // Strict under the lock: an unreadable copy is an error, never a reason to replace it.
            if (current(target)) return false;
            string staging = target + ".extract-" + Guid.NewGuid().ToString("N");
            try
            {
                extract(staging);
                // A damaged copy goes aside first (a rename; on Windows it waits while a run outside the lock still reads a
                // file of it), then the new one moves in. If the new one cannot, the damaged one goes back: never no copy.
                if (Directory.Exists(target))
                {
                    old = target + ".old-" + Guid.NewGuid().ToString("N");
                    Retry(() => Directory.Move(target, old), $"set the damaged {what} at {target} aside");
                }
                try { Retry(() => Directory.Move(staging, target), $"move the extracted {what} into {target}"); }
                catch when (old != null)
                {
                    // Retried like the moves above; if even that fails, the move-in's error is the one reported.
                    try { Retry(() => Directory.Move(old, target), $"put the damaged {what} back at {target}"); old = null; } catch (IOException) { }
                    throw;
                }
            }
            finally
            {
                // Best effort: a failed clean-up never hides why the extraction failed.
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        // The damaged copy is out of the way: removed after the lock, best effort.
        if (old != null) try { Directory.Delete(old, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return true;
    }

    // An exclusive handle on the lock file (FileShare.None: a sharing lock on Windows, flock on Linux and macOS), retried
    // until another run's extraction ends. The file stays; only the handle is the lock.
    private static FileStream SwapLock(string path, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (clock.Elapsed < SwapWait) { Thread.Sleep(20); }
            catch (IOException error) { throw new IOException($"Another run has held {path} for {SwapWait.TotalMinutes:0} minutes while extracting the {what}; the lock ends with that process.", error); }
        }
    }

    // A folder rename that a reader outside the lock briefly blocks on Windows (a file of it open for hashing), for up to ~10 s.
    private static void Retry(Action action, string what)
    {
        for (int attempt = 1; ; attempt++)
            try { action(); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 50) throw new IOException($"Could not {what}: {error.Message}", error);
                Thread.Sleep(20 * Math.Min(attempt, 10));
            }
    }

    // A check outside the lock: files that vanish or are locked mid-swap mean "not current yet", not a failure.
    private static bool Settled(Func<bool> check)
    {
        try { return check(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
