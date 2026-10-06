using System.Globalization;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using MyMod;
using UnityEngine;

namespace AcceptanceMod;

/// <summary>
/// The native acceptance suite's mod, the toolkit's own test subject. It adds one server command, <c>acceptancemod_mark &lt;x&gt; &lt;z&gt;</c>, that places a wooden pole as a
/// marker where the generator's ground is dry (see <see cref="DrySiteRule"/>) and refuses anywhere else. The marker is an
/// ordinary saved object, so it persists and replicates to clients that do not have this mod.
/// <para>
/// Small additions give the suite's scenarios something to observe, each kept apart: a version handshake
/// (<see cref="VersionHandshake"/>), one server-synced config entry (<see cref="SyncedGreeting"/>) and a custom-data key
/// on the player (<c>acceptancemod_note</c>, below). A client without AcceptanceMod still joins and sees the marker.
/// </para>
/// </summary>
[BepInPlugin(Guid, "AcceptanceMod (ValheimTesting acceptance suite)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.acceptancemod";
    public const string MarkerPrefab = "wood_pole2";
    /// <summary>The marker's saved label (a ZDO field): state kept with the object, so it survives unloads and restarts.</summary>
    public const string LabelKey = "acceptancemod_label", DryLabel = "dry-site";
    /// <summary>The player's custom-data key that <c>acceptancemod_note</c> writes; the game saves it with the character.</summary>
    public const string NoteKey = "acceptancemod.note";
    private const float WaterLevel = 30f, Clearance = 1.5f;
    internal static ManualLogSource Log = null!;

    private void Awake()
    {
        Log = Logger;
        SyncedGreeting.Bind(Config);
        Logger.LogInfo($"AcceptanceMod net version {VersionHandshake.NetVersion}");
        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
    }

    // The game rebuilds its command list when the terminal starts; add ours each time. InitTerminal is private in the
    // shipped game, so it is named as a string (a mod built against publicized assemblies could use nameof).
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    private static class RegisterCommands
    {
        private static void Postfix()
        {
            new Terminal.ConsoleCommand("acceptancemod_mark", "Mark a dry site with a pole, or refuse a wet one: acceptancemod_mark <x> <z>", args => Mark(args), isCheat: true);
            new Terminal.ConsoleCommand("acceptancemod_greeting", "Set the greeting the server sends every client: acceptancemod_greeting <word>", args => Greeting(args), isCheat: true);
            new Terminal.ConsoleCommand("acceptancemod_note", "Keep a note on your character: acceptancemod_note <word>", args => Note(args));
        }
    }

    private static void Mark(Terminal.ConsoleEventArgs args)
    {
        if (args.Length != 3 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        { args.Context.AddString("Usage: acceptancemod_mark <x> <z>"); return; }
        if (ZNet.instance == null || !ZNet.instance.IsServer() || WorldGenerator.instance == null || ZNetScene.instance == null)
        { args.Context.AddString("ERROR: acceptancemod_mark runs on the server of a loaded world"); return; }
        float ground = WorldGenerator.instance.GetHeight(x, z);
        string at = string.Format(CultureInfo.InvariantCulture, "{0} {1} ground={2:F2}", x, z, ground);
        if (!DrySiteRule.CanPlace(new Vector3(x, 0, z), WaterLevel, Clearance))
        { args.Context.AddString(string.Format(CultureInfo.InvariantCulture, "REFUSED: {0} below {1}", at, WaterLevel + Clearance)); return; }
        var prefab = ZNetScene.instance.GetPrefab(MarkerPrefab);
        if (prefab == null) { args.Context.AddString("ERROR: no prefab " + MarkerPrefab); return; }
        // Sunk a little into the ground, so the pole is supported where the terrain differs slightly from the generator.
        var marker = UnityEngine.Object.Instantiate(prefab, new Vector3(x, ground - 0.25f, z), Quaternion.identity);
        // Saved with the object: every client reads it, and it comes back whenever the object is created again.
        var view = marker.GetComponent<ZNetView>();
        if (view != null && view.GetZDO() != null) view.GetZDO().Set(LabelKey, DryLabel);
        args.Context.AddString("OK: marked " + at);
    }

    private static void Greeting(Terminal.ConsoleEventArgs args)
    {
        if (args.Length != 2) { args.Context.AddString("Usage: acceptancemod_greeting <word>"); return; }
        if (ZNet.instance == null || !ZNet.instance.IsServer()) { args.Context.AddString("ERROR: acceptancemod_greeting runs on the server"); return; }
        SyncedGreeting.Set(args[1]);
        args.Context.AddString("OK: greeting " + SyncedGreeting.Value);
    }

    // Mods keep per-character state in Player.m_customData; the game writes it into the character file when it saves the
    // player (at logout, among other times) and reads it back at the next spawn.
    private static void Note(Terminal.ConsoleEventArgs args)
    {
        if (args.Length != 2) { args.Context.AddString("Usage: acceptancemod_note <word>"); return; }
        if (Player.m_localPlayer == null) { args.Context.AddString("ERROR: acceptancemod_note needs a spawned local player"); return; }
        Player.m_localPlayer.m_customData[NoteKey] = args[1];
        args.Context.AddString("OK: note " + args[1]);
    }
}
