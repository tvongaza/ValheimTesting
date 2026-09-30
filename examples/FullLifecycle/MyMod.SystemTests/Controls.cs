using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// A negative-control plugin (examples/FullLifecycle/Controls): installed only for a run whose plan names it in
/// <c>expectFailure</c>, on the server or the client, in one scenario. <see cref="Check"/> names the check it must make
/// fail and <see cref="Reason"/> the text that failure must contain; any other outcome fails the run.
/// </summary>
public sealed record ControlPlugin(string Name, string Guid, bool OnServer, string Scenario, string Check, string Reason);

/// <summary>The four controls, and how a scenario requires one to fail.</summary>
public static class ControlPlugins
{
    public const string MissingHarmonyTarget = "missing-harmony-target", ServerOnlyPrefab = "server-only-prefab",
        FieldOnlyState = "field-only-state", SuppressedProfileSave = "suppressed-profile-save";
    /// <summary>The prefab only the server-only-prefab control registers.</summary>
    public const string ServerOnlyPrefabName = "MyModControl_ServerOnly";
    /// <summary>The missing-harmony-target control's patch, on a method the game does not have.</summary>
    public static readonly DeclaredPatch MissingPatch = new("Player::MyModControlMethodThatDoesNotExist", "postfix");

    public static readonly IReadOnlyList<ControlPlugin> All =
    [
        new(MissingHarmonyTarget, "example.mymod.control.missingtarget", OnServer: true, LifecyclePlan.WorldScenario,
            "the control's Harmony patch is applied", "not applied: " + MissingPatch),
        new(ServerOnlyPrefab, "example.mymod.control.serveronlyprefab", OnServer: true, LifecyclePlan.VanillaClientScenario,
            "the vanilla client resolves every prefab hash where the player stands", $"{StableHash.Of(ServerOnlyPrefabName)} ({ServerOnlyPrefabName})"),
        new(FieldOnlyState, "example.mymod.control.fieldonlystate", OnServer: false, LifecyclePlan.WorldScenario,
            "the field-only value survives the zone reload", "did not survive the zone reload"),
        new(SuppressedProfileSave, "example.mymod.control.suppressedsave", OnServer: false, LifecyclePlan.WorldScenario,
            "the logout rewrites the character file", "to be rewritten by the logout"),
    ];

    public static ControlPlugin? Named(string? name) => name == null ? null : All.FirstOrDefault(control => control.Name == name);

    /// <summary>
    /// Runs the control's check as one step, <c>control {name}: {check} fails for the named reason</c>, which passes only
    /// when <paramref name="check"/> throws with the control's <see cref="ControlPlugin.Reason"/> in its message. A check
    /// that passes, or fails for another reason, fails the step: the check could not see the defect the control plants,
    /// or something else broke. The expected failure's message is recorded as <c>controlFailure</c>.
    /// </summary>
    public static void ExpectFailure(ScenarioReport report, ControlPlugin control, Action check, string? stepPrefix = null)
    {
        report.Step($"{stepPrefix}control {control.Name}: {control.Check} fails for the named reason", () =>
        {
            try { check(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (!error.Message.Contains(control.Reason, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The {control.Name} control's check failed, but not for its reason (\"{control.Reason}\"): {error.Message}", error);
                report.Provenance["controlFailure"] = error.Message;
                return;
            }
            throw new InvalidOperationException($"The {control.Name} control is installed, yet \"{control.Check}\" passed: the check cannot see the defect the control plants.");
        });
    }
}

/// <summary>
/// Ends a control run once its expected failure is recorded: the rest of the scenario would only repeat what the control
/// breaks. Thrown out of a round's measurement (the client is still closed), caught by <see cref="CampaignScenarios.Run"/>.
/// </summary>
public sealed class ControlConcluded(ControlPlugin control) : Exception($"The {control.Name} control failed its check as expected; the run ends here.")
{
    public ControlPlugin Control { get; } = control;
}
