using System;
using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace AcceptanceMod.Adapter;

/// <summary>
/// AcceptanceMod's test adapter serves its owned-session identity (<c>acceptancemod.testing/session</c>),
/// a guarded global-key fixture command and its own marker observation (<see cref="MarkerObservation"/>).
/// Generic observations, including Harmony and content census, belong to ValheimCLI's Observe pack.
/// The scenarios drive the mod through its own console commands.
/// <para>
/// The adapter never references AcceptanceMod's types and depends on it only softly, so it also loads on a client without AcceptanceMod:
/// the vanilla-client scenario reads that client's unresolved prefabs through it.
/// </para>
/// </summary>
[BepInPlugin("valheimtesting.acceptancemod.adapter", "AcceptanceMod Test Adapter (ValheimTesting acceptance suite)", "0.1.0")]
[BepInDependency("valheimtesting.acceptancemod", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("valheimCLI.valheimCLI")]
public sealed class Plugin : BaseUnityPlugin
{
    /// <summary>The owned session's token, which only the runner sets.</summary>
    public const string TokenVariable = "ACCEPTANCEMOD_TEST_SESSION_TOKEN";
    /// <summary>Fixture commands (the global-key change) run only in a process started with this set to 1 (FixtureGate).</summary>
    public const string FixturesVariable = "ACCEPTANCEMOD_TEST_FIXTURES";
    /// <summary>Set to 1 to leave BepInEx's disk log as it is at quit: the negative control for QuitLogFlush (#99).</summary>
    public const string NoQuitFlushVariable = "ACCEPTANCEMOD_TEST_NO_QUIT_FLUSH";
    private ExtensionRegistration? _registration;
    private Harmony? _ownershipPatch;

    // Lines plugins log while the game quits reach the kept BepInEx log, for the teardown log scan (#99).
    private void Awake()
    {
        if (Environment.GetEnvironmentVariable(NoQuitFlushVariable) != "1") QuitLogFlush.Enable();
        _ownershipPatch = new Harmony("valheimtesting.acceptancemod.adapter.marker-owner");
        MarkerOwnership.Patch(_ownershipPatch);
    }

    // The session is complete once the world is up and the mod itself is loaded.
    private IEnumerator Start() => TestExtension.Register("acceptancemod.testing", "0.1.0", TokenVariable,
        () => Chainloader.PluginInfos.ContainsKey("valheimtesting.acceptancemod"), registration => _registration = registration, Logger.LogError,
        GlobalKeyCommands.Change(FixturesVariable, TokenVariable),  // globalkey set|remove: server fixture command (#23)
        MarkerObservation.Command(),                                // markers <x> <z> [radius]: the mod's own
        MarkerOwnership.SnapshotCommand(), MarkerOwnership.WaitCommand(), MarkerOwnership.ClaimCommand(),
        AiWatch.WatchCommand(), AiWatch.SpawnCommand(), AiWatch.RemoveCommand(), AiWatch.GhostCommand()); // ghost-protection (#261)
    private void OnApplicationQuit() => QuitLogFlush.Quitting("AcceptanceMod.Adapter OnApplicationQuit");
    private void OnDestroy() { _registration?.Dispose(); _ownershipPatch?.UnpatchSelf(); QuitLogFlush.Disable(); }
}
