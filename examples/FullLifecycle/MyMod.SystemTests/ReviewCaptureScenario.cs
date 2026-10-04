using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>Example of issue #78: two human-review stills from the same joined client and declared site.</summary>
public sealed class CaptureSettings
{
    public string GameBuild { get; set; } = "";
    public string Weather { get; set; } = "Clear";
    public float TimeOfDay { get; set; } = 0.45f;
    public float CameraDistance { get; set; } = 24f;
    public float CameraHeight { get; set; } = 12f;
    /// <summary>Direction from the subject toward the camera, clockwise from north; change this when foliage blocks a view.</summary>
    public float CameraAzimuthDegrees { get; set; } = 225f;
    public bool MistOff { get; set; } = true;
    public bool ClutterOff { get; set; }
    public int Supersize { get; set; } = 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(GameBuild) || GameBuild.Length > 64)
            throw new ArgumentException("Declare the Valheim game build for the review evidence.");
        if (Weather.Length is < 1 or > 64 || !Weather.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Review weather must be one named environment.");
        if (!float.IsFinite(TimeOfDay) || TimeOfDay is < 0 or > 1 ||
            !float.IsFinite(CameraDistance) || CameraDistance is < 2 or > 100 ||
            !float.IsFinite(CameraHeight) || CameraHeight is < 1 or > 100 ||
            !float.IsFinite(CameraAzimuthDegrees) || CameraAzimuthDegrees is < 0 or >= 360 || Supersize is < 1 or > 4)
            throw new ArgumentException("Review time, camera distance, height or supersize is outside the supported range.");
    }
}

public static class ReviewCaptureScenario
{
    public static void Run(CampaignRun run)
    {
        if (run.Profile != null)
            throw new NotSupportedException("This example captures on the runner's local client. A remote client needs its profile host passed to ReviewCapture.");
        var plan = run.Plan;
        var capture = plan.Capture!;
        var client = plan.Client!;
        IGameHost host = new LocalGameHost("review-client", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash);
        run.Report.Provenance["humanReview"] = "two stills; no visual verdict asserted";
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = run.Report, Output = run.Output,
            OwnedServer = run.OwnedServer,
            Rounds = ["joined"], Cancellation = run.Cancellation,
        }.Run(run.Server, () => run.OpenClient(client, null), round =>
        {
            foreach (var id in new[] { "first", "second" })
            {
                round.Step($"capture {id} view for human review", () =>
                {
                    var shot = new ReviewCapturePlan(id,
                        new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground),
                        capture.Weather, capture.TimeOfDay, capture.CameraDistance, capture.CameraHeight,
                        "mymod.testing", Path.Combine(run.Output, "capture-host-" + id),
                        Path.Combine(run.Output, "review-" + id), plan.WorldUid, capture.GameBuild,
                        client.Pins, capture.MistOff, capture.ClutterOff, capture.Supersize, capture.CameraAzimuthDegrees);
                    _ = ReviewCapture.CaptureAsync(round.Server, round.Client, host, shot,
                        TimeSpan.FromSeconds(client.ArrivalSeconds), TimeSpan.FromSeconds(30), run.Cancellation)
                        .GetAwaiter().GetResult();
                });
            }
            run.Report.Provenance["reviewCapture"] = "review-first/first.png and review-second/second.png; inspect both images by eye";
        });
    }
}
