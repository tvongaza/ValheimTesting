// The game-side half of the ReloadCheck example: a ScriptEngine script (BepInEx/scripts) that registers a ValheimCLI
// extension, so the driver can replace revision A with B (-p:ProbeRevision=B) and then remove it. Test runtimes only.
// Moved from ValheimCLI (commit d112140, examples/ReloadProbe) to ValheimTesting on 30 Sep 2026; the code is unchanged.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using UnityEngine;
using valheimCLI.Extensions;

[BepInPlugin("testing.cli.reload-probe", "CLI Reload Probe", Revision)]
public sealed class ReloadProbe : BaseUnityPlugin
{
#if PROBE_B
    public const string Revision = "0.2.0";
#else
    public const string Revision = "0.1.0";
#endif
    private const string Id = "example.probe";
    private const string Marker = "CLI Reload Probe Lease ";
    private ExtensionRegistration? _registration;
    private int _waiting;
    private IEnumerator Start()
    {
        float deadline = Time.realtimeSinceStartup + 30;
        while (valheimCLI.valheimCLIPlugin.Instance?.Extensions == null)
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("Stable CLI core is not available."); yield break; }
            yield return null;
        }
        ExtensionRegistry registry = valheimCLI.valheimCLIPlugin.Instance!.Extensions!;
        // A replacement waits while the previous revision's in-flight work (such as `wait`) drains.
        while (registry.Registrations.Any(r => r.Id == Id))
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("Previous probe is still active, draining or failed cleanup; replacement refused."); yield break; }
            yield return null;
        }
        var commands = new List<ExtensionCommand>
        {
            new ExtensionCommand("hello", "Read revision and live probe resources", Hello, readOnly: true),
            new ExtensionCommand("wait", "Wait until this extension is replaced (bounded at 120s)", Wait, readOnly: true)
        };
#if !PROBE_B
        commands.Add(new ExtensionCommand("removed", "Present only in revision A", Hello, readOnly: true));
#endif
        _registration = registry.Register(Id, Revision, 1, commands.ToArray());
        var marker = new GameObject(Marker + _registration.Instance);
        DontDestroyOnLoad(marker);
        string instance = _registration.Instance;
        _registration.OnDispose(() =>
        {
            Destroy(marker);
            Logger.LogInfo("PROBE_CLEANUP revision=" + Revision + " instance=" + instance + " waiting=" + _waiting);
        });
        Logger.LogInfo("PROBE_REGISTERED revision=" + Revision + " instance=" + instance);
    }
    private IEnumerator Hello(ExtensionContext context)
    {
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = "reload-probe", ["complete"] = true, ["revision"] = Revision,
            ["waiting"] = _waiting,
            ["leases"] = UnityEngine.Object.FindObjectsOfType<GameObject>().Count(obj => obj.name.StartsWith(Marker, StringComparison.Ordinal))
        });
        yield break;
    }
    private IEnumerator Wait(ExtensionContext context)
    {
        _waiting++;
        Logger.LogInfo("PROBE_WAIT_STARTED revision=" + Revision);
        try
        {
            float deadline = Time.realtimeSinceStartup + 120;
            while (Time.realtimeSinceStartup < deadline) yield return null;
            context.Fail("probe_deadline", "The expected replacement did not arrive.");
        }
        finally
        {
            _waiting--;
            Logger.LogInfo("PROBE_WAIT_DISPOSED revision=" + Revision + " waiting=" + _waiting);
        }
    }
    private void OnDestroy() => _registration?.Dispose();
}
