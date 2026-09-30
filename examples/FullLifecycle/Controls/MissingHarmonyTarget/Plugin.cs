using BepInEx;
using HarmonyLib;

namespace MyMod.Controls.MissingHarmonyTarget;

/// <summary>
/// Negative control for the Harmony census (#30) and the log scan (#26): a patch whose target method does not exist, as
/// after a game update renamed it. On the Valheim 1.0.16 dedicated server (BepInEx 5.4.23.5, HarmonyX 2.9.0) the missing
/// target showed up as one <c>accesstools-not-found</c> warning (<c>AccessTools.DeclaredMethod: Could not find method ...</c>)
/// and no error line in the server's BepInEx log; the census, not the log scan, is what catches it. Whether
/// <c>PatchAll</c> returned or threw after that warning is what the line logged after it tells: the lifecycle-world scenario
/// records it as <c>controlPatchAllReturned</c>. With <c>"expectFailure": "missing-harmony-target"</c> the scenario requires
/// the census to name the missing patch and that warning in the server's log. Test runtimes only.
/// </summary>
[BepInPlugin(Guid, "MyMod control: missing Harmony target (ValheimTesting example)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "example.mymod.control.missingtarget";

    private void Awake()
    {
        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        // Logged only if PatchAll returned: its absence after the warning means PatchAll threw.
        Logger.LogInfo("MissingHarmonyTarget: PatchAll returned");
    }

    [HarmonyPatch(typeof(Player), "MyModControlMethodThatDoesNotExist")]
    private static class PatchMissingMethod
    {
        private static void Postfix() { }
    }
}
