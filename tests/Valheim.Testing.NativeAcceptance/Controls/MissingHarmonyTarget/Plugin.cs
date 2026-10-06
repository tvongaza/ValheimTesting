using BepInEx;
using HarmonyLib;

namespace AcceptanceMod.Controls.MissingHarmonyTarget;

/// <summary>
/// Negative control for the Harmony census (#30) and the log scan (#26): a patch whose target method does not exist, as
/// after a game update renamed it. On the Valheim 1.0.16 Windows dedicated server (BepInEx 5.4.23.5, HarmonyX 2.9.0) the missing target
/// leaves one <c>accesstools-not-found</c> warning (<c>AccessTools.DeclaredMethod: Could not find method ...</c>) in the
/// server's BepInEx log, then <c>PatchAll</c> throws <c>ArgumentException: Undefined target method for patch method</c>,
/// which only Unity's own log (<c>-logFile</c>) records; the line after <c>PatchAll</c> is never logged (native run, 30 Sep
/// 2026: <c>controlPatchAllReturned</c> false). Both lines fail the teardown log scan by default. With
/// <c>"expectFailure": "missing-harmony-target"</c> the scenario requires the census to name the missing patch and the scan
/// of the server's BepInEx log to fail on the warning. Test runtimes only.
/// </summary>
[BepInPlugin(Guid, "AcceptanceMod control: missing Harmony target (ValheimTesting acceptance suite)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.acceptancemod.control.missingtarget";

    private void Awake()
    {
        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        // Logged only if PatchAll returned: its absence after the warning means PatchAll threw.
        Logger.LogInfo("MissingHarmonyTarget: PatchAll returned");
    }

    [HarmonyPatch(typeof(Player), "AcceptanceModControlMethodThatDoesNotExist")]
    private static class PatchMissingMethod
    {
        private static void Postfix() { }
    }
}
