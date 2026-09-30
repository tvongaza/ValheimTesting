using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace MyMod.Controls.ServerOnlyPrefab;

/// <summary>
/// Negative control for the vanilla-client check (#33): a prefab that only this process registers, a copy of the
/// marker's <c>wood_pole2</c> under its own name, and a server command that spawns one,
/// <c>mymodcontrol_spawn &lt;x&gt; &lt;z&gt;</c>. A client without this plugin cannot create the object: the game logs
/// "Missing prefab hash" and skips it. The vanilla-client scenario, with <c>"expectFailure": "server-only-prefab"</c>,
/// spawns one beside the dry site and requires the client's census to name its hash. Install it on the server only.
/// </summary>
[BepInPlugin(Guid, "MyMod control: server-only prefab (ValheimTesting example)", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "example.mymod.control.serveronlyprefab";
    public const string PrefabName = "MyModControl_ServerOnly";
    private static GameObject? _prefab;

    private void Awake() => new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

    // Every world's scene registers its prefabs anew: add ours, a copy of wood_pole2 kept under an inactive holder so the
    // copy itself never becomes a networked object.
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    private static class Register
    {
        private static void Postfix(ZNetScene __instance)
        {
            var prefab = _prefab;
            if (prefab == null)
            {
                var original = __instance.GetPrefab("wood_pole2");
                if (original == null) return;
                var holder = new GameObject("MyModControl prefabs");
                holder.SetActive(false);
                Object.DontDestroyOnLoad(holder);
                prefab = _prefab = Object.Instantiate(original, holder.transform);
                prefab.name = PrefabName;
            }
            __instance.m_prefabs.Add(prefab);
            // The scene resolves saved objects' hashes here (private in the shipped game).
            var named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(__instance);
            named[PrefabName.GetStableHashCode()] = prefab;
        }
    }

    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    private static class RegisterCommands
    {
        private static void Postfix() =>
            new Terminal.ConsoleCommand("mymodcontrol_spawn", "Spawn the server-only control object: mymodcontrol_spawn <x> <z>", args => Spawn(args), isCheat: true);
    }

    private static void Spawn(Terminal.ConsoleEventArgs args)
    {
        if (args.Length != 3 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        { args.Context.AddString("Usage: mymodcontrol_spawn <x> <z>"); return; }
        if (ZNet.instance == null || !ZNet.instance.IsServer() || WorldGenerator.instance == null || _prefab == null)
        { args.Context.AddString("ERROR: mymodcontrol_spawn runs on the server of a loaded world"); return; }
        float ground = WorldGenerator.instance.GetHeight(x, z);
        Object.Instantiate(_prefab, new Vector3(x, ground - 0.25f, z), Quaternion.identity);
        args.Context.AddString(string.Format(CultureInfo.InvariantCulture, "OK: spawned {0} at {1} {2}", PrefabName, x, z));
    }
}
