using System.Text.Json;
using System.Text;

namespace Valheim.Testing.Game;

/// <summary>A screenshot requested by an owned local client, written directly into its private run evidence.</summary>
internal static class OwnedScreenshot
{
    internal sealed record Request(string Command, string Image, string Intent, string Result, string RelativeImage);
    internal sealed record Outcome(bool Accepted, bool Complete, string Status, string? Sha256,
        string RelativeImage, string ResultFile);

    internal static Request Prepare(string evidence, string requestedCommand, int supersize = 1,
        Func<string>? nextId = null)
    {
        if (supersize is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(supersize));
        string directory = Path.Combine(Path.GetFullPath(evidence), "screenshots");
        Directory.CreateDirectory(directory);
        nextId ??= () => Guid.NewGuid().ToString("N");
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string id = nextId();
            if (id.Length != 32 || !id.All(Uri.IsHexDigit))
                throw new InvalidDataException("The screenshot ID must be 32 hexadecimal characters.");
            string image = Path.Combine(directory, "owned-" + id + ".png");
            string intent = image + ".intent.json";
            if (File.Exists(image) || File.Exists(intent)) continue;
            string path64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(image))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string command = "cli_screenshot_path64 " + path64 + (supersize == 1 ? "" : " " + supersize);
            try
            {
                using var claim = new FileStream(intent, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(claim, new { requestedCommand, command, image, utc = DateTimeOffset.UtcNow });
            }
            catch (IOException) when (File.Exists(intent)) { continue; }
            if (File.Exists(image))
                throw new IOException("A screenshot destination appeared after its claim; no command was sent: " + image);
            return new Request(command, image, intent, image + ".result.json",
                Path.GetRelativePath(evidence, image).Replace('\\', '/'));
        }
        throw new IOException("Could not claim a fresh private screenshot destination; no command was sent.");
    }

    /// <summary>
    /// The longest Capture waits for a file event before looking at the image again. Events end the wait early; this only
    /// covers a completed image whose last event came while its writer still had it open.
    /// </summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Sends the request and waits for the complete image. <paramref name="aboutToWait"/> (tests only) runs after each check that
    /// found no complete image, right before the wait for the next event or <paramref name="recheck"/>.
    /// </summary>
    internal static Outcome Capture(GameActor actor, string evidence, string requestedCommand, int supersize,
        TimeSpan writeTimeout, CancellationToken cancellation = default, Func<string>? nextId = null, Action? aboutToWait = null,
        TimeSpan? recheck = null)
    {
        TimeSpan recheckEvery = recheck ?? RecheckInterval;
        if (recheckEvery <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(recheck), "The re-check interval must be positive.");
        var request = Prepare(evidence, requestedCommand, supersize, nextId);
        using var signal = new AutoResetEvent(false);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(request.Image)!, Path.GetFileName(request.Image));
        FileSystemEventHandler changed = (_, _) => signal.Set();
        RenamedEventHandler renamed = (_, _) => signal.Set();
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Renamed += renamed;
        watcher.EnableRaisingEvents = true;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var reply = actor.Execute(request.Command, requireAccepted: false);
            string? queued = reply.Line("OK: screenshot queued path=");
            string? named = queued?["OK: screenshot queued path=".Length..]
                .Split(" size=", 2, StringSplitOptions.None)[0].Trim('"');
            if (!reply.Accepted || named == null)
                return Write(request, reply.Accepted, false, reply.Refusal ?? reply.ErrorCode ??
                    reply.Message ?? "ValheimCLI did not queue a screenshot", null);
            if (!string.Equals(Path.GetFullPath(named), request.Image,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Write(request, true, false, "ValheimCLI named a different screenshot destination; no external file was collected", null);
            var clock = System.Diagnostics.Stopwatch.StartNew(); // Monotonic: a wall-clock step must not expire the wait.
            string? hash;
            while ((hash = CompletePngHash(request.Image)) == null)
            {
                cancellation.ThrowIfCancellationRequested();
                TimeSpan left = writeTimeout - clock.Elapsed;
                if (left <= TimeSpan.Zero)
                    return Write(request, true, false, "Screenshot queued but a complete PNG did not appear before the deadline", null);
                aboutToWait?.Invoke(); // Tests order a write after this check; the event it raises is kept by the signal.
                // A file event is only a hint that the image may be complete: a writer that still holds the file when its
                // last event arrives (the check above is refused) closes it without raising another. So each wait is bounded.
                int fired = WaitHandle.WaitAny([signal, cancellation.WaitHandle], left < recheckEvery ? left : recheckEvery);
                if (fired == 1) cancellation.ThrowIfCancellationRequested();
            }
            return Write(request, true, true, "captured", hash);
        }
        catch (OperationCanceledException)
        {
            TryWriteFailure(request, "interrupted; any later image remains inside private evidence");
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            TryWriteFailure(request, "screenshot attempt failed: " + error.Message);
            throw;
        }
    }

    private static void TryWriteFailure(Request request, string status)
    {
        // Keep the original failure even if writing its sidecar also fails.
        try { if (!File.Exists(request.Result)) Write(request, false, false, status, null); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Outcome Write(Request request, bool accepted, bool complete, string status, string? hash)
    {
        using (var result = new FileStream(request.Result, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(result, new
            { accepted, complete, status, image = request.RelativeImage, sha256 = hash, utc = DateTimeOffset.UtcNow });
        return new Outcome(accepted, complete, status, hash, request.RelativeImage, request.Result);
    }

    private static string? CompletePngHash(string path)
    {
        try
        {
            // FileShare.Read refuses a writer that still has the PNG open. The same handle supplies
            // the completeness check and hash, so evidence never hashes a different set of bytes.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 20 or > 100_000_000) return null;
            Span<byte> header = stackalloc byte[8];
            Span<byte> trailer = stackalloc byte[12];
            file.ReadExactly(header);
            file.Seek(-12, SeekOrigin.End);
            file.ReadExactly(trailer);
            if (!header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !trailer[..8].SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68 })) return null;
            file.Seek(0, SeekOrigin.Begin);
            return FileHash.Sha256(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
