using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace AcceptanceMod.Adapter;

/// <summary>
/// The suite's ownership probe for the two-client scenario. A command takes one snapshot, or subscribes to
/// ZDO owner changes and completes on the relevant change. The runner never repeatedly queries ValheimCLI.
/// </summary>
internal static class MarkerOwnership
{
    private const string Source = "acceptancemod-marker-owner";
    private static event Action<ZDOID>? Changed;

    internal static void Patch(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwnerInternal)),
        postfix: new HarmonyMethod(typeof(MarkerOwnership), nameof(OwnerChanged)));

    private static void OwnerChanged(ZDO __instance) => Changed?.Invoke(__instance.m_uid);

    internal static ExtensionCommand SnapshotCommand() => new("marker-owner", "Read one marker's owner: <x> <z>",
        Snapshot, readOnly: true, needsWorld: true);
    internal static ExtensionCommand WaitCommand() => new("marker-owner-wait", "Wait for one marker owner change: <x> <z> <self|other|none|session-id> [seconds]",
        Wait, readOnly: true, needsWorld: true);
    internal static ExtensionCommand ClaimCommand() => new("marker-owner-claim", "Claim the loaded marker for this test client: <x> <z>",
        Claim, role: ExtensionRole.Client, needsWorld: true);

    private static IEnumerator Snapshot(ExtensionContext context)
    {
        if (context.Arguments.Count != 2) { context.Fail("usage", "marker-owner <x> <z>"); yield break; }
        if (!Point(context, 2, out float x, out float z)) yield break;
        if (!TryFind(x, z, out var marker, out string error)) { context.Fail("marker", error); yield break; }
        context.Succeed(Reading(marker!));
    }

    private static IEnumerator Claim(ExtensionContext context)
    {
        if (context.Arguments.Count != 2) { context.Fail("usage", "marker-owner-claim <x> <z>"); yield break; }
        if (!Point(context, 2, out float x, out float z)) yield break;
        if (!TryFind(x, z, out var marker, out string error)) { context.Fail("marker", error); yield break; }
        var instance = ZNetScene.instance.FindInstance(marker!);
        if (instance == null) { context.Fail("not_loaded", "The marker has no loaded instance on this client."); yield break; }
        instance.ClaimOwnership(); // One explicit, test-only action; never retried after an uncertain reply.
        context.Succeed(Reading(marker!));
    }

    private static IEnumerator Wait(ExtensionContext context)
    {
        if (!Point(context, 3, out float x, out float z)) yield break;
        string expected = context.Arguments[2];
        if (expected is not ("self" or "other" or "none") && !long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        { context.Fail("usage", "Expected owner is self, other, none or a session ID."); yield break; }
        float seconds = 30f;
        if (context.Arguments.Count == 4 && (!float.TryParse(context.Arguments[3], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
            !Finite(seconds) || seconds < 1f || seconds > 120f))
        { context.Fail("usage", "Wait 1 to 120 seconds."); yield break; }
        if (!TryFind(x, z, out var marker, out string error)) { context.Fail("marker", error); yield break; }
        ZDOID id = marker!.m_uid;
        bool changed = false;
        void Notify(ZDOID eventId) { if (eventId == id) changed = true; }
        Changed += Notify;
        try
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            changed = true; // A snapshot after subscribing closes the snapshot/subscription gap.
            while (!context.Cancelled && Time.realtimeSinceStartup < deadline)
            {
                if (changed)
                {
                    changed = false;
                    if (Matches(marker, expected)) { context.Succeed(Reading(marker)); yield break; }
                }
                yield return null; // Only the deadline/notification flag is checked on frames, not the world.
            }
            context.Fail(context.Cancelled ? "cancelled" : "timeout", "The marker did not report owner " + expected + ".");
        }
        finally { Changed -= Notify; }
    }

    private static bool Point(ExtensionContext context, int minimum, out float x, out float z)
    {
        x = z = 0;
        if (context.Arguments.Count < minimum || context.Arguments.Count > minimum + 1 ||
            !float.TryParse(context.Arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !Finite(x) ||
            !float.TryParse(context.Arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z) || !Finite(z))
        { context.Fail("usage", "Give finite marker x and z coordinates."); return false; }
        return true;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool TryFind(float x, float z, out ZDO? marker, out string error)
    {
        marker = null; error = "";
        if (ZDOMan.instance == null || ZNetScene.instance == null || ZoneSystem.instance == null)
        { error = "No loaded world."; return false; }
        var min = ZoneSystem.GetZone(new Vector3(x - 1.5f, 0f, z - 1.5f));
        var max = ZoneSystem.GetZone(new Vector3(x + 1.5f, 0f, z + 1.5f));
        for (int zx = min.x; zx <= max.x; zx++)
            for (int zz = min.y; zz <= max.y; zz++)
                foreach (ZDO candidate in SavedObjects.InZone(new Vector2s(zx, zz), 50000, zdo => zdo.GetPrefab() == MarkerObservation.Marker.GetStableHashCode()))
                {
                    Vector3 at = candidate.GetPosition();
                    if ((at.x - x) * (at.x - x) + (at.z - z) * (at.z - z) > 2.25f ||
                        candidate.GetString(MarkerObservation.LabelKey, "") != "dry-site") continue;
                    if (marker != null) { error = "More than one labelled marker is at the site."; return false; }
                    marker = candidate;
                }
        if (marker == null) { error = "The labelled marker is absent at the site."; return false; }
        return true;
    }

    private static bool Matches(ZDO marker, string expected)
    {
        long owner = marker.GetOwner(), self = ZDOMan.GetSessionID();
        return expected switch { "self" => owner != 0 && owner == self, "other" => owner != 0 && owner != self, "none" => owner == 0,
            _ => long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && owner == id };
    }

    private static Dictionary<string, object?> Reading(ZDO marker)
    {
        long owner = marker.GetOwner(), self = ZDOMan.GetSessionID();
        return new Dictionary<string, object?>
        {
            ["source"] = Source, ["complete"] = true, ["id"] = marker.m_uid.ToString(),
            ["owner"] = owner.ToString(CultureInfo.InvariantCulture), ["self"] = self.ToString(CultureInfo.InvariantCulture),
            ["ownedHere"] = owner != 0 && owner == self,
            ["instance"] = ZNetScene.instance.FindInstance(marker) != null,
        };
    }
}
