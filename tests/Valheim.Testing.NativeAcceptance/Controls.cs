using Valheim.Testing.Game;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// A negative-control plugin (tests/Valheim.Testing.NativeAcceptance/Controls): installed only for a run whose plan names it in
/// <c>expectFailure</c>, on the server or the client, in one scenario. <see cref="Check"/> names the check it must make
/// fail and <see cref="Reason"/> the text that failure must contain; any other outcome fails the run. A
/// <see cref="Build"/> control is not a plugin but a build of the plugin <see cref="Guid"/> names (AcceptanceMod built with a
/// defect), pinned by its MD5 where the normal build would be: the plan cannot tell the two builds apart, so only the
/// check's named failure shows which one ran.
/// </summary>
public sealed record ControlPlugin(string Name, string Guid, bool OnServer, string Scenario, string Check, string Reason, bool Build = false);

/// <summary>The four controls, and how a scenario requires one to fail.</summary>
public static class ControlPlugins
{
    public const string MissingHarmonyTarget = "missing-harmony-target", ServerOnlyPrefab = "server-only-prefab",
        FieldOnlyState = "field-only-state", SuppressedProfileSave = "suppressed-profile-save", OmittedRecipe = "omitted-recipe",
        OmittedStatusEffect = "omitted-status-effect";
    /// <summary>The prefab only the server-only-prefab control registers.</summary>
    public const string ServerOnlyPrefabName = "AcceptanceModControl_ServerOnly";
    /// <summary>The missing-harmony-target control's patch, on a method the game does not have.</summary>
    public const string MissingMethodName = "AcceptanceModControlMethodThatDoesNotExist";
    public static readonly DeclaredPatch MissingPatch = new("Player::" + MissingMethodName, "postfix");
    /// <summary>The control's patch class, as HarmonyX's "Undefined target method" error names it in Unity's log.</summary>
    public const string MissingPatchClass = "MissingHarmonyTarget.Plugin+PatchMissingMethod";
    /// <summary>The line the missing-harmony-target control logs once <c>PatchAll</c> has returned (so it did not throw).</summary>
    public const string PatchAllReturnedLine = "MissingHarmonyTarget: PatchAll returned";
    /// <summary>
    /// The missing-harmony-target control's second check (#26): the teardown log scan, with its default classifications, over
    /// the server's live log. It must fail on HarmonyX's <c>accesstools-not-found</c> warning naming the control's method.
    /// </summary>
    public const string ScanCheck = "the server's log scan passes";

    public static readonly IReadOnlyList<ControlPlugin> All =
    [
        new(MissingHarmonyTarget, "valheimtesting.acceptancemod.control.missingtarget", OnServer: true, AcceptancePlan.WorldScenario,
            "the control's Harmony patch is applied", "not applied: " + MissingPatch),
        new(ServerOnlyPrefab, "valheimtesting.acceptancemod.control.serveronlyprefab", OnServer: true, AcceptancePlan.VanillaClientScenario,
            "the vanilla client resolves every prefab hash where the player stands", $"{StableHash.Of(ServerOnlyPrefabName)} ({ServerOnlyPrefabName})"),
        new(FieldOnlyState, "valheimtesting.acceptancemod.control.fieldonlystate", OnServer: false, AcceptancePlan.WorldScenario,
            "the field-only value survives the zone reload", "did not survive the zone reload"),
        new(SuppressedProfileSave, "valheimtesting.acceptancemod.control.suppressedsave", OnServer: false, AcceptancePlan.WorldScenario,
            "the logout rewrites the character file", "to be rewritten by the logout"),
    ];

    /// <summary>
    /// The build controls omit the recipe or status effect from AcceptanceMod on both sides; the content census must identify
    /// exactly that omission (#91/#114).
    /// </summary>
    public static readonly IReadOnlyList<ControlPlugin> Builds =
    [
        new(OmittedRecipe, AcceptancePlan.ModPlugin, OnServer: true, AcceptancePlan.ContentCensusScenario,
            ContentCensusScenario.Check, ContentCensusScenario.OnlyTheOmittedRecipe, Build: true),
        new(OmittedStatusEffect, AcceptancePlan.ModPlugin, OnServer: true, AcceptancePlan.ContentCensusScenario,
            ContentCensusScenario.Check, ContentCensusScenario.OnlyTheOmittedStatusEffect, Build: true),
    ];

    /// <summary>Every control, plugins and builds, by name.</summary>
    public static IEnumerable<ControlPlugin> Every => All.Concat(Builds);

    public static ControlPlugin? Named(string? name) => name == null ? null : Every.FirstOrDefault(control => control.Name == name);

    /// <summary>
    /// Runs the control's check as one step, <c>control {name}: {check} fails for the named reason</c>, which passes only
    /// when <paramref name="check"/> throws with the control's <see cref="ControlPlugin.Reason"/> in its message. A check
    /// that passes, or fails for another reason, fails the step: the check could not see the defect the control plants,
    /// or something else broke. The expected failure's message is recorded as <c>controlFailure</c>.
    /// </summary>
    public static void ExpectFailure(ScenarioReport report, ControlPlugin control, Action check, string? stepPrefix = null) =>
        ExpectFailure(report, control, control.Check, control.Reason, "controlFailure", check, stepPrefix);

    /// <summary>
    /// As <see cref="ExpectFailure(ScenarioReport, ControlPlugin, Action, string?)"/>, for a control's further check with its
    /// own name and reason; the expected failure's message is recorded under <paramref name="provenanceKey"/>.
    /// </summary>
    public static void ExpectFailure(ScenarioReport report, ControlPlugin control, string checkName, string reason, string provenanceKey, Action check, string? stepPrefix = null)
    {
        report.Step($"{stepPrefix}control {control.Name}: {checkName} fails for the named reason", () =>
        {
            try { check(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (!error.Message.Contains(reason, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The {control.Name} control's check failed, but not for its reason (\"{reason}\"): {error.Message}", error);
                report.Provenance[provenanceKey] = error.Message;
                return;
            }
            throw new InvalidOperationException($"The {control.Name} control is installed, yet \"{checkName}\" passed: the check cannot see the defect the control plants.");
        });
    }
}

/// <summary>
/// Ends a control run once its expected failure is recorded: the rest of the scenario would only repeat what the control
/// breaks. Thrown out of a round's measurement (the client is still closed), caught by <see cref="ScenarioTable.Run"/>.
/// </summary>
public sealed class ControlConcluded(ControlPlugin control) : Exception($"The {control.Name} control failed its check as expected; the run ends here.")
{
    public ControlPlugin Control { get; } = control;
}
