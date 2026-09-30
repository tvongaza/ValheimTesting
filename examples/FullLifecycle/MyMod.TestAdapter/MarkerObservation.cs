using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace MyMod.TestAdapter;

/// <summary>
/// The mod's own observation, <c>markers &lt;x&gt; &lt;z&gt; [radius]</c>: the markers (<c>wood_pole2</c> saved objects)
/// this process knows within <c>radius</c> metres (default 1.5, at most 64) of a point, each with the label MyMod saves on
/// it (<c>mymod_label</c>) and whether it has an instance here. It reads saved data only, through the adapter's
/// <see cref="ZoneTerrain.ZoneObjects"/>, never MyMod's types, so it also runs where MyMod is absent. Read-only.
/// </summary>
internal static class MarkerObservation
{
    public const string Source = "mymod-markers", Marker = "wood_pole2", LabelKey = "mymod_label";
    public const float MaxRadius = 64f;

    public static ExtensionCommand Command() =>
        new ExtensionCommand("markers", "List markers near a point with their saved label: <x> <z> [radius]", Run, readOnly: true, needsWorld: true);

    private static IEnumerator Run(ExtensionContext context)
    {
        var arguments = context.Arguments;
        float x = 0f, z = 0f, radius = 1.5f;
        if (arguments.Count < 2 || arguments.Count > 3 || !Number(arguments[0], out x) || !Number(arguments[1], out z) ||
            (arguments.Count == 3 && (!Number(arguments[2], out radius) || !(radius > 0f && radius <= MaxRadius))))
        { context.Fail("usage", "markers <x> <z> [radius], 0 < radius <= " + MaxRadius.ToString(CultureInfo.InvariantCulture)); yield break; }
        if (ZNetScene.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null) { context.Fail("no_world", "No loaded world."); yield break; }
        int hash = Marker.GetStableHashCode();
        var markers = new List<object?>();
        Vector2s min = ZoneSystem.GetZone(new Vector3(x - radius, 0f, z - radius));
        Vector2s max = ZoneSystem.GetZone(new Vector3(x + radius, 0f, z + radius));
        for (int zx = min.x; zx <= max.x; zx++)
            for (int zz = min.y; zz <= max.y; zz++)
                foreach (ZDO zdo in ZoneTerrain.ZoneObjects(new Vector2s(zx, zz), 50000, candidate => candidate.GetPrefab() == hash))
                {
                    Vector3 at = zdo.GetPosition();
                    float dx = at.x - x, dz = at.z - z;
                    if (dx * dx + dz * dz > radius * radius) continue;
                    markers.Add(new Dictionary<string, object?>
                    {
                        ["x"] = at.x, ["y"] = at.y, ["z"] = at.z, ["label"] = zdo.GetString(LabelKey, ""),
                        ["instance"] = ZNetScene.instance.FindInstance(zdo) != null,
                    });
                }
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = Source, ["complete"] = true, ["x"] = x, ["z"] = z, ["radius"] = radius, ["markers"] = markers.ToArray(),
        });
    }

    private static bool Number(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
}
