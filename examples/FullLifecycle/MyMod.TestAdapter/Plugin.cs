using System;
using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace MyMod.TestAdapter;

/// <summary>
/// The example's test adapter. It serves the toolkit's owned-session identity (<c>mymod.testing/session</c>), the census of
/// applied Harmony patches (<c>mymod.testing/harmony</c>), the toolkit's world observations (the registered-content census
/// among them) and one global-key fixture
/// command, and one observation of the mod's own (<see cref="MarkerObservation"/>). The scenarios drive the mod through
/// its own console commands. A mod that needs a test-only action or observation adds it here as another extension command.
/// <para>
/// The adapter never references MyMod's types and depends on it only softly, so it also loads on a client without MyMod:
/// the vanilla-client scenario reads that client's unresolved prefabs through it.
/// </para>
/// </summary>
[BepInPlugin("example.mymod.testadapter", "MyMod Test Adapter (ValheimTesting example)", "0.1.0")]
[BepInDependency("example.mymod", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("valheimCLI.valheimCLI")]
public sealed class Plugin : BaseUnityPlugin
{
    /// <summary>The owned session's token, which only the runner sets.</summary>
    public const string TokenVariable = "MYMOD_TEST_SESSION_TOKEN";
    /// <summary>Fixture commands (the global-key change) run only in a process started with this set to 1 (FixtureGate).</summary>
    public const string FixturesVariable = "MYMOD_TEST_FIXTURES";
    /// <summary>Set to 1 to leave BepInEx's disk log as it is at quit: the negative control for QuitLogFlush (#99).</summary>
    public const string NoQuitFlushVariable = "MYMOD_TEST_NO_QUIT_FLUSH";
    private ExtensionRegistration? _registration;
    private Harmony? _ownershipPatch;

    // Lines plugins log while the game quits reach the kept BepInEx log, for the teardown log scan (#99).
    private void Awake()
    {
        if (Environment.GetEnvironmentVariable(NoQuitFlushVariable) != "1") QuitLogFlush.Enable();
        _ownershipPatch = new Harmony("example.mymod.testadapter.marker-owner");
        MarkerOwnership.Patch(_ownershipPatch);
    }

    // The session is complete once the world is up and the mod itself is loaded.
    private IEnumerator Start() => TestExtension.Register("mymod.testing", "0.1.0", TokenVariable,
        () => Chainloader.PluginInfos.ContainsKey("example.mymod"), registration => _registration = registration, Logger.LogError,
        HarmonyCensus.Command(),                                    // harmony [owner]: applied patches (#30)
        ZonePresence.Command(),                                     // zones <x,z> ...: client, for ZoneCycle (#35)
        PlayerCustomData.Command(),                                 // custom-data [prefix]: client, for LogoutCycle (#35)
        GlobalKeyCommands.List(),                                   // globalkeys: either side (#23)
        GlobalKeyCommands.Change(FixturesVariable, TokenVariable),  // globalkey set|remove: server fixture command (#23)
        ConfigEntryCommand.Command(),                               // config <guid> <section> <key>: either side (#20)
        UnresolvedPrefabs.Command(),                                // unresolved-prefabs [radius]: a client without MyMod (#33)
        DungeonRooms.Command(),                                     // dungeon-rooms <x> <z> [radius]: server (#24)
        ContentCensus.Command(),                                    // content-census <owner> <prefix> ...: either side (#91)
        ReviewState.BeginCommand(), ReviewState.MistOffCommand(), ReviewState.ClutterOffCommand(), ReviewState.RestoreCommand(), // owned visual-state lease (#78)
        ReviewClipFrames.Command(),                                  // bounded, scene-only motion evidence (#212)
        MarkerObservation.Command(),                                // markers <x> <z> [radius]: the mod's own
        MarkerOwnership.SnapshotCommand(), MarkerOwnership.WaitCommand(), MarkerOwnership.ClaimCommand());
    private void OnApplicationQuit() => QuitLogFlush.Quitting("MyMod.TestAdapter OnApplicationQuit");
    private void OnDestroy() { ReviewClipFrames.AbortOnUnload(); ReviewState.RestoreOnUnload(); _registration?.Dispose(); _ownershipPatch?.UnpatchSelf(); }
}
