using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MyMod;

/// <summary>
/// The content MyMod registers, for the content census (#91/#114): one item, <c>MyMod_SurveyStake</c> (a copy of the game's
/// <c>Wood</c> under its own name), registered in ObjectDB as an item and in ZNetScene as the network prefab a dropped
/// stake needs, and one recipe, <c>Recipe_MyMod_SurveyStake</c>, crafting it at the workbench from two wood. Nothing
/// spawns it; a client without MyMod never meets it.
/// <para>
/// Each world load builds new registries, so each is filled when the world scene wakes: the item and recipe in a postfix
/// on <c>ObjectDB.Awake</c> (the main menu's database wakes empty and is filled later by <c>CopyOtherDB</c>, so an empty
/// one is skipped), the prefab in a postfix on <c>ZNetScene.Awake</c>. The workbench and the wood come from the database
/// being filled, never from another scene's.
/// </para>
/// <para>
/// Negative-control builds leave out the recipe or status effect; each census must fail for exactly that omission.
/// </para>
/// </summary>
internal static class Content
{
    public const string ItemName = "MyMod_SurveyStake", RecipeName = "Recipe_MyMod_SurveyStake";
    public const string PieceName = "MyMod_SurveyPost", StatusName = "MyMod_SurveyBlessing";
    private const string Source = "Wood", Workbench = "piece_workbench", Hammer = "Hammer", WoodWall = "wood_wall";
    private static GameObject? _prefab;
    private static GameObject? _piece;
#if !MYMOD_OMIT_STATUS_EFFECT
    private static StatusEffect? _status;
#endif

    // One copy for the whole session, kept under an inactive holder so the copy itself never wakes as a scene object.
    private static GameObject? Prefab(GameObject? original)
    {
        if (_prefab != null) return _prefab;
        if (original == null) { Plugin.Log.LogError($"MyMod content: no {Source} prefab to copy"); return null; }
        var holder = new GameObject("MyMod content");
        holder.SetActive(false);
        Object.DontDestroyOnLoad(holder);
        var prefab = Object.Instantiate(original, holder.transform);
        prefab.name = ItemName;
        var drop = prefab.GetComponent<ItemDrop>();
        if (drop != null) drop.m_itemData.m_shared.m_name = "MyMod survey stake";
        return _prefab = prefab;
    }

    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    private static class RegisterItem
    {
        private static void Postfix(ObjectDB __instance)
        {
            if (__instance.m_items.Count == 0) return; // The main menu's database, before CopyOtherDB fills it.
            var prefab = Prefab(__instance.GetItemPrefab(Source));
            if (prefab == null) return;
            if (!__instance.m_items.Any(item => item != null && item.name == ItemName))
            {
                __instance.m_items.Add(prefab);
                // The index (private in the shipped game) is built in Awake; build it again so lookups find the item.
                var reindex = AccessTools.Method(typeof(ObjectDB), "UpdateRegisters");
                if (reindex == null) { Plugin.Log.LogError("MyMod content: ObjectDB.UpdateRegisters not found"); return; }
                reindex.Invoke(__instance, null);
            }
#if !MYMOD_OMIT_STATUS_EFFECT
            if (!__instance.m_StatusEffects.Any(effect => effect != null && effect.name == StatusName))
            {
                _status ??= ScriptableObject.CreateInstance<StatusEffect>();
                _status.name = StatusName;
                _status.m_name = "MyMod survey blessing";
                __instance.m_StatusEffects.Add(_status);
            }
#endif
#if !MYMOD_OMIT_RECIPE
            if (__instance.m_recipes.Any(existing => existing != null && existing.name == RecipeName)) return;
            // The workbench as the game's own recipes reference it.
            var station = __instance.m_recipes.Select(existing => existing == null ? null : existing.m_craftingStation)
                .FirstOrDefault(candidate => candidate != null && candidate.name == Workbench);
            var wood = __instance.GetItemPrefab(Source);
            if (station == null || wood == null) { Plugin.Log.LogError($"MyMod content: no {Workbench} recipe or no {Source} item to build the recipe from"); return; }
            var recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = RecipeName;
            recipe.m_item = prefab.GetComponent<ItemDrop>();
            recipe.m_amount = 1;
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = 1;
            recipe.m_resources = new[] { new Piece.Requirement { m_resItem = wood.GetComponent<ItemDrop>(), m_amount = 2 } };
            __instance.m_recipes.Add(recipe);
#endif
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    private static class RegisterPrefab
    {
        private static void Postfix(ZNetScene __instance)
        {
            var prefab = Prefab(__instance.GetPrefab(Source));
            // The scene resolves saved objects' hashes here (private in the shipped game).
            var named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(__instance);
            if (prefab != null && !__instance.m_prefabs.Contains(prefab))
            {
                __instance.m_prefabs.Add(prefab);
                named[ItemName.GetStableHashCode()] = prefab;
            }
            var database = ObjectDB.instance;
            var tool = database?.GetItemPrefab(Hammer)?.GetComponent<ItemDrop>();
            var table = tool?.m_itemData?.m_shared?.m_buildPieces;
            var wood = database?.GetItemPrefab(Source)?.GetComponent<ItemDrop>();
            var original = __instance.GetPrefab(WoodWall);
            if (table == null || wood == null || original == null)
            {
                Plugin.Log.LogError($"MyMod content: no {Hammer} build table, {Source} item or {WoodWall} prefab for the survey post");
                return;
            }
            if (_piece == null)
            {
                var holder = new GameObject("MyMod piece content");
                holder.SetActive(false);
                Object.DontDestroyOnLoad(holder);
                _piece = Object.Instantiate(original, holder.transform);
                _piece.name = PieceName;
                var component = _piece.GetComponent<Piece>();
                if (component == null) { Plugin.Log.LogError("MyMod content: source has no Piece component"); return; }
                component.m_name = "MyMod survey post";
                component.m_craftingStation = null;
                component.m_resources = new[] { new Piece.Requirement { m_resItem = wood, m_amount = 2 } };
            }
            if (!table.m_pieces.Contains(_piece)) table.m_pieces.Add(_piece);
            if (!__instance.m_prefabs.Contains(_piece)) __instance.m_prefabs.Add(_piece);
            named[PieceName.GetStableHashCode()] = _piece;
        }
    }
}
