using BepInEx;
using HarmonyLib;
#if PROBE_REVISION_B
using Revision = AcceptanceMod.Probes.PatchReload.RevisionB;
#else
using Revision = AcceptanceMod.Probes.PatchReload.RevisionA;
#endif

namespace AcceptanceMod.Probes.PatchReload;

/// <summary>
/// A script for ScriptEngine's <c>BepInEx/scripts</c>: in <c>Awake</c> it adds a postfix to <c>Terminal::InitTerminal</c>, the
/// method AcceptanceMod patches, under its own Harmony ID; when ScriptEngine unloads it (a reload or a removed file), <c>OnDestroy</c>
/// removes only that ID's patches (<c>UnpatchSelf</c>), as an adapter should. The census tells the loads apart by the patch
/// class: <see cref="RevisionA"/> or, built with <c>-p:ProbeRevision=B</c>, <see cref="RevisionB"/>. Built with
/// <c>-p:ProbeUnpatch=Other</c> it is the negative control: after its own it also removes AcceptanceMod's patches
/// (<c>Harmony.UnpatchID("valheimtesting.acceptancemod")</c>), patches it does not own. Not <c>Harmony.UnpatchAll()</c>: that removes ValheimCLI's patches too, and on the Valheim 1.0.16
/// Windows server no CLI command ran after it, so the run lost the census it reads (the teardown log scan's
/// <c>harmony-unpatch-all</c> failure is what names that case). Test runtimes only.
/// </summary>
[BepInPlugin(Guid, "AcceptanceMod probe: patch reload (ValheimTesting acceptance suite)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.acceptancemod.probe.patchreload";
    private Harmony? _harmony;

    private void Awake()
    {
        _harmony = new Harmony(Guid);
        _harmony.Patch(AccessTools.DeclaredMethod(typeof(Terminal), "InitTerminal"), postfix: new HarmonyMethod(typeof(Revision), nameof(Revision.Postfix)));
        Logger.LogInfo("PatchReload: patched Terminal::InitTerminal as " + typeof(Revision).Name);
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
#if PROBE_UNPATCH_OTHER
        Harmony.UnpatchID("valheimtesting.acceptancemod");
#endif
        Logger.LogInfo("PatchReload: unloaded " + typeof(Revision).Name);
    }
}

#if PROBE_REVISION_B
internal static class RevisionB
#else
internal static class RevisionA
#endif
{
    public static void Postfix() { }
}
