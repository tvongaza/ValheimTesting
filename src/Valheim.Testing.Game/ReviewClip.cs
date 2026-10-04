using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>A short, opt-in world-only motion sample. Output is a directory of hashed PNG frames for human inspection, never a verdict.</summary>
public sealed record ReviewClipPlan(string Id, string ExtensionId, string HostDirectory, string EvidenceDirectory,
    string WorldUid, string GameBuild, IReadOnlyDictionary<string, string> PluginPins,
    int Width = 320, int Height = 180, int FramesPerSecond = 5, int Frames = 15);

/// <summary>
/// One local frame directory with provenance: <c>frame-NNN.png</c>, the game's <c>frames.csv</c> (index, elapsed
/// milliseconds, bytes and SHA-256 of every frame) and the JSON sidecar. <see cref="ManifestSha256"/> is the SHA-256 of
/// <c>frames.csv</c>, which in turn lists every frame's digest. Visual correctness remains for a person to judge.
/// </summary>
public sealed record ReviewClipReceipt(string FramesDirectory, string MetadataPath, string ManifestSha256, long Bytes, int Frames, int DurationMs);

/// <summary>Capture only after an owned, pinned client has entered its intended world; remove partial local evidence on any failure.</summary>
public static class ReviewClip
{
    /// <summary>
    /// Capture and fetch one clip. The frame command is an ordinary bounded command; <paramref name="cancellation"/> is
    /// checked between steps and stops the transfer, and a canceled capture is restored and never published.
    /// </summary>
    public static ReviewClipReceipt Capture(GameActor client, IGameHost clientHost, ReviewClipPlan plan,
        TimeSpan fetchTimeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(clientHost);
        return CaptureCore(client, plan, (host, local, token) => clientHost.FetchDirectoryAsync(host, local, fetchTimeout, token), cancellation);
    }

    internal static ReviewClipReceipt CaptureCore(GameActor client, ReviewClipPlan plan,
        Func<string, string, CancellationToken, Task<FetchedDirectory>> fetch, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fetch);
        Validate(plan);
        if (!client.Pinned) throw new InvalidOperationException("A review clip requires strict client pins.");
        if (Directory.Exists(plan.EvidenceDirectory) || File.Exists(plan.EvidenceDirectory))
            throw new IOException("Review evidence already exists: " + plan.EvidenceDirectory);
        var state = new SessionControl(client).Read();
        if (!state.WorldReady || !state.PlayerReady || state.Dedicated || state.WorldUid != plan.WorldUid)
            throw new InvalidOperationException("The pinned client has not entered the declared review world with a ready player.");
        var begin = client.RequireCapability(plan.ExtensionId + "/review-begin");
        var clip = client.RequireCapability(plan.ExtensionId + "/review-clip-frames");
        var restore = client.RequireCapability(plan.ExtensionId + "/review-restore");
        if (!begin.ReadOnly || clip.ReadOnly || restore.ReadOnly)
            throw new InvalidOperationException("The review adapter's capture access modes are wrong.");
        string staging = plan.EvidenceDirectory + ".partial-" + Guid.NewGuid().ToString("N");
        string hostFrames = plan.HostDirectory.TrimEnd('\\', '/') + (clientHostSeparator(plan.HostDirectory)) + plan.Id + "-frames";
        Exception? failure = null;
        ReviewClipReceipt? receipt = null;
        bool beginAttempted = false;
        try
        {
            beginAttempted = true;
            RequireState(client.Invoke(begin, plan.Id), plan.Id, "begun");
            cancellation.ThrowIfCancellationRequested();
            TimeSpan previousTimeout = client.CommandTimeout;
            JsonElement result;
            try
            {
                client.CommandTimeout = TimeSpan.FromSeconds(Math.Max(30, plan.Frames / (double)plan.FramesPerSecond + 20));
                result = client.Invoke(clip, plan.Id, hostFrames,
                    plan.Width.ToString(CultureInfo.InvariantCulture), plan.Height.ToString(CultureInfo.InvariantCulture),
                    plan.FramesPerSecond.ToString(CultureInfo.InvariantCulture), plan.Frames.ToString(CultureInfo.InvariantCulture));
            }
            finally { client.CommandTimeout = previousTimeout; }
            if (result.GetProperty("source").GetString() != "scene-only-frames" ||
                !result.GetProperty("complete").GetBoolean() || result.GetProperty("id").GetString() != plan.Id ||
                result.GetProperty("directory").GetString() != hostFrames ||
                result.GetProperty("width").GetInt32() != plan.Width || result.GetProperty("height").GetInt32() != plan.Height ||
                result.GetProperty("frames").GetInt32() != plan.Frames)
                throw new InvalidDataException("The client did not confirm the requested bounded world-frame capture.");
            cancellation.ThrowIfCancellationRequested();
            // The fetch is the one asynchronous step; wait for it off any caller's synchronization context.
            var copied = Task.Run(() => fetch(hostFrames, staging, cancellation), CancellationToken.None).GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            if (copied.Files != plan.Frames + 1) throw new InvalidDataException("The frame transfer is incomplete.");
            var frames = ReadFrames(staging, plan);
            string manifestSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(staging, "frames.csv")))).ToLowerInvariant();
            long bytes = frames.Sum(f => f.Bytes);
            int duration = frames[^1].ElapsedMs - frames[0].ElapsedMs;
            var metadata = new
            {
                schema = 1, kind = "human-review-world-clip", visualVerdict = "not asserted", plan.Id,
                worldUid = plan.WorldUid, gameBuild = plan.GameBuild, pluginPins = plan.PluginPins,
                plan.Width, plan.Height, requestedFps = plan.FramesPerSecond, frameCount = plan.Frames,
                durationMs = duration, actualFps = Math.Round((plan.Frames - 1) * 1000d / duration, 2),
                frameSha256 = frames.Select(f => f.Sha256).ToArray(), manifestSha256 = manifestSha, frameBytes = bytes,
                recordedUtc = DateTimeOffset.UtcNow,
            };
            File.WriteAllText(Path.Combine(staging, plan.Id + ".json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            receipt = new ReviewClipReceipt(plan.EvidenceDirectory, Path.Combine(plan.EvidenceDirectory, plan.Id + ".json"),
                manifestSha, bytes, plan.Frames, duration);
        }
        catch (Exception error) { failure = error; }
        try
        {
            if (beginAttempted) RequireState(client.Invoke(restore, plan.Id), plan.Id, "restored");
        }
        catch (Exception cleanup)
        {
            failure = failure == null ? cleanup : new AggregateException("Clip capture and review-state restoration both failed.", failure, cleanup);
        }
        if (failure == null && cancellation.IsCancellationRequested)
            failure = new OperationCanceledException("Clip capture was canceled before publication.", cancellation);
        if (failure != null)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        try { Directory.Move(staging, plan.EvidenceDirectory); }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
        return receipt!;
    }

    private static char clientHostSeparator(string path) => Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant) ? '\\' : '/';

    private sealed record Frame(string Path, int ElapsedMs, string Sha256, long Bytes);

    /// <summary>
    /// Checks the fetched directory against the game's manifest: exactly the declared frames and <c>frames.csv</c>, rising
    /// timestamps, and each frame's length and SHA-256 as the game recorded them. Size and duration ceilings are the
    /// plan's and the adapter's rules (<see cref="Validate"/>, <c>ReviewClipFrames</c>), not repeated here.
    /// </summary>
    private static List<Frame> ReadFrames(string directory, ReviewClipPlan plan)
    {
        string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        if (files.Length != plan.Frames + 1 || !File.Exists(Path.Combine(directory, "frames.csv")))
            throw new InvalidDataException("The capture directory has missing or extra files.");
        string[] rows = File.ReadAllLines(Path.Combine(directory, "frames.csv"));
        if (rows.Length != plan.Frames + 1 || rows[0] != "frame,elapsed_ms,bytes,sha256")
            throw new InvalidDataException("The clip timing manifest is incomplete.");
        var frames = new List<Frame>();
        int previous = -1;
        for (int index = 0; index < plan.Frames; index++)
        {
            string[] cells = rows[index + 1].Split(',');
            if (cells.Length != 4 || !int.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out int rowIndex) || rowIndex != index ||
                !int.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out int elapsed) || elapsed <= previous ||
                !long.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) || bytes < 1 ||
                !Regex.IsMatch(cells[3], "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("A captured frame timing or digest is invalid.");
            string path = Path.Combine(directory, "frame-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".png");
            if (!File.Exists(path)) throw new InvalidDataException("A captured frame is missing.");
            using var stream = File.OpenRead(path);
            if (stream.Length != bytes || Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != cells[3])
                throw new InvalidDataException("A captured frame differs from its game-side digest.");
            frames.Add(new Frame(path, elapsed, cells[3], bytes));
            previous = elapsed;
        }
        return frames;
    }

    private static void RequireState(JsonElement data, string id, string state)
    {
        if (data.GetProperty("source").GetString() != "review-state" || data.GetProperty("id").GetString() != id ||
            data.GetProperty("state").GetString() != state || !data.GetProperty("complete").GetBoolean())
            throw new InvalidDataException("The review adapter did not confirm " + state + " for " + id + ".");
    }

    private static void Validate(ReviewClipPlan plan)
    {
        if (!Regex.IsMatch(plan.Id ?? "", "^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(plan.ExtensionId ?? "", "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ||
            plan.Width is < 160 or > 640 || plan.Height is < 90 or > 360 ||
            plan.FramesPerSecond is < 2 or > 10 || plan.Frames is < 2 or > 60 || plan.Frames > plan.FramesPerSecond * 10 ||
            !Path.IsPathFullyQualified(plan.EvidenceDirectory) || plan.PluginPins == null || plan.PluginPins.Count == 0 ||
            string.IsNullOrWhiteSpace(plan.GameBuild) || !long.TryParse(plan.WorldUid, out _) ||
            string.IsNullOrWhiteSpace(plan.HostDirectory) || plan.HostDirectory.Any(char.IsWhiteSpace) ||
            !Regex.IsMatch(plan.HostDirectory.Replace('\\', '/'), "^([A-Za-z]:/|/)", RegexOptions.CultureInvariant) ||
            plan.HostDirectory.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Provide bounded frames, a pinned world and an absolute private capture directory.");
    }
}
