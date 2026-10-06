using System.Globalization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace ExampleMod.Tests;

/// <summary>
/// The example mod's end-to-end scenario on a <see cref="GameSession"/>: an owned dedicated server with the mod and a client
/// without it.
/// <list type="number">
/// <item>Before it runs, the session checked the mod's declared Harmony patch on the server (the adapter's census,
/// <see cref="MarkerPlan.Mod"/>), so a missing target fails at runtime-ready, by name.</item>
/// <item>Neither site has a marker yet (the fresh fixture copy), so any marker found later is the mod's.</item>
/// <item>The mod marks the dry site and refuses the wet one, each asked exactly once.</item>
/// <item>The server's saved objects show one marker at the dry site and none at the wet one.</item>
/// <item>The client joins, is protected, arrives beside the marker and sees it.</item>
/// <item>Confirmed save, the client leaves, only the owned server restarts; the server still has the marker, the client
/// rejoins and sees it again, then leaves.</item>
/// </list>
/// The toolkit's <see cref="ClientRounds"/> runs the client rounds and always closes the client; this scenario supplies the
/// checks and step names.
/// </summary>
public static class MarkerScenario
{
    public const string Marker = "wood_pole2";
    /// <summary>How far from a site a marker may stand and still count as that site's.</summary>
    public const float MarkerRadius = 1.5f;
    /// <summary>The Harmony patch the mod declares (its <c>[HarmonyPatch]</c> class); it must be applied.</summary>
    public static readonly DeclaredPatch[] Patches = [new("Terminal::InitTerminal", "postfix", "ExampleMod.Plugin+RegisterCommands::Postfix")];

    /// <summary>The toolkit runner's options for ExampleMod: the plan's rules, the mod's declaration and the scenario.</summary>
    public static PinnedServerRunOptions<MarkerPlan> RunnerOptions(Func<GameSession, MarkerPlan, Task> scenario) => new()
    {
        Name = "examplemod-system-test",
        ReadPlan = path => MarkerPlan.Validated(ServerRunPlan.Read<MarkerPlan>(path)),
        Mod = MarkerPlan.Mod,
        CheckPlan = plan => MarkerPlan.Validated(plan), // A session's plan is bound to its prepared actors in memory.
        Scenario = scenario,
    };

    /// <summary>Runs the scenario on the session's server and its one client.</summary>
    public static void Run(GameSession session, MarkerPlan plan) =>
        Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report, session.Output, session.Cancellation);

    public static void Run(MarkerPlan plan, GameActor server, IOwnedServer ownedServer, Func<ClientSession> openClient,
        ScenarioReport report, string output, CancellationToken cancellation = default)
    {
        var client = plan.Client ?? throw new ArgumentException("The scenario needs the plan's client section.");
        report.Step("no marker at either site before the mod acts", () => { RequireServerMarkers(server, plan.DrySite, 0); RequireServerMarkers(server, plan.WetSite, 0); });
        report.Step("the mod marks the dry site", () => server.Execute(Mark(plan.DrySite)).RequireLine("OK: marked ", "ExampleMod did not mark the dry site"));
        report.Step("the mod refuses the wet site", () => server.Execute(Mark(plan.WetSite)).RequireLine("REFUSED: ", "ExampleMod did not refuse the wet site"));
        report.Step("server: one marker at the dry site, none at the wet site", () => { RequireServerMarkers(server, plan.DrySite, 1); RequireServerMarkers(server, plan.WetSite, 0); });

        // Join, protect, arrive, then this mod's measurement; between the rounds a confirmed save, the client leaves and
        // only the owned server restarts.
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = output, OwnedServer = ownedServer,
            Arrival = new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground),
            ArriveStep = "arrive beside the marker", Cancellation = cancellation,
        }.Run(server, openClient,
            measure: round => round.Step("the client sees the marker at the dry site", () => RequireClientMarkers(round.Client, plan.DrySite, 1)),
            afterRestart: round => round.Step("the server still has one marker at the dry site, none at the wet site",
                () => { RequireServerMarkers(round.Server, plan.DrySite, 1); RequireServerMarkers(round.Server, plan.WetSite, 0); }));
    }

    public static string Mark(Site site) => string.Create(CultureInfo.InvariantCulture, $"examplemod_mark {site.X} {site.Z}");

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
}
