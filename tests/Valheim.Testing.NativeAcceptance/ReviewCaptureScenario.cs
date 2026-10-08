using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// The plan's declared capture conditions for issue #78: the <see cref="ReviewCapturePlan"/> fields an operator chooses,
/// with their defaults. <see cref="ReviewCapture.Validate"/> owns the ranges; this class only carries the JSON.
/// </summary>
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
}

/// <summary>Example of issue #78: two human-review stills from the same joined client and declared site.</summary>
public static class ReviewCaptureScenario
{
    private static readonly string[] Ids = ["first", "second"];
    /// <summary>The runner's own machine runs the client in this scenario, so its shell decides the host path rules.</summary>
    private static HostShell LocalShell => OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash;

    /// <summary>The two stills a run under <paramref name="output"/> captures, as the library validates and takes them.</summary>
    private static IEnumerable<ReviewCapturePlan> Shots(AcceptancePlan plan, string output) => Ids.Select(id =>
    {
        var capture = plan.Capture ?? throw new ArgumentException("Add capture conditions to the review-capture plan.");
        var client = plan.Client ?? throw new ArgumentException("The review-capture scenario looks from a client: add the client section.");
        return new ReviewCapturePlan(id,
            new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground),
            capture.Weather, capture.TimeOfDay, capture.CameraDistance, capture.CameraHeight,
            Path.Combine(output, "capture-host-" + id),
            Path.Combine(output, "review-" + id), plan.WorldUid, capture.GameBuild,
            client.Pins, capture.MistOff, capture.ClutterOff, capture.Supersize, capture.CameraAzimuthDegrees);
    });

    /// <summary>
    /// Refuses the declared conditions with the library's rule before anything launches. The output directory is not known
    /// yet, so a fixed placeholder stands in; the run's real directories are checked again when each still is taken.
    /// </summary>
    public static void Validate(AcceptancePlan plan)
    {
        string placeholder = OperatingSystem.IsWindows() ? @"C:\review-plan-check" : "/review-plan-check";
        foreach (var shot in Shots(plan, placeholder)) ReviewCapture.Validate(shot, LocalShell.Kind);
    }

    public static void Run(GameSession session, AcceptancePlan plan)
    {
        if ((session.CampaignClients.Count != 0))
            throw new NotSupportedException("This scenario captures on the runner's local client. A campaign client needs its host (session.ClientHost) passed to ReviewCapture.");
        var client = plan.Client!;
        // The run's own directories, before the client launches: an output path with a space is refused here, not mid-round.
        foreach (var shot in Shots(plan, session.Output)) ReviewCapture.Validate(shot, LocalShell.Kind);
        IGameHost host = new LocalGameHost("review-client", LocalShell);
        session.Report.Provenance["humanReview"] = "two stills; no visual verdict asserted";
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = session.Report, Output = session.Output,
            OwnedServer = session.Server!,
            Rounds = ["joined"], Cancellation = session.Cancellation,
        }.Run(session.Server!.Game, () => session.OpenClient(client), round =>
        {
            // Each still is linked from result.json (kind review-still) with its SHA-256; inspect both images by eye.
            foreach (var shot in Shots(plan, session.Output))
                round.Step($"capture {shot.Id} view for human review", () => session.Report.Attach(ReviewCapture.Capture(round.Server, round.Client, host, shot,
                    TimeSpan.FromSeconds(client.ArrivalSeconds), TimeSpan.FromSeconds(30), session.Cancellation).Evidence));
        });
    }
}
