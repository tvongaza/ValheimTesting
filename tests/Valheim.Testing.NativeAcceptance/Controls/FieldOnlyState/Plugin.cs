using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using UnityEngine;
using valheimCLI;
using valheimCLI.Extensions;

namespace AcceptanceMod.Controls.FieldOnlyState;

/// <summary>
/// Negative control for the zone cycle (#35): a value kept only in a component field on AcceptanceMod's marker, the mistake the
/// zone cycle exists to catch. When the player leaves the area the game destroys the marker's instance, and on return it
/// creates a new one from the saved data, so the field starts empty again (AcceptanceMod's own label is saved with the object and
/// comes back). Two client extension commands under <c>acceptancemodcontrol.fieldstate</c>: <c>set &lt;x&gt; &lt;z&gt;
/// &lt;value&gt;</c> puts the value on the markers near a point, <c>read &lt;x&gt; &lt;z&gt;</c> reads it back. The
/// lifecycle-world scenario, with <c>"expectFailure": "field-only-state"</c>, sets it before the zone cycle and requires it
/// to be gone after. Install it on the client only.
/// </summary>
[BepInPlugin(Guid, "AcceptanceMod control: field-only state (ValheimTesting acceptance suite)", "0.1.0")]
[BepInDependency("valheimCLI.valheimCLI")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.acceptancemod.control.fieldonlystate";
    public const string Extension = "acceptancemodcontrol.fieldstate", Source = "field-only-state";
    private const float Radius = 1.5f;
    private ExtensionRegistration? _registration;

    private IEnumerator Start()
    {
        float deadline = Time.realtimeSinceStartup + 30f;
        while (valheimCLIPlugin.Instance == null || valheimCLIPlugin.Instance!.Extensions == null)
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("ValheimCLI's extension API did not become ready; the control's commands are off."); yield break; }
            yield return null;
        }
        _registration = valheimCLIPlugin.Instance!.Extensions!.Register(Extension, "0.1.0", 1,
            new ExtensionCommand("set", "Keep a value only in a component field on the markers near a point: <x> <z> <value>", Set,
                readOnly: false, role: ExtensionRole.Client, needsWorld: true),
            new ExtensionCommand("read", "Read the field-only value on the markers near a point: <x> <z>", Read,
                readOnly: true, role: ExtensionRole.Client, needsWorld: true));
    }

    private void OnDestroy() => _registration?.Dispose();

    private static IEnumerator Set(ExtensionContext context)
    {
        var arguments = context.Arguments;
        float x = 0f, z = 0f;
        if (arguments.Count != 3 || !Number(arguments[0], out x) || !Number(arguments[1], out z) || arguments[2].Length == 0)
        { context.Fail("usage", "set <x> <z> <value>"); yield break; }
        var markers = Markers(x, z);
        foreach (var marker in markers)
        {
            var field = marker.GetComponent<FieldOnlyValue>();
            if (field == null) field = marker.AddComponent<FieldOnlyValue>();
            field.Value = arguments[2];
        }
        context.Succeed(Reading(markers));
    }

    private static IEnumerator Read(ExtensionContext context)
    {
        var arguments = context.Arguments;
        float x = 0f, z = 0f;
        if (arguments.Count != 2 || !Number(arguments[0], out x) || !Number(arguments[1], out z)) { context.Fail("usage", "read <x> <z>"); yield break; }
        context.Succeed(Reading(Markers(x, z)));
    }

    // {source, complete, markers, values}: one value per marker instance, null where the component is absent.
    private static Dictionary<string, object?> Reading(List<GameObject> markers) => new Dictionary<string, object?>
    {
        ["source"] = Source, ["complete"] = true, ["markers"] = markers.Count,
        ["values"] = markers.Select(marker => marker.GetComponent<FieldOnlyValue>() is FieldOnlyValue field && field != null ? field.Value : null).ToArray(),
    };

    // The marker instances this client has within Radius of the point.
    private static List<GameObject> Markers(float x, float z)
    {
        int hash = "wood_pole2".GetStableHashCode();
        var found = new List<GameObject>();
        foreach (ZNetView view in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
        {
            if (!view.IsValid() || view.GetZDO().GetPrefab() != hash) continue;
            Vector3 at = view.transform.position;
            float dx = at.x - x, dz = at.z - z;
            if (dx * dx + dz * dz <= Radius * Radius) found.Add(view.gameObject);
        }
        return found;
    }

    private static bool Number(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>The control's state: a plain field, never saved. Exactly what a mod must not rely on across a zone reload.</summary>
public sealed class FieldOnlyValue : MonoBehaviour
{
    public string Value = "";
}
