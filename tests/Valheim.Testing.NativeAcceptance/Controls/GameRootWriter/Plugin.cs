using System;
using System.IO;
using BepInEx;

namespace AcceptanceMod.Controls.GameRootWriter;

/// <summary>
/// Native control for #612: on load it writes <c>vt612-game-root-&lt;id&gt;.txt</c> into BepInEx's game root
/// (<c>Paths.GameRootPath</c>, the folder of the executable that ran, where a preloader failure log goes), and, as the
/// owned-storage control, <c>vt612-config-&lt;id&gt;.txt</c> into BepInEx's config folder and <c>vt612-cwd-&lt;id&gt;.txt</c>
/// into the working directory. It logs each path it wrote. A run with it must leave the source install unchanged: on Windows and
/// Linux the game root is the profile; on macOS the source check moves the game-root file into the run's evidence and names it.
/// </summary>
[BepInPlugin(Guid, "AcceptanceMod control: game-root writer (ValheimTesting acceptance suite)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.acceptancemod.control.gamerootwriter";

    private void Awake()
    {
        string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.GetHashCode().ToString("x8");
        Write(Paths.GameRootPath, "game-root");
        Write(Paths.ConfigPath, "config");
        Write(Directory.GetCurrentDirectory(), "cwd");

        void Write(string folder, string kind)
        {
            string path = Path.Combine(folder, $"vt612-{kind}-{id}.txt");
            try
            {
                File.WriteAllText(path, $"written by {Guid} into {kind}\n");
                Logger.LogInfo($"vt612 wrote {kind}: {path}");
            }
            catch (Exception error) { Logger.LogWarning($"vt612 could not write {kind}: {path}: {error.Message}"); }
        }
    }
}
