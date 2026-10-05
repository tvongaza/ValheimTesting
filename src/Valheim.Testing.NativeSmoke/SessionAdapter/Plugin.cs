using System;
using System.Collections;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using valheimCLI.Extensions;
using Valheim.Testing.Adapter;

namespace NativeSmoke.SessionAdapter;

/// <summary>
/// Test-only server identity. The runner sets the token and exact selected plugin GUIDs on the owned process;
/// ValheimCLI exposes the read-only session reply. Never install this plugin in a player or production runtime.
/// </summary>
[BepInPlugin(Guid, "Native Smoke Session Adapter", "0.1.0")]
[BepInDependency("valheimCLI.valheimCLI")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheim.testing.native-smoke";
    public const string TokenVariable = "VT_NATIVE_SMOKE_SESSION_TOKEN";
    public const string SelectedVariable = "VT_NATIVE_SMOKE_PLUGIN_GUIDS";
    public const string Capability = "valheim.testing.native-smoke/session";
    private ExtensionRegistration? _registration;

    private IEnumerator Start()
    {
        string? raw = Environment.GetEnvironmentVariable(SelectedVariable);
        string[] selected = raw?.Split(';').Where(guid => !string.IsNullOrWhiteSpace(guid)).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        if (selected.Length == 0 || selected.Any(guid => guid != guid.Trim()))
        {
            Logger.LogError($"{SelectedVariable} must list the deliberately selected plugin GUIDs, separated by semicolons.");
            yield break;
        }
        IEnumerator registration = TestExtension.Register("valheim.testing.native-smoke", "0.1.0", TokenVariable,
            () => selected.All(Chainloader.PluginInfos.ContainsKey), value => _registration = value, Logger.LogError);
        while (registration.MoveNext()) yield return registration.Current;
    }

    private void OnDestroy() => _registration?.Dispose();
}
