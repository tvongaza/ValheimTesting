using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>
/// Free space and directory sizes for the copies a native run makes. A run that copies a game install needs room for the
/// whole copy; refusing before the copy starts says which drive and how much, where a full drive part-way through a copy
/// leaves a half-made runtime behind and a confusing I/O error.
/// </summary>
public static class DiskSpace
{
    /// <summary>Room kept free beyond the estimate: 2 GiB, or a tenth of the estimate when that is larger.</summary>
    public static long Headroom(long bytes) => Math.Max(2L << 30, bytes / 10);

    // Test seam: the free bytes for a path, in place of the drive's. Async-local, so one test's fake drive never reaches
    // another test running in parallel.
    private static readonly AsyncLocal<Func<string, long>?> s_availableOverride = new();
    internal static Func<string, long>? AvailableOverride { get => s_availableOverride.Value; set => s_availableOverride.Value = value; }

    /// <summary>
    /// The free bytes on the drive that holds <paramref name="path"/> (or would hold it: the nearest existing parent
    /// decides), or null when the system cannot say, as for a Windows network share.
    /// </summary>
    public static long? Available(string path)
    {
        string? existing = Path.GetFullPath(path);
        while (existing != null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing == null) return null;
        if (AvailableOverride is { } fake) return fake(existing);
        try { return new DriveInfo(existing).AvailableFreeSpace; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The bytes of every file under <paramref name="directory"/>, or 0 when it does not exist. Links are not followed.</summary>
    public static long DirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return new DirectoryInfo(directory).EnumerateFiles("*", options).Sum(file => file.Length);
    }

    /// <summary>
    /// Refuses, before anything is copied, unless the drive that will hold <paramref name="target"/> has
    /// <paramref name="bytes"/> free plus <see cref="Headroom"/>. The message names the drive, both amounts and what needed them.
    /// Returns the free bytes it saw, or null when the drive's free space is unknown (then nothing is refused).
    /// </summary>
    public static long? Require(string target, long bytes, string purpose)
    {
        if (Available(target) is not { } free) return null;
        long needed = bytes + Headroom(bytes);
        if (free < needed)
            throw new IOException($"Not enough free disk space for {purpose}: it needs about {Format(needed)} ({Format(bytes)} plus {Format(Headroom(bytes))} headroom) " +
                $"on the drive holding {Path.GetFullPath(target)}, which has {Format(free)} free. Remove old run outputs (their game copies, not their result.json and logs) or choose an output on another drive.");
        return free;
    }

    /// <summary>Bytes as a short human figure: "812 MB", "2.3 GB".</summary>
    public static string Format(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";
}
