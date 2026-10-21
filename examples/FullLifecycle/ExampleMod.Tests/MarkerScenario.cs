using System.Globalization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace ExampleMod.Tests;

/// <summary>
/// The example mod's scenarios on a <see cref="GameSession"/>: an owned dedicated server with the mod and clean clients.
/// The one-client persistence mode follows these rounds:
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
/// The toolkit's <see cref="ClientRounds"/> runs those persistence rounds and always closes the client. The separate
/// two-client visibility mode checks each client's marker census, then B rejoins while A stays. <see cref="GameSession"/>
/// owns both clients' lifecycle and cleanup; this scenario supplies the assertions.
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
        ClientPlans = plan => MarkerPlan.Clients(plan).Values,
        Scenario = scenario,
    };

    /// <summary>Runs the declared persistence or two-client visibility mode on the session's actors.</summary>
    public static void Run(GameSession session, MarkerPlan plan)
    {
        if (plan.SecondClient != null)
        {
            RunPair(plan, session.Server!.Game,
                () => session.OpenClientsAsync(MarkerPlan.Clients(plan)).GetAwaiter().GetResult()
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Actor, StringComparer.Ordinal),
                () => session.Server!.WaitUntilJoinable(session.Server.Game), session.Report, session.Output, session.Cancellation);
            return;
        }
        IGameHost? reviewHost = plan.ReviewStill == null ? null : session.CampaignClients.Count == 0
            ? new LocalGameHost("review-client", HostShell.ThisMachine) : session.ClientHost("client");
        Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report,
            session.Output, session.Cancellation, reviewHost);
    }

    public static void Run(MarkerPlan plan, GameActor server, IOwnedServer ownedServer, Func<ClientSession> openClient,
        ScenarioReport report, string output, CancellationToken cancellation = default, IGameHost? reviewHost = null)
    {
        plan.RequireScenario(MarkerPlan.ScenarioName);
        plan.CheckSites();
        var client = plan.Client ?? throw new ArgumentException("The scenario needs the plan's client section.");
        ReviewCapturePlan? still = plan.ReviewStill?.For(plan, output);
        if (still != null)
        {
            if (reviewHost == null) throw new ArgumentException("A review still needs the client's IGameHost.");
            ReviewCapture.Validate(still, reviewHost.Shell.Kind);
            report.Provenance["humanReview"] = "one still; visual verdict not asserted";
        }
        MarkAndRequireServer(plan, server, report);

        // Join, protect, arrive, then this mod's measurement; between the rounds a confirmed save, the client leaves and
        // only the owned server restarts.
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = output, OwnedServer = ownedServer,
            Arrival = new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground),
            ArriveStep = "arrive beside the marker", Cancellation = cancellation,
        }.Run(server, openClient,
            measure: round =>
            {
                round.Step("the client sees the marker at the dry site", () => RequireClientMarkers(round.Client, plan.DrySite, 1));
                if (round.Index == 0 && still != null)
                    round.Step("capture marker for human review", () => report.Attach(ReviewCapture.Capture(
                        round.Server, round.Client, reviewHost!, still, TimeSpan.FromSeconds(client.ArrivalSeconds),
                        TimeSpan.FromSeconds(30), cancellation).Evidence));
            },
            afterRestart: round => round.Step("the server still has one marker at the dry site, none at the wet site",
                () => { RequireServerMarkers(round.Server, plan.DrySite, 1); RequireServerMarkers(round.Server, plan.WetSite, 0); }));
    }

    // This overload owns assertions only; the GameSession that opens the actors owns their cleanup.
    public static void RunPair(MarkerPlan plan, GameActor server, Func<IReadOnlyDictionary<string, GameActor>> openClients,
        Action waitUntilJoinable, ScenarioReport report, string output, CancellationToken cancellation = default)
    {
        plan.RequireScenario(MarkerPlan.TwoClientScenarioName);
        plan.CheckSites();
        var plans = MarkerPlan.Clients(plan);
        if (plans.Count != 2) throw new ArgumentException("The visibility scenario needs two named client plans.");
        MarkAndRequireServer(plan, server, report);
        report.Step(StepPhase.Setup, "the server accepts both clients", waitUntilJoinable);
        IReadOnlyDictionary<string, GameActor>? actors = null;
        report.Step(StepPhase.Setup, "open both pinned clients on their assigned hosts", () =>
        {
            actors = openClients();
            if (actors.Count != 2 || !actors.ContainsKey(MarkerPlan.FirstClientName) || !actors.ContainsKey(MarkerPlan.SecondClientName) ||
                ReferenceEquals(actors[MarkerPlan.FirstClientName], actors[MarkerPlan.SecondClientName]))
                throw new InvalidOperationException("The visibility scenario needs two distinct named client actors.");
        });
        var a = actors![MarkerPlan.FirstClientName];
        var b = actors[MarkerPlan.SecondClientName];
        var first = plans[MarkerPlan.FirstClientName];
        var second = plans[MarkerPlan.SecondClientName];
        JoinAndArrive(a, first, plan.Arrival, "A");
        JoinAndArrive(b, second, plan.SecondArrival!, "B");
        report.Step("two clients are connected together", () => RequirePeers(2));
        report.Step("A sees exactly one marker", () => RequireClientMarkers(a, plan.DrySite, 1));
        report.Step("B independently sees exactly one marker", () => RequireClientMarkers(b, plan.DrySite, 1));
        Leave(b, second, "B leaves while A stays");
        report.Step("A remains pinned in the world while B is away", () =>
        {
            a.VerifyEnvironment(first.WorldExpectations(plan.WorldUid));
            var state = new SessionControl(a).Read();
            if (!state.WorldPresent || !state.WorldReady || state.WorldUid != plan.WorldUid)
                throw new InvalidOperationException("A did not remain ready in the pinned world.");
            RequirePeers(1);
            RequireClientMarkers(a, plan.DrySite, 1);
        });
        JoinAndArrive(b, second, plan.SecondArrival!, "B after rejoin");
        report.Step("two clients are connected after B rejoins", () => RequirePeers(2));
        report.Step("A still sees exactly one marker after B rejoins", () => RequireClientMarkers(a, plan.DrySite, 1));
        report.Step("B sees exactly one marker after rejoin", () => RequireClientMarkers(b, plan.DrySite, 1));
        Leave(b, second, "B leaves at the end");
        Leave(a, first, "A leaves at the end");

        void RequirePeers(int expected)
        {
            int found = PlayerPlacement.PeerCount(server);
            if (found != expected) throw new InvalidOperationException($"Expected {expected} connected peers; the server lists {found}.");
        }
        void Leave(GameActor actor, ClientRunPlan client, string name) => report.Step(StepPhase.Setup, name, () =>
        {
            new SessionControl(actor).Leave();
            actor.VerifyEnvironment(client.MenuExpectations);
        });
        void JoinAndArrive(GameActor actor, ClientRunPlan client, Site arrival, string name)
        {
            report.Step(StepPhase.Setup, name + " joins the pinned world and is protected", () =>
                new SessionControl(actor).JoinWorld(client, plan.WorldUid, cancellation: cancellation));
            report.Step(StepPhase.Setup, name + " arrives beside the marker on dry loaded ground", () =>
            {
                var measured = PlayerPlacement.Arrive(null, actor, new HeightExpectation(arrival.X, arrival.Z, arrival.Ground),
                    TimeSpan.FromSeconds(client.ArrivalSeconds), cancellation, loadedGround: true);
                if (measured.Target.Height < MarkerPlan.WaterLevel + MarkerPlan.Clearance)
                    throw new InvalidOperationException(name + " arrived on wet loaded ground.");
                File.WriteAllText(Path.Combine(output, name.Replace(' ', '-') + "-arrival.json"),
                    System.Text.Json.JsonSerializer.Serialize(measured));
            });
        }
    }

    private static void MarkAndRequireServer(MarkerPlan plan, GameActor server, ScenarioReport report)
    {
        report.Step("no marker at either site before the mod acts", () => { RequireServerMarkers(server, plan.DrySite, 0); RequireServerMarkers(server, plan.WetSite, 0); });
        report.Step("the mod marks the dry site", () => server.Execute(Mark(plan.DrySite)).RequireLine("OK: marked ", "ExampleMod did not mark the dry site"));
        report.Step("the mod refuses the wet site", () => server.Execute(Mark(plan.WetSite)).RequireLine("REFUSED: ", "ExampleMod did not refuse the wet site"));
        report.Step("server: one marker at the dry site, none at the wet site", () => { RequireServerMarkers(server, plan.DrySite, 1); RequireServerMarkers(server, plan.WetSite, 0); });
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
