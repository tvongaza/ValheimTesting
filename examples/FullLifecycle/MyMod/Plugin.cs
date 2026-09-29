using System.Globalization;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace MyMod;

/// <summary>
/// The example mod. It adds one server command, <c>mymod_mark &lt;x&gt; &lt;z&gt;</c>, that places a wooden pole as a
/// marker where the generator's ground is dry (see <see cref="DrySiteRule"/>) and refuses anywhere else. The marker is an
/// ordinary saved object, so it persists and replicates to clients that do not have this mod.
/// </summary>
[BepInPlugin(Guid, "MyMod (ValheimTesting example)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "example.mymod";
    public const string MarkerPrefab = "wood_pole2";
    private const float WaterLevel = 30f, Clearance = 1.5f;

    private void Awake() => new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

    // The game rebuilds its command list when the terminal starts; add ours each time. InitTerminal is private in the
    // shipped game, so it is named as a string (a mod built against publicized assemblies could use nameof).
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    private static class RegisterCommands
    {
        private static void Postfix() => new Terminal.ConsoleCommand("mymod_mark",
            "Mark a dry site with a pole, or refuse a wet one: mymod_mark <x> <z>", args => Mark(args), isCheat: true);
    }

    private static void Mark(Terminal.ConsoleEventArgs args)
    {
        if (args.Length != 3 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        { args.Context.AddString("Usage: mymod_mark <x> <z>"); return; }
        if (ZNet.instance == null || !ZNet.instance.IsServer() || WorldGenerator.instance == null || ZNetScene.instance == null)
        { args.Context.AddString("ERROR: mymod_mark runs on the server of a loaded world"); return; }
        float ground = WorldGenerator.instance.GetHeight(x, z);
        string at = string.Format(CultureInfo.InvariantCulture, "{0} {1} ground={2:F2}", x, z, ground);
        if (!DrySiteRule.CanPlace(new Vector3(x, 0, z), WaterLevel, Clearance))
        { args.Context.AddString(string.Format(CultureInfo.InvariantCulture, "REFUSED: {0} below {1}", at, WaterLevel + Clearance)); return; }
        var prefab = ZNetScene.instance.GetPrefab(MarkerPrefab);
        if (prefab == null) { args.Context.AddString("ERROR: no prefab " + MarkerPrefab); return; }
        // Sunk a little into the ground, so the pole is supported where the terrain differs slightly from the generator.
        UnityEngine.Object.Instantiate(prefab, new Vector3(x, ground - 0.25f, z), Quaternion.identity);
        args.Context.AddString("OK: marked " + at);
    }
}
