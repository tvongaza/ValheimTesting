using System.Globalization;
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

/// <summary>
/// The fetched PNG, its metadata and digest. No visual verdict is implied. <see cref="Evidence"/> links the metadata
/// sidecar, which records the image's SHA-256 with the world, build, pins and conditions, for
/// <see cref="ScenarioReport.Attach(EvidenceReference)"/> (kind <c>review-still</c>, site = the capture id).
/// </summary>
[ResultShape]
public sealed record ReviewCaptureReceipt(string ImagePath, string MetadataPath, string Sha256, long Bytes)
{
    public required EvidenceReference Evidence { get; init; }
}

/// <summary>
/// Makes a reproducible still view with a protected, arrived player. It restores the client's exact starting safety,
/// weather, debug time, mist and camera mode on success or failure. A failed restore fails the capture even if a complete
/// PNG was written. The adapter also restores on unload when the client is still alive.
/// </summary>
public static class ReviewCapture
{
    /// <summary>
    /// Capture from a joined client, fetching only this run's image directory from its owned host. Synchronous like the
    /// arrival and commands it issues, so it runs directly inside a <c>ClientRound.Step</c>; it waits for the host fetch.
    /// </summary>
    public static ReviewCaptureReceipt Capture(GameActor server, GameActor client, IGameHost clientHost,
        ReviewCapturePlan plan, TimeSpan arrivalTimeout, TimeSpan fetchTimeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clientHost);
        // Refused before review-begin touches the client: PlayerPlacement.Arrive's deadline is at most 10 minutes.
        if (arrivalTimeout <= TimeSpan.Zero || arrivalTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(arrivalTimeout));
        return CaptureCore(server, client, plan, clientHost.Shell.Kind,
            (host, local, token) => clientHost.FetchDirectoryAsync(host, local, fetchTimeout, token),
            () => { PlayerPlacement.Protect(client); PlayerPlacement.Arrive(server, client, plan.Arrival, arrivalTimeout, cancellation); },
            cancellation);
    }

    internal static ReviewCaptureReceipt CaptureCore(GameActor server, GameActor client, ReviewCapturePlan plan,
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
        string staging = ReviewLease.Stage(plan.EvidenceDirectory);
        var begin = client.RequireCapability(plan.ExtensionId + "/review-begin");
        var restore = client.RequireCapability(plan.ExtensionId + "/review-restore");
        var mistOff = plan.MistOff ? client.RequireCapability(plan.ExtensionId + "/review-mist-off") : null;
        var clutterOff = plan.ClutterOff ? client.RequireCapability(plan.ExtensionId + "/review-clutter-off") : null;
        if (!begin.ReadOnly || restore.ReadOnly) throw new InvalidOperationException("The review adapter's begin/restore capabilities have the wrong access modes.");
        if (mistOff?.ReadOnly == true || clutterOff?.ReadOnly == true) throw new InvalidOperationException("The review adapter's visual commands must be mutations.");
        string hostImage = plan.HostDirectory.TrimEnd('\\', '/') + (shell == HostShellKind.PowerShell ? "\\" : "/") + plan.Id + ".png";
        Exception? failure = null;
        ReviewCaptureReceipt? receipt = null;
        JsonElement original = default;
        bool beginAttempted = false;
        try
        {
            beginAttempted = true; // A lost reply may still have stored the snapshot in the game.
            original = client.Invoke(begin, plan.Id);
            ReviewLease.RequireState(original, plan.Id, "begun");
            cancellation.ThrowIfCancellationRequested();
            arrive();
            string tod = plan.TimeOfDay.ToString("R", CultureInfo.InvariantCulture);
            client.Execute($"cli_env {tod} {plan.Weather}").RequireLine("OK: ENV ", "environment did not settle");
            if (mistOff != null) ReviewLease.RequireState(client.Invoke(mistOff, plan.Id), plan.Id, "mist-off");
            if (clutterOff != null) ReviewLease.RequireState(client.Invoke(clutterOff, plan.Id), plan.Id, "clutter-off");
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
                capture = client.Execute(command).RequireLine("OK: CAPTURE ", "capture did not complete");
            }
            finally { client.CommandTimeout = previousTimeout; }
            if (!capture.Contains("path=" + hostImage + " ", StringComparison.Ordinal))
                throw new InvalidDataException("The game saved a different image path: " + capture);
            // The fetch is the one asynchronous step; wait for it off any caller's synchronization context.
            var copied = Task.Run(() => fetch(plan.HostDirectory, staging, cancellation), CancellationToken.None).GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            if (copied.Files != 1) throw new InvalidDataException($"Expected one private image, fetched {copied.Files} files.");
            string[] files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
            if (files.Length != 1 || Path.GetFileName(files[0]) != plan.Id + ".png")
                throw new InvalidDataException("The fetched capture has an unexpected filename.");
            byte[] png = File.ReadAllBytes(files[0]);
            if (!CompletePng(png)) throw new InvalidDataException("The fetched image is not a complete PNG.");
            string sha = FileHash.Sha256(png);
            string sidecarSha = ReviewLease.WriteSidecar(Path.Combine(staging, plan.Id + ".json"), "human-review-still", plan.Id, plan.WorldUid, plan.GameBuild, plan.PluginPins, new
            {
                location = new { plan.Arrival.X, y = plan.Arrival.Height, plan.Arrival.Z },
                conditions = new { plan.Weather, plan.TimeOfDay, plan.MistOff, plan.ClutterOff, plan.CameraDistance, plan.CameraHeight, plan.CameraAzimuthDegrees, plan.Supersize },
                initialState = original, captureReply = capture, imageSha256 = sha, imageBytes = png.LongLength,
            });
            string image = Path.Combine(plan.EvidenceDirectory, plan.Id + ".png");
            string metadata = Path.Combine(plan.EvidenceDirectory, plan.Id + ".json");
            receipt = new ReviewCaptureReceipt(image, metadata, sha, png.LongLength)
            { Evidence = new("review-still", plan.Id, plan.WorldUid, metadata, sidecarSha) };
        }
        catch (Exception error) { failure = error; }
        try
        {
            if (beginAttempted)
            {
                // Restoration is cleanup, so a caller's cancellation never suppresses it. Never resend the capture.
                ReviewLease.RequireState(client.Invoke(restore, plan.Id), plan.Id, "restored");
            }
        }
        catch (Exception cleanup)
        {
            failure = failure == null ? cleanup : new AggregateException("Review capture and state restoration both failed.", failure, cleanup);
        }
        if (failure == null && cancellation.IsCancellationRequested)
            failure = new OperationCanceledException("Review capture was canceled before publication.", cancellation);
        ReviewLease.Publish(failure, staging, plan.EvidenceDirectory);
        return receipt!;
    }

    /// <summary>
    /// The one rule for a capture plan: id, extension, weather, time, camera distance, height, azimuth, supersize, host and
    /// evidence directories and provenance. <see cref="Capture"/> applies it first; call it to refuse a plan before launch.
    /// </summary>
    public static void Validate(ReviewCapturePlan plan, HostShellKind shell)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReviewLease.ValidId(plan.Id)) throw new ArgumentException("Capture id must be 1-64 letters, digits or hyphens.");
        if (!Regex.IsMatch(plan.ExtensionId ?? "", "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)) throw new ArgumentException("Name the adapter extension id.");
        if (!Regex.IsMatch(plan.Weather ?? "", "^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Weather must be one named environment of at most 64 letters, digits or underscores.");
        if (!float.IsFinite(plan.TimeOfDay) || plan.TimeOfDay is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(plan.TimeOfDay));
        if (!float.IsFinite(plan.CameraDistance) || plan.CameraDistance is < 2 or > 100 || !float.IsFinite(plan.CameraHeight) || plan.CameraHeight is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(plan.CameraDistance), "Camera distance and height must be bounded and positive.");
        if (!float.IsFinite(plan.CameraAzimuthDegrees) || plan.CameraAzimuthDegrees is < 0 or >= 360)
            throw new ArgumentOutOfRangeException(nameof(plan.CameraAzimuthDegrees), "Camera azimuth must be from 0 up to 360 degrees.");
        if (plan.Supersize is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(plan.Supersize));
        if (string.IsNullOrWhiteSpace(plan.HostDirectory) || plan.HostDirectory.Any(char.IsWhiteSpace) || !Path.IsPathFullyQualified(plan.EvidenceDirectory) ||
            string.IsNullOrWhiteSpace(plan.WorldUid) || string.IsNullOrWhiteSpace(plan.GameBuild) || plan.GameBuild.Length > 64 || plan.PluginPins == null || plan.PluginPins.Count == 0)
            throw new ArgumentException("Use a private no-space host directory, an absolute new evidence directory and complete provenance.");
        string host = plan.HostDirectory.Replace('\\', '/');
        bool absolute = shell == HostShellKind.PowerShell ? Regex.IsMatch(host, "^[A-Za-z]:/[^/].*") : host.StartsWith("/", StringComparison.Ordinal);
        if (!absolute || host.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("The host image directory must be absolute and contain no traversal segments.");
    }

    private static bool CompletePng(byte[] bytes) => bytes.Length >= 20 &&
        bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        bytes.AsSpan(bytes.Length - 8, 8).SequenceEqual(new byte[] { 73, 69, 78, 68, 174, 66, 96, 130 });
}
