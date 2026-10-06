using BepInEx;

namespace AcceptanceMod.Probes.QuitLog;

/// <summary>
/// Logs one warning when Unity tells the plugin the game is quitting (<c>OnApplicationQuit</c>) and one when its object is
/// destroyed during the shutdown (<c>OnDestroy</c>), each with the session's world if one is loaded. After an owned process
/// quits cleanly, the teardown log scan counts both as unknown warnings (the first is the scan's first line); after a kill
/// neither is written. Test runtimes only.
/// </summary>
[BepInPlugin(Guid, "AcceptanceMod probe: quit log (ValheimTesting acceptance suite)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.acceptancemod.probe.quitlog";

    private void OnApplicationQuit() => Logger.LogWarning("QuitLog: OnApplicationQuit (" + World() + ")");
    private void OnDestroy() => Logger.LogWarning("QuitLog: OnDestroy (" + World() + ")");

    private static string World() => ZNet.instance != null && ZNet.World != null ? "world " + ZNet.World.m_name : "no world";
}
