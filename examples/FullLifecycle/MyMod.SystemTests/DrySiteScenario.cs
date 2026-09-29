using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// The example mod's end-to-end scenario on an owned server, with the mod's own expectations and the toolkit's pieces:
/// <list type="number">
/// <item>The mod's Harmony patch is applied (the adapter's census), so a missing target fails here, by name.</item>
/// <item>Neither site has a marker yet (the fresh fixture copy), so any marker found later is the mod's.</item>
/// <item>The mod marks the dry site and refuses the wet one, each asked exactly once.</item>
/// <item>The server's saved objects show one marker at the dry site and none at the wet one.</item>
/// <item>A client without the mod starts (owned) or is attached to; once the server accepts game connections (waited for
/// only now, so it overlaps the steps before) the client joins, is protected, arrives beside the marker and sees it.</item>
/// <item>Confirmed save, the client leaves, only the owned server restarts; the server still has the marker, the client
/// rejoins and sees it again, then leaves.</item>
/// <item>Optionally, a person looks at it (<see cref="ReviewSettings"/>); the verdict is recorded, never scored.</item>
/// </list>
/// The toolkit's <see cref="ClientRounds"/> runs the client rounds; this scenario supplies the checks and step names. The
/// client is closed in every outcome: an owned client's process is stopped, an attached one is left running.
/// </summary>
public static class DrySiteScenario
{
    public const string Marker = "wood_pole2";
    /// <summary>How far from a site a marker may stand and still count as that site's.</summary>
    public const float MarkerRadius = 1.5f;
    /// <summary>The Harmony patches the mod declares (its <c>[HarmonyPatch]</c> classes); each must be applied.</summary>
    public static readonly DeclaredPatch[] Patches = [new("Terminal::InitTerminal", "postfix", "MyMod.Plugin+RegisterCommands::Postfix")];

    public static void Run(LifecyclePlan plan, GameActor server, Func<GameActor> restartOwnedServer, Func<ClientSession> openClient,
        Action<GameActor> waitUntilJoinable, ScenarioReport report, string output, CancellationToken cancellation = default, TimeSpan? settleFor = null)
    {
        var client = plan.Client ?? throw new ArgumentException("The run mode needs the plan's client section.");
        report.Step("server: the mod's Harmony patches are applied", () =>
            HarmonyCensus.Read(server, "mymod.testing/harmony", LifecyclePlan.ModPlugin).Check(LifecyclePlan.ModPlugin, Patches).RequireApplied());
        report.Step("no marker at either site before the mod acts", () => { RequireServerMarkers(server, plan.DrySite, 0); RequireServerMarkers(server, plan.WetSite, 0); });
        report.Step("the mod marks the dry site", () => RequireReply(server.Execute(Mark(plan.DrySite)), "OK: marked "));
        report.Step("the mod refuses the wet site", () => RequireReply(server.Execute(Mark(plan.WetSite)), "REFUSED: "));
        report.Step("server: one marker at the dry site, none at the wet site", () => { RequireServerMarkers(server, plan.DrySite, 1); RequireServerMarkers(server, plan.WetSite, 0); });

        // The client rounds are the toolkit's (ClientRounds): join, protect, arrive, then this mod's measurement; between
        // the rounds a confirmed save, the client leaves and only the owned server restarts. It always closes the client.
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = output, WaitUntilJoinable = waitUntilJoinable,
            RestartServer = restartOwnedServer, Arrival = new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground),
            ArriveStep = "arrive beside the marker", SettleFor = settleFor, Cancellation = cancellation,
        }.Run(server, openClient,
            measure: round =>
            {
                round.Step("the client sees the marker at the dry site", () => RequireClientMarkers(round.Client, plan.DrySite, 1));
                if (round.Last && plan.Review.Enabled) Review(plan, report, output, cancellation);
            },
            afterRestart: round => round.Step("the server still has one marker at the dry site, none at the wet site",
                () => { RequireServerMarkers(round.Server, plan.DrySite, 1); RequireServerMarkers(round.Server, plan.WetSite, 0); }));
    }

    public static string Mark(Site site) => string.Create(CultureInfo.InvariantCulture, $"mymod_mark {site.X} {site.Z}");

    private static void RequireReply(valheim_cli.Testing.CommandResult reply, string prefix)
    {
        if (reply.Output.Count(line => line.StartsWith("OK: ", StringComparison.Ordinal) || line.StartsWith("REFUSED: ", StringComparison.Ordinal)) != 1 ||
            !reply.Output.Any(line => line.StartsWith(prefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Expected one \"{prefix.Trim()}\" reply from the mod: " + string.Join(" | ", reply.Output));
    }

    private static readonly Regex ZdoLine = new(@"^ZDO (\S+) id=\S+ pos=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+) ", RegexOptions.CultureInvariant);
    private static readonly Regex ZdoSummary = new(@"^OK: ZDOS_AT .* objects=(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex PrefabLine = new(@"^PREFAB name=(\S+) distance=\S+ pos=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+) ", RegexOptions.CultureInvariant);
    private static readonly Regex PrefabSummary = new(@"^OK: NEARBY_PREFABS radius=\S+ count=(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>Markers in the server's saved objects around a site, whether or not its zone is loaded (<c>cli_zdos_at</c>).</summary>
    public static void RequireServerMarkers(GameActor server, Site site, int expected) =>
        RequireCount("server", site, expected, server.Execute(string.Create(CultureInfo.InvariantCulture, $"cli_zdos_at {site.X} {site.Z} 8")).Output, ZdoLine, ZdoSummary);

    /// <summary>Markers the client has loaded around a site (<c>cli_prefabs_at</c>); the client must be near it.</summary>
    public static void RequireClientMarkers(GameActor client, Site site, int expected) =>
        RequireCount("client", site, expected, client.Execute(string.Create(CultureInfo.InvariantCulture, $"cli_prefabs_at {site.X} {site.Ground} {site.Z} 8")).Output, PrefabLine, PrefabSummary);

    // An observation counts only when it is complete: its summary is present and lists exactly the objects returned.
    private static void RequireCount(string observer, Site site, int expected, IReadOnlyList<string> output, Regex line, Regex summary)
    {
        var objects = output.Select(text => line.Match(text)).Where(match => match.Success).ToArray();
        var totals = output.Select(text => summary.Match(text)).Where(match => match.Success).ToArray();
        if (totals.Length != 1 || int.Parse(totals[0].Groups[1].Value, CultureInfo.InvariantCulture) != objects.Length)
            throw new InvalidOperationException($"Incomplete {observer} observation at ({site.X}, {site.Z}): the reply's summary does not account for the objects listed.");
        int found = objects.Count(match => match.Groups[1].Value == Marker &&
            MathF.Abs(float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - site.X) <= MarkerRadius &&
            MathF.Abs(float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) - site.Z) <= MarkerRadius);
        if (found != expected) throw new InvalidOperationException($"The {observer} lists {found} {Marker} marker(s) at ({site.X}, {site.Z}); expected {expected}.");
    }

    /// <summary>
    /// The human checkpoint: asks for a verdict in <c>review.json</c> (<c>{"verdict":"pass|fail","notes":"..."}</c>) and
    /// records it in the report's provenance. It never adds a step, so it cannot change the automated result.
    /// </summary>
    public static void Review(LifecyclePlan plan, ScenarioReport report, string output, CancellationToken cancellation)
    {
        string answer = Path.Combine(output, "review.json");
        File.WriteAllText(Path.Combine(output, "review-request.json"), JsonSerializer.Serialize(new
        {
            look = "Does the marker stand on dry ground at the dry site and look right? Walk around it.",
            site = new { plan.DrySite.X, plan.DrySite.Z }, answer, seconds = plan.Review.Seconds,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"REVIEW: the player is beside the marker. Write {answer} within {plan.Review.Seconds} s: {{\"verdict\":\"pass|fail\",\"notes\":\"...\"}}");
        using var written = new ManualResetEventSlim(File.Exists(answer));
        using var watcher = new FileSystemWatcher(output, "review.json") { EnableRaisingEvents = true };
        watcher.Created += (_, _) => written.Set(); watcher.Changed += (_, _) => written.Set(); watcher.Renamed += (_, _) => written.Set();
        if (File.Exists(answer)) written.Set();
        if (!written.Wait(TimeSpan.FromSeconds(plan.Review.Seconds), cancellation)) { report.Provenance["humanReview"] = $"no verdict within {plan.Review.Seconds} s"; return; }
        Thread.Sleep(200); // Let the writer finish the file.
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(answer));
            string verdict = document.RootElement.GetProperty("verdict").GetString() ?? "";
            report.Provenance["humanReview"] = verdict is "pass" or "fail" ? verdict : "unreadable verdict: " + verdict;
            if (document.RootElement.TryGetProperty("notes", out var notes)) report.Provenance["humanReviewNotes"] = notes.GetString() ?? "";
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or IOException) { report.Provenance["humanReview"] = "unreadable review.json: " + error.Message; }
    }
}
