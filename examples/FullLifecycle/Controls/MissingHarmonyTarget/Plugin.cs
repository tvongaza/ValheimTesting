using BepInEx;
using HarmonyLib;

namespace MyMod.Controls.MissingHarmonyTarget;

/// <summary>
/// Negative control for the Harmony census (#30) and the log scan (#26): a patch whose target method does not exist, as
/// after a game update renamed it. HarmonyX refuses it ("Undefined target method for patch method") and <c>PatchAll</c>
/// throws, so nothing of this plugin is patched, the error is in the log, and the plugin still counts as loaded. The
/// lifecycle-world scenario, with <c>"expectFailure": "missing-harmony-target"</c>, requires the census to name the
/// missing patch and the server's log scan to find the error. Test runtimes only.
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
