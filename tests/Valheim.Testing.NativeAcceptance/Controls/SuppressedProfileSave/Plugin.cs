using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace AcceptanceMod.Controls.SuppressedProfileSave;

/// <summary>
/// Negative control for the logout check (#35): the character file is never written, as when another mod removes every
/// Harmony patch at quit or breaks the save. It skips <c>PlayerProfile.Save</c>, so the game keeps the player's custom data
/// (AcceptanceMod's <c>acceptancemod.note</c> included) in memory but never on disk. The lifecycle-world scenario, with
/// <c>"expectFailure": "suppressed-profile-save"</c>, requires the logout check to find the file unchanged. Install it on
/// the client only, and only with a disposable character: nothing that character does while it is installed is saved.
/// </summary>
[BepInPlugin(Guid, "AcceptanceMod control: suppressed profile save (ValheimTesting acceptance suite)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.acceptancemod.control.suppressedsave";
    private static ManualLogSource _log = null!;

    private void Awake()
    {
        _log = Logger;
        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
    }

    [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.Save))]
    private static class SkipSave
    {
        private static bool Prefix(ref bool __result)
        {
            _log.LogWarning("Control: PlayerProfile.Save skipped; the character file is not written.");
            __result = false;
            return false;
        }
    }
}
