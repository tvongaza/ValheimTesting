using BepInEx;
using HarmonyLib;

namespace MyMod.Controls.MissingHarmonyTarget;

/// <summary>
/// Negative control for the Harmony census (#30) and the log scan (#26): a patch whose target method does not exist, as
/// after a game update renamed it. In the game (1.0.16, BepInEx 5.4.23.5, HarmonyX 2.9.0) HarmonyX skips it with one
/// warning (<c>AccessTools.DeclaredMethod: Could not find method ...</c>); <c>PatchAll</c> does not throw and the plugin
/// loads. The lifecycle-world scenario, with <c>"expectFailure": "missing-harmony-target"</c>, requires the census to name
/// the missing patch and that warning in the server's log. Test runtimes only.
/// </summary>
[BepInPlugin(Guid, "MyMod control: missing Harmony target (ValheimTesting example)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "example.mymod.control.missingtarget";

    private void Awake() => new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

    [HarmonyPatch(typeof(Player), "MyModControlMethodThatDoesNotExist")]
    private static class PatchMissingMethod
    {
        private static void Postfix() { }
    }
}
