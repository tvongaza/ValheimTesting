using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// One human-review screenshot at a declared site. The host directory must be a fresh, private directory belonging to
/// this run; the local evidence directory must not exist. The review adapter provides the paired
/// <c>review-begin</c>/<c>review-restore</c> commands. Images are evidence for a person, never an automated pass.
/// </summary>
public sealed record ReviewCapturePlan(
    string Id, HeightExpectation Arrival, string Weather, float TimeOfDay, float CameraDistance, float CameraHeight,
    string ExtensionId, string HostDirectory, string EvidenceDirectory, string WorldUid, string GameBuild,
    IReadOnlyDictionary<string, string> PluginPins, bool MistOff = true, bool ClutterOff = false, int Supersize = 1,
    float CameraAzimuthDegrees = 225);

/// <summary>The fetched PNG, its metadata and digest. No visual verdict is implied.</summary>
public sealed record ReviewCaptureReceipt(string ImagePath, string MetadataPath, string Sha256, long Bytes);

/// <summary>
/// Makes a reproducible still view with a protected, arrived player. It restores the client's exact starting safety,
/// weather, debug time, mist and camera mode on success or failure. A failed restore fails the capture even if a complete
/// PNG was written. The adapter also restores on unload when the client is still alive.
/// </summary>
public static class ReviewCapture
{
    /// <summary>Capture from a joined client, fetching only this run's image directory from its owned host.</summary>
    public static Task<ReviewCaptureReceipt> CaptureAsync(GameActor server, GameActor client, IGameHost clientHost,
        ReviewCapturePlan plan, TimeSpan arrivalTimeout, TimeSpan fetchTimeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clientHost);
        return CaptureCoreAsync(server, client, plan, clientHost.Shell.Kind,
            (host, local, token) => clientHost.FetchDirectoryAsync(host, local, fetchTimeout, token),
            () => { PlayerPlacement.Protect(client); PlayerPlacement.Arrive(server, client, plan.Arrival, arrivalTimeout, cancellation); },
            cancellation);
    }

    internal static async Task<ReviewCaptureReceipt> CaptureCoreAsync(GameActor server, GameActor client, ReviewCapturePlan plan,
        HostShellKind shell, Func<string, string, CancellationToken, Task<FetchedDirectory>> fetch, Action arrive,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(arrive);
        Validate(plan, shell);
        if (!server.Pinned || !client.Pinned) throw new InvalidOperationException("A review capture requires strict server and client pins.");
        if (Directory.Exists(plan.EvidenceDirectory) || File.Exists(plan.EvidenceDirectory))
            throw new IOException("Review evidence already exists: " + plan.EvidenceDirectory);
        var begin = client.RequireCapability(plan.ExtensionId + "/review-begin");
        var restore = client.RequireCapability(plan.ExtensionId + "/review-restore");
        var mistOff = plan.MistOff ? client.RequireCapability(plan.ExtensionId + "/review-mist-off") : null;
        var clutterOff = plan.ClutterOff ? client.RequireCapability(plan.ExtensionId + "/review-clutter-off") : null;
        if (!begin.ReadOnly || restore.ReadOnly) throw new InvalidOperationException("The review adapter's begin/restore capabilities have the wrong access modes.");
        if (mistOff?.ReadOnly == true || clutterOff?.ReadOnly == true) throw new InvalidOperationException("The review adapter's visual commands must be mutations.");
        string staging = plan.EvidenceDirectory + ".partial-" + Guid.NewGuid().ToString("N");
        string hostImage = plan.HostDirectory.TrimEnd('\\', '/') + (shell == HostShellKind.PowerShell ? "\\" : "/") + plan.Id + ".png";
        Exception? failure = null;
        ReviewCaptureReceipt? receipt = null;
        JsonElement original = default;
        bool beginAttempted = false;
        try
        {
            beginAttempted = true; // A lost reply may still have stored the snapshot in the game.
            original = client.Invoke(begin, plan.Id);
            RequireState(original, plan.Id, "begun");
            cancellation.ThrowIfCancellationRequested();
            arrive();
            string tod = plan.TimeOfDay.ToString("R", CultureInfo.InvariantCulture);
            Require(client.Execute($"cli_env {tod} {plan.Weather}"), "OK: ENV ", "environment did not settle");
            if (mistOff != null) RequireState(client.Invoke(mistOff, plan.Id), plan.Id, "mist-off");
            if (clutterOff != null) RequireState(client.Invoke(clutterOff, plan.Id), plan.Id, "clutter-off");
            double radians = plan.CameraAzimuthDegrees * Math.PI / 180;
            double cameraX = plan.Arrival.X + plan.CameraDistance * Math.Sin(radians);
            double cameraZ = plan.Arrival.Z + plan.CameraDistance * Math.Cos(radians);
            string F(double number) => number.ToString("R", CultureInfo.InvariantCulture);
            string command = string.Join(" ", new[] { "cli_capture", hostImage,
                F(cameraX), F(plan.Arrival.Height + plan.CameraHeight), F(cameraZ),
                F(plan.Arrival.X), F(plan.Arrival.Height + 1), F(plan.Arrival.Z), plan.Supersize.ToString(CultureInfo.InvariantCulture), "45" });
            var previousTimeout = client.CommandTimeout;
            string capture;
            try
            {
                client.CommandTimeout = TimeSpan.FromSeconds(60);
                capture = Require(client.Execute(command), "OK: CAPTURE ", "capture did not complete");
            }
            finally { client.CommandTimeout = previousTimeout; }
            if (!capture.Contains("path=" + hostImage + " ", StringComparison.Ordinal))
                throw new InvalidDataException("The game saved a different image path: " + capture);
            var copied = await fetch(plan.HostDirectory, staging, cancellation).ConfigureAwait(false);
            if (copied.Files != 1) throw new InvalidDataException($"Expected one private image, fetched {copied.Files} files.");
            string[] files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
            if (files.Length != 1 || Path.GetFileName(files[0]) != plan.Id + ".png")
                throw new InvalidDataException("The fetched capture has an unexpected filename.");
            byte[] png = await File.ReadAllBytesAsync(files[0], cancellation).ConfigureAwait(false);
            if (!CompletePng(png)) throw new InvalidDataException("The fetched image is not a complete PNG.");
            string sha = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var metadata = new
            {
                schema = 1, kind = "human-review-still", visualVerdict = "not asserted", plan.Id,
                worldUid = plan.WorldUid, gameBuild = plan.GameBuild, pluginPins = plan.PluginPins,
                location = new { plan.Arrival.X, y = plan.Arrival.Height, plan.Arrival.Z },
                conditions = new { plan.Weather, plan.TimeOfDay, plan.MistOff, plan.ClutterOff, plan.CameraDistance, plan.CameraHeight, plan.CameraAzimuthDegrees, plan.Supersize },
                initialState = original, captureReply = capture, imageSha256 = sha, imageBytes = png.LongLength,
                recordedUtc = DateTimeOffset.UtcNow,
            };
            string metadataPath = Path.Combine(staging, plan.Id + ".json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), cancellation).ConfigureAwait(false);
            receipt = new ReviewCaptureReceipt(Path.Combine(plan.EvidenceDirectory, plan.Id + ".png"),
                Path.Combine(plan.EvidenceDirectory, plan.Id + ".json"), sha, png.LongLength);
        }
        catch (Exception error) { failure = error; }
        try
        {
            if (beginAttempted)
            {
                // Restoration is cleanup, so a caller's cancellation never suppresses it. Never resend the capture.
                var restored = client.Invoke(restore, plan.Id);
                RequireState(restored, plan.Id, "restored");
            }
        }
        catch (Exception cleanup)
        {
            failure = failure == null ? cleanup : new AggregateException("Review capture and state restoration both failed.", failure, cleanup);
        }
        if (failure != null)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        Directory.Move(staging, plan.EvidenceDirectory);
        return receipt!;
    }

    private static void Validate(ReviewCapturePlan plan, HostShellKind shell)
    {
        if (!Regex.IsMatch(plan.Id ?? "", "^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Capture id must be 1-64 letters, digits or hyphens.");
        if (!Regex.IsMatch(plan.ExtensionId ?? "", "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)) throw new ArgumentException("Name the adapter extension id.");
        if (!Regex.IsMatch(plan.Weather ?? "", "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)) throw new ArgumentException("Weather must be one named environment.");
        if (!float.IsFinite(plan.TimeOfDay) || plan.TimeOfDay is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(plan.TimeOfDay));
        if (!float.IsFinite(plan.CameraDistance) || plan.CameraDistance is < 2 or > 100 || !float.IsFinite(plan.CameraHeight) || plan.CameraHeight is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(plan.CameraDistance), "Camera distance and height must be bounded and positive.");
        if (!float.IsFinite(plan.CameraAzimuthDegrees) || plan.CameraAzimuthDegrees is < 0 or >= 360)
            throw new ArgumentOutOfRangeException(nameof(plan.CameraAzimuthDegrees), "Camera azimuth must be from 0 up to 360 degrees.");
        if (plan.Supersize is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(plan.Supersize));
        if (string.IsNullOrWhiteSpace(plan.HostDirectory) || plan.HostDirectory.Any(char.IsWhiteSpace) || !Path.IsPathFullyQualified(plan.EvidenceDirectory) ||
            string.IsNullOrWhiteSpace(plan.WorldUid) || string.IsNullOrWhiteSpace(plan.GameBuild) || plan.PluginPins.Count == 0)
            throw new ArgumentException("Use a private no-space host directory, an absolute new evidence directory and complete provenance.");
        string host = plan.HostDirectory.Replace('\\', '/');
        bool absolute = shell == HostShellKind.PowerShell ? Regex.IsMatch(host, "^[A-Za-z]:/[^/].*") : host.StartsWith("/", StringComparison.Ordinal);
        if (!absolute || host.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("The host image directory must be absolute and contain no traversal segments.");
    }

    private static void RequireState(JsonElement data, string id, string state)
    {
        if (data.GetProperty("source").GetString() != "review-state" || data.GetProperty("id").GetString() != id ||
            data.GetProperty("state").GetString() != state || !data.GetProperty("complete").GetBoolean())
            throw new InvalidDataException("The review adapter did not confirm " + state + " for " + id + ".");
    }

    private static string Require(valheim_cli.Testing.CommandResult reply, string prefix, string failure) =>
        reply.Output.SingleOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(failure + ": " + string.Join(" | ", reply.Output));

    private static bool CompletePng(byte[] bytes) => bytes.Length >= 20 &&
        bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        bytes.AsSpan(bytes.Length - 8, 8).SequenceEqual(new byte[] { 73, 69, 78, 68, 174, 66, 96, 130 });
}
