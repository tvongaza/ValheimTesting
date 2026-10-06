using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace AcceptanceMod.Adapter;

/// <summary>
/// The ghost-protection scenario's game-side half (#261). An AI's target is not replicated (its ZDO carries only
/// <c>haveTarget</c>), so whom a creature targets can be read only in the process that owns, and so simulates, it.
/// <list type="bullet">
/// <item><c>ai-watch &lt;x&gt; &lt;z&gt; &lt;radius&gt; &lt;seconds&gt;</c> (read-only): every player this process knows, with whether
/// it reads that player as in ghost mode, and every creature within <c>radius</c> of the point, with its ZDO owner, whether
/// this process owned it at every reading (<c>ownedHere</c>) and every target it chose here during the window (sampled four
/// times a second; 0 seconds is one reading).</item>
/// <item><c>creature-spawn &lt;prefab&gt; &lt;x&gt; &lt;z&gt;</c> (client): one creature (a prefab with an AI) on the ground there,
/// created and so owned by this client.</item>
/// <item><c>creature-remove &lt;zdo&gt;</c> (client): removes a creature this client owns.</item>
/// <item><c>ghost-mode &lt;on|off&gt;</c> (client): the local player's ghost mode, as the game's <c>ghost</c> cheat sets it,
/// which a joined client cannot run without being the server's admin.</item>
/// </list>
/// </summary>
internal static class AiWatch
{
    public const string Source = "acceptancemod-ai-watch";
    private const float MaxRadius = 64f, MaxSeconds = 60f, SampleSeconds = 0.25f;

    internal static ExtensionCommand WatchCommand() => new("ai-watch", "Players and creature targets near a point over a window: <x> <z> <radius> <seconds>",
        Watch, readOnly: true, needsWorld: true);
    internal static ExtensionCommand SpawnCommand() => new("creature-spawn", "Spawn one creature owned by this client: <prefab> <x> <z>",
        Spawn, role: ExtensionRole.Client, needsWorld: true);
    internal static ExtensionCommand RemoveCommand() => new("creature-remove", "Remove a creature this client owns: <zdo>",
        Remove, role: ExtensionRole.Client, needsWorld: true);
    internal static ExtensionCommand GhostCommand() => new("ghost-mode", "Set the local player's ghost mode: <on|off>",
        Ghost, role: ExtensionRole.Client, needsWorld: true);

    private static IEnumerator Watch(ExtensionContext context)
    {
        var a = context.Arguments;
        if (a.Count != 4 || !Number(a[0], out float x) || !Number(a[1], out float z) || !Number(a[2], out float radius) || !Number(a[3], out float seconds) ||
            radius <= 0f || radius > MaxRadius || seconds < 0f || seconds > MaxSeconds)
        { context.Fail("usage", $"ai-watch <x> <z> <radius 0..{MaxRadius}> <seconds 0..{MaxSeconds}>"); yield break; }
        var creatures = new Dictionary<ZDOID, Dictionary<string, object?>>();
        var seen = new Dictionary<ZDOID, List<string>>();
        float start = Time.realtimeSinceStartup;
        while (true)
        {
            Sample(x, z, radius, creatures, seen, Time.realtimeSinceStartup - start);
            if (context.Cancelled) { context.Fail("cancelled", "The watch was cancelled."); yield break; }
            if (Time.realtimeSinceStartup - start >= seconds) break;
            float next = Time.realtimeSinceStartup + SampleSeconds;
            while (Time.realtimeSinceStartup < next && !context.Cancelled) yield return null;
        }
        foreach (var pair in creatures) pair.Value["targets"] = seen[pair.Key].ToArray();
        var players = new List<object?>();
        foreach (Player player in Player.GetAllPlayers())
            players.Add(new Dictionary<string, object?>
            {
                ["name"] = player.GetPlayerName(), ["local"] = player == Player.m_localPlayer, ["ghost"] = player.InGhostMode(),
                ["debugFlying"] = player.IsDebugFlying(), ["health"] = player.GetHealth(),
                ["x"] = player.transform.position.x, ["z"] = player.transform.position.z,
            });
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = Source, ["complete"] = true, ["self"] = ZDOMan.GetSessionID().ToString(CultureInfo.InvariantCulture),
            ["x"] = x, ["z"] = z, ["radius"] = radius, ["seconds"] = Time.realtimeSinceStartup - start,
            ["players"] = players.ToArray(), ["creatures"] = new List<object?>(creatures.Values).ToArray(),
        });
    }

    // One reading: each creature in range (its first reading's place is kept) and the target it has now, where this process owns it.
    private static void Sample(float x, float z, float radius, Dictionary<ZDOID, Dictionary<string, object?>> creatures,
        Dictionary<ZDOID, List<string>> seen, float at)
    {
        foreach (BaseAI ai in BaseAI.GetAllInstances())
        {
            var view = ai.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) continue;
            Vector3 position = ai.transform.position;
            if ((position.x - x) * (position.x - x) + (position.z - z) * (position.z - z) > radius * radius) continue;
            ZDO zdo = view.GetZDO();
            long owner = zdo.GetOwner(), self = ZDOMan.GetSessionID();
            if (!creatures.ContainsKey(zdo.m_uid))
            {
                creatures[zdo.m_uid] = new Dictionary<string, object?>
                {
                    ["zdo"] = zdo.m_uid.ToString(), ["prefab"] = PrefabName(zdo), ["owner"] = owner.ToString(CultureInfo.InvariantCulture),
                    ["ownedHere"] = owner != 0 && owner == self && view.IsOwner(), ["x"] = position.x, ["z"] = position.z,
                };
                seen[zdo.m_uid] = new List<string>();
            }
            // Only the owner chooses targets; a reading elsewhere leaves the window's targets incomplete, so it is owned here no longer.
            if (!view.IsOwner()) { creatures[zdo.m_uid]["ownedHere"] = false; continue; }
            Character? target = ai.GetTargetCreature();
            if (target == null) continue;
            string name = target is Player player ? "player:" + player.GetPlayerName() : "creature:" + PrefabName(target.GetComponent<ZNetView>()?.GetZDO());
            if (!seen[zdo.m_uid].Contains(name))
            {
                seen[zdo.m_uid].Add(name);
                creatures[zdo.m_uid]["firstTarget" + seen[zdo.m_uid].Count] = at;
            }
        }
    }

    private static IEnumerator Spawn(ExtensionContext context)
    {
        var a = context.Arguments;
        if (a.Count != 3 || !Number(a[1], out float x) || !Number(a[2], out float z)) { context.Fail("usage", "creature-spawn <prefab> <x> <z>"); yield break; }
        GameObject prefab = ZNetScene.instance.GetPrefab(a[0]);
        if (prefab == null || prefab.GetComponent<BaseAI>() == null) { context.Fail("prefab", a[0] + " is not a creature prefab."); yield break; }
        float ground = ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z));
        GameObject spawned = Object.Instantiate(prefab, new Vector3(x, ground + 0.5f, z), Quaternion.identity);
        var view = spawned.GetComponent<ZNetView>();
        if (view == null || !view.IsValid()) { context.Fail("spawn", "The creature has no network view."); yield break; }
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = Source, ["complete"] = true, ["zdo"] = view.GetZDO().m_uid.ToString(), ["prefab"] = a[0],
            ["owner"] = view.GetZDO().GetOwner().ToString(CultureInfo.InvariantCulture), ["ownedHere"] = view.IsOwner(),
            ["x"] = x, ["z"] = z, ["y"] = ground + 0.5f,
        });
    }

    private static IEnumerator Remove(ExtensionContext context)
    {
        if (context.Arguments.Count != 1) { context.Fail("usage", "creature-remove <zdo>"); yield break; }
        foreach (BaseAI ai in BaseAI.GetAllInstances())
        {
            var view = ai.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || view.GetZDO().m_uid.ToString() != context.Arguments[0]) continue;
            if (!view.IsOwner()) { context.Fail("not_owner", "This client does not own " + context.Arguments[0] + "."); yield break; }
            ZNetScene.instance.Destroy(ai.gameObject);
            context.Succeed(new Dictionary<string, object?> { ["source"] = Source, ["complete"] = true, ["zdo"] = context.Arguments[0], ["removed"] = true });
            yield break;
        }
        context.Fail("absent", "No loaded creature " + context.Arguments[0] + ".");
    }

    private static IEnumerator Ghost(ExtensionContext context)
    {
        if (context.Arguments.Count != 1 || context.Arguments[0] is not ("on" or "off")) { context.Fail("usage", "ghost-mode <on|off>"); yield break; }
        if (Player.m_localPlayer == null) { context.Fail("no_player", "No local player."); yield break; }
        Player.m_localPlayer.SetGhostMode(context.Arguments[0] == "on");
        context.Succeed(new Dictionary<string, object?> { ["source"] = Source, ["complete"] = true, ["ghost"] = Player.m_localPlayer.InGhostMode() });
    }

    private static string PrefabName(ZDO? zdo) =>
        zdo == null ? "" : ZNetScene.instance.GetPrefab(zdo.GetPrefab()) is GameObject prefab ? prefab.name : zdo.GetPrefab().ToString(CultureInfo.InvariantCulture);

    private static bool Number(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
}
