using System.Security.Cryptography;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

public sealed partial class LifecyclePlan
{
    /// <summary>dry-site-server: then load, replace and remove a patching script with ScriptEngine (<see cref="PatchReloadScenario"/>, #30).</summary>
    public PatchReloadSettings? PatchReload { get; set; }
}

/// <summary>
/// The two builds of the PatchReload probe (examples/FullLifecycle/Probes/PatchReload) that <see cref="PatchReloadScenario"/>
/// installs in the server runtime's <c>BepInEx/scripts</c>, as this runner reads them. The runtime has ScriptEngine in its
/// plugins, pinned, watching an empty scripts folder (<c>LoadOnStart</c> and <c>EnableFileSystemWatcher</c> on).
/// </summary>
public sealed class PatchReloadSettings
{
    public const string ScriptEngine = "com.bepis.bepinex.scriptengine";
    /// <summary>The probe built as revision A.</summary>
    public string RevisionA { get; set; } = "";
    /// <summary>The probe built with <c>-p:ProbeRevision=B</c>.</summary>
    public string RevisionB { get; set; } = "";
    /// <summary>
    /// A control run: <see cref="RevisionA"/> is the <c>-p:ProbeUnpatch=Other</c> build, whose unload also removes MyMod's
    /// patches. The check that other owners' patches are unchanged after the reload must then fail, naming MyMod's patch.
    /// </summary>
    public bool ExpectOthersRemoved { get; set; }
    /// <summary>How long ScriptEngine may take to load, replace or unload the script.</summary>
    public int ReloadSeconds { get; set; } = 60;

    public void Validate(LifecyclePlan plan)
    {
        foreach (var (name, path) in new[] { ("revisionA", RevisionA), ("revisionB", RevisionB) })
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException($"patchReload.{name} is the full path of a built PatchReload probe on this machine.");
        if (Md5(RevisionA) == Md5(RevisionB)) throw new ArgumentException("patchReload.revisionA and revisionB are the same build: build B with -p:ProbeRevision=B.");
        if (ReloadSeconds is < 5 or > 600) throw new ArgumentException("patchReload.reloadSeconds is 5 to 600.");
        if (!plan.Pins.TryGetValue(ScriptEngine, out var engine) || engine.Length != 32)
            throw new ArgumentException($"patchReload needs ScriptEngine in the server runtime's plugins: pin {ScriptEngine} by its MD5.");
        if (plan.Pins.ContainsKey(PatchReloadScenario.Probe))
            throw new ArgumentException($"Do not pin {PatchReloadScenario.Probe}: the scenario installs it after the start and pins each build as it loads.");
    }

    internal static string Md5(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path)));
}

/// <summary>
/// An adapter's unload leaves other owners' Harmony patches in place (#30), shown with ScriptEngine, the reload path the toolkit
/// documents for a test adapter. The PatchReload probe patches <c>Terminal::InitTerminal</c>, the method MyMod patches, under its
/// own Harmony ID, and removes only its own patches when unloaded.
/// <list type="number">
/// <item>The scripts folder is empty, MyMod's declared patch is applied and the probe has none.</item>
/// <item>Revision A is copied in; once the pins show it loaded, the census shows exactly its postfix beside MyMod's.</item>
/// <item>Revision B replaces it (ScriptEngine unloads A, then loads B); the census shows exactly B's postfix.</item>
/// <item>The file is removed; once the pins show the probe gone, the census shows no probe patch.</item>
/// </list>
/// After each change, every other owner's patches on those methods are exactly as before (<see cref="HarmonyCensus.OthersChanged"/>)
/// and MyMod's declared patch is applied. In a control run (<see cref="PatchReloadSettings.ExpectOthersRemoved"/>) A's unload also
/// removes MyMod's patches, and the check after the reload must fail naming them; the run ends there.
/// Each census is written to <c>patch-reload-{stage}.txt</c>. A run on this machine only: the scenario writes into the runtime copy.
/// </summary>
public static class PatchReloadScenario
{
    public const string Probe = "example.mymod.probe.patchreload";
    private const string ProbeFile = "MyMod.Probe.PatchReload.dll";

    public static void Run(LifecyclePlan plan, GameActor server, string runtimeDirectory, string output, ScenarioReport report, CancellationToken cancellation)
    {
        var settings = plan.PatchReload ?? throw new ArgumentException("The plan has no patchReload section.");
        string scripts = Path.Combine(runtimeDirectory, "BepInEx", "scripts");
        string deployed = Path.Combine(scripts, ProbeFile);
        var timeout = TimeSpan.FromSeconds(settings.ReloadSeconds);
        string Pins(string md5OrAbsent) => StrictExpectations.WithPlugin(plan.ExpectCommand, Probe, md5OrAbsent);
        report.Provenance["patchReloadA"] = PatchReloadSettings.Md5(settings.RevisionA);
        report.Provenance["patchReloadB"] = PatchReloadSettings.Md5(settings.RevisionB);
        // The whole census, not one owner's methods: once a control removes MyMod's patches, a census filtered to MyMod lists no
        // method at all, and the probe's own patch on the same method would vanish from it too (run-prc2, 30 Sep).
        HarmonyCensus Census(string stage)
        {
            var census = HarmonyCensus.Read(server, Capabilities.Harmony);
            File.WriteAllLines(Path.Combine(output, $"patch-reload-{stage}.txt"), census.Patches.Select(p => p.ToString()));
            return census;
        }
        // ScriptEngine logs one of these when a reload has finished. The pins alone cannot say so: they hash the file on disk,
        // which changes before the reload, and while it runs the probe is briefly not loaded (run-pr3, 30 Sep).
        string bepinexLog = Path.Combine(runtimeDirectory, "BepInEx", "LogOutput.log");
        int Reloads()
        {
            using var stream = new FileStream(bepinexLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            int count = 0;
            for (string? line; (line = reader.ReadLine()) != null;)
                if (line.Contains("Script Engine] Reloaded all plugins!", StringComparison.Ordinal) || line.Contains("Script Engine] No plugins to reload", StringComparison.Ordinal)) count++;
            return count;
        }
        // After the change: wait for ScriptEngine's reload line (BepInEx flushes its disk log every 2 s), then until the pins hold
        // and the census shows the probe's patches as expected, re-reading every half second. A pin mismatch in between is the
        // reload settling, not a failure; the deadline ends the wait with the last reading.
        HarmonyCensus Change(Action change, string md5OrAbsent, string stage, string? revision)
        {
            int before = Reloads();
            change();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Reloads() == before)
            {
                if (clock.Elapsed > timeout) throw new TimeoutException($"After {clock.Elapsed.TotalSeconds:0} s ScriptEngine has not logged a finished reload.");
                cancellation.ThrowIfCancellationRequested();
                Thread.Sleep(500);
            }
            string last = "no census read";
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var left = timeout - clock.Elapsed;
                if (left <= TimeSpan.Zero) throw new TimeoutException($"After {clock.Elapsed.TotalSeconds:0} s {last}.");
                try
                {
                    server.WaitForEnvironment(Pins(md5OrAbsent), left, cancellation).GetAwaiter().GetResult();
                    var census = Census(stage);
                    var own = census.Patches.Where(p => p.Owner == Probe).ToArray();
                    bool done = revision == null ? own.Length == 0
                        : own.Length == 1 && own[0].Kind == "postfix" && own[0].Method.StartsWith("Terminal::InitTerminal", StringComparison.Ordinal) &&
                          (own[0].Patch ?? "").StartsWith($"MyMod.Probes.PatchReload.{revision}::Postfix", StringComparison.Ordinal);
                    if (done) return census;
                    last = $"the probe's patches are [{string.Join("; ", own.Select(p => p.ToString()))}], " + (revision == null ? "not none" : $"not one postfix of {revision} on Terminal::InitTerminal");
                }
                catch (InvalidOperationException error) { last = error.Message; }
                Thread.Sleep(500);
            }
        }
        void RequireOthersUnchanged(HarmonyCensus before, string stage)
        {
            var after = Census(stage);
            var changes = HarmonyCensus.OthersChanged(before, after, Probe);
            if (changes.Count != 0) throw new InvalidOperationException("Other owners' patches changed: " + string.Join("; ", changes));
            after.Check(LifecyclePlan.ModPlugin, DrySiteScenario.Patches).RequireApplied();
        }

        HarmonyCensus before = null!;
        report.Step("patch reload: the scripts folder is empty, MyMod's patch is applied and the probe has none", () =>
        {
            if (!Directory.Exists(scripts) || Directory.EnumerateFileSystemEntries(scripts).Any())
                throw new InvalidOperationException("ScriptEngine's folder must exist and be empty: a reload affects every script. " + scripts);
            server.VerifyEnvironment(Pins("absent"));
            before = Census("before");
            before.Check(LifecyclePlan.ModPlugin, DrySiteScenario.Patches).RequireApplied();
            if (before.Patches.Any(p => p.Owner == Probe)) throw new InvalidOperationException("The probe is patched before it was installed.");
        });
        report.Step("patch reload: revision A loads and patches Terminal::InitTerminal beside MyMod",
            () => Change(() => ExtensionReload.Install(settings.RevisionA, deployed), report.Provenance["patchReloadA"], "a-loaded", "RevisionA"));
        report.Step("patch reload: other owners' patches unchanged after A loaded", () => RequireOthersUnchanged(before, "a-others"));
        report.Step("patch reload: revision B replaces A (A unloaded, B patched once)",
            () => Change(() => ExtensionReload.Install(settings.RevisionB, deployed), report.Provenance["patchReloadB"], "b-loaded", "RevisionB"));
        const string afterReload = "patch reload: other owners' patches unchanged after the reload";
        if (settings.ExpectOthersRemoved)
        {
            var control = new ControlPlugin("unpatch-other-reload", Probe, OnServer: true, LifecyclePlan.ServerScenario, afterReload, "removed: " + LifecyclePlan.ModPlugin + " ");
            ControlPlugins.ExpectFailure(report, control, () => RequireOthersUnchanged(before, "b-others"));
            return; // MyMod's patch is gone: the rest would only repeat it.
        }
        report.Step(afterReload, () => RequireOthersUnchanged(before, "b-others"));
        report.Step("patch reload: removing the script unloads B", () => Change(() => File.Delete(deployed), "absent", "removed", null));
        report.Step("patch reload: other owners' patches unchanged after the unload", () => RequireOthersUnchanged(before, "removed-others"));
    }
}
