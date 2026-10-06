using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// <c>content-census</c> (#91/#114): AcceptanceMod registers one item, its network prefabs, a recipe, a build piece and a status effect
/// (AcceptanceMod's <c>Content.cs</c>),
/// and <see cref="ExpectationsResource"/> declares them for the server and the client. A client running the server's AcceptanceMod
/// and adapter builds joins; in each round (<c>first</c>, and <c>after-restart</c>, when both processes have loaded a world
/// again) the adapter's content census is read on the server and on the client, each through its own pinned actor, and
/// reconciled: each side must report being that side, run the pinned AcceptanceMod build and hold every declared entry once, with
/// the recipe's item, workbench and wood resolved by the game's own lookups, and nothing undeclared in scope. The census and
/// its report are written to <c>{round}-content-census.json</c> before the check.
/// <para>
/// The omitted-recipe and omitted-status-effect builds each pass only when the census fails on that entry alone, on both sides.
/// </para>
/// </summary>
public static class ContentCensusScenario
{
    /// <summary>AcceptanceMod's declared content, embedded from <c>content-expectations.json</c>.</summary>
    public const string ExpectationsResource = "Valheim.Testing.NativeAcceptance.content-expectations.json";
    /// <summary>The names AcceptanceMod's <c>Content.cs</c> registers.</summary>
    public const string ItemName = "AcceptanceMod_SurveyStake", RecipeName = "Recipe_AcceptanceMod_SurveyStake",
        PieceName = "AcceptanceMod_SurveyPost", StatusName = "AcceptanceMod_SurveyBlessing";
    /// <summary>The check's step within a round.</summary>
    public const string Check = "the declared content is registered on the server and the client";
    /// <summary>What the omitted-recipe control's failure must say: nothing but the recipe is missing, on each side.</summary>
    public const string OnlyTheOmittedRecipe = "the census fails only on the omitted recipe";
    public const string OnlyTheOmittedStatusEffect = "the census fails only on the omitted status effect";

    /// <summary>AcceptanceMod's content expectations, as the file declares them.</summary>
    public static ContentExpectations Expectations()
    {
        using var stream = typeof(ContentCensusScenario).Assembly.GetManifestResourceStream(ExpectationsResource)
            ?? throw new InvalidOperationException("The runner was built without " + ExpectationsResource + ".");
        using var reader = new StreamReader(stream);
        return ContentExpectations.Parse(reader.ReadToEnd());
    }

    public static void Run(GameSession session, AcceptancePlan plan)
    {
        var report = session.Report; var client = plan.Client!; var control = plan.Control;
        var expectations = Expectations();
        report.Provenance["contentExpectations"] = $"{expectations.Owner} [{string.Join(", ", expectations.Scope)}]: " +
            string.Join(", ", expectations.Entries.Select(e => $"{e.Kind} {e.Name} ({string.Join("+", e.Sides).ToLowerInvariant()})"));
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = session.Output, OwnedServer = session.Server!, Cancellation = session.Cancellation,
        }.Run(session.Server!.Game, () => session.OpenClient(client), round =>
        {
            ContentObservation? server = null, joined = null;
            round.Step("read the server's content census", () => server = ContentCensus.Read(round.Server, Capabilities.ContentCensus, expectations));
            round.Step("read the client's content census", () => joined = ContentCensus.Read(round.Client, Capabilities.ContentCensus, expectations));
            var census = ContentCensus.Reconcile(expectations,
                new SideObservation(CensusSide.Server, plan.Pins[AcceptancePlan.ModPlugin], server),
                new SideObservation(CensusSide.Client, client.Pins[AcceptancePlan.ModPlugin], joined));
            round.Write("content-census", new { expectations = expectations.Entries, server, client = joined, report = census });
            if (control == null) { round.Step(Check, census.RequirePassed); return; }
            ControlPlugins.ExpectFailure(report, control, () =>
            {
                if (control.Name == ControlPlugins.OmittedStatusEffect) RequireOnlyOmittedStatusEffect(census);
                else RequireOnlyOmittedRecipe(census);
            }, round.Name + ": ");
            throw new ControlConcluded(control);
        });
    }

    /// <summary>
    /// The omitted-recipe control's check: throws naming <see cref="OnlyTheOmittedRecipe"/> only when the census fails on
    /// the recipe alone (missing on the server and on the client, nothing else wrong). Any other failure is thrown as the
    /// census reports it, without that phrase, and a census that passes returns, so the control run fails either way.
    /// </summary>
    public static void RequireOnlyOmittedRecipe(ContentCensusReport census)
    {
        if (census.Passed) return;
        var wrong = census.Entries.Where(e => e.State != CensusState.Present).ToList();
        bool only = census.Sides.All(s => s.Problem == null) && wrong.Count == 2 &&
            wrong.All(e => e.Kind == "recipe" && e.Name == RecipeName && e.State == CensusState.Missing) &&
            wrong.Select(e => e.Side).Distinct().Count() == 2;
        if (only) throw new InvalidOperationException($"{OnlyTheOmittedRecipe}: {string.Join("; ", census.Failures)}.");
        census.RequirePassed();
    }

    public static void RequireOnlyOmittedStatusEffect(ContentCensusReport census)
    {
        if (census.Passed) return;
        var wrong = census.Entries.Where(e => e.State != CensusState.Present).ToList();
        bool only = census.Sides.All(s => s.Problem == null) && wrong.Count == 2 &&
            wrong.All(e => e.Kind == "statusEffect" && e.Name == StatusName && e.State == CensusState.Missing) &&
            wrong.Select(e => e.Side).Distinct().Count() == 2;
        if (only) throw new InvalidOperationException($"{OnlyTheOmittedStatusEffect}: {string.Join("; ", census.Failures)}.");
        census.RequirePassed();
    }
}
