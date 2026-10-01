using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// The plan of a hosted run: one game client, with MyMod and its adapter, hosts a pinned fixture world from its menu (a
/// listen server). There is no dedicated server, so this is not a <see cref="ServerRunPlan"/>: the client section's
/// <c>hostWorld</c> names the world. Strict pins only, as every plan of this example.
/// </summary>
public sealed class HostedPlan
{
    public const string HostedScenarioName = "hosted";
    private static readonly Regex Word = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);

    public string Scenario { get; set; } = "";
    /// <summary>The hosting client, with its <c>hostWorld</c> section.</summary>
    public ClientRunPlan Client { get; set; } = new();
    public Site DrySite { get; set; } = new();
    public Site WetSite { get; set; } = new();
    /// <summary>The word the host's greeting changes to for the broadcast check; it is set back afterwards.</summary>
    public string NewGreeting { get; set; } = "";

    public static HostedPlan ReadValidated(string path)
    {
        var plan = JsonSerializer.Deserialize<HostedPlan>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Empty plan.");
        if (plan.Scenario != HostedScenarioName) throw new ArgumentException($"A hosted plan's scenario is {HostedScenarioName}; the other scenarios run on an owned server with run.");
        if (!plan.Client.Pinned) throw new ArgumentException("This example runs with strict pins only: remove \"pinning\".");
        if (plan.Client.HostWorld == null) throw new ArgumentException("Add the client's hostWorld section: the fixture world it hosts.");
        plan.Client.Validate();
        foreach (string plugin in new[] { LifecyclePlan.ModPlugin, LifecyclePlan.AdapterPlugin })
            if (!plan.Client.Pins.TryGetValue(plugin, out var md5) || md5 == "absent")
                throw new ArgumentException($"The host runs MyMod and its adapter: pin {plugin} by its MD5.");
        if (ControlPlugins.All.FirstOrDefault(c => plan.Client.Pins.TryGetValue(c.Guid, out var value) && value != "absent") is { } control)
            throw new ArgumentException($"The hosted scenario has no control run: remove {control.Guid}.");
        LifecyclePlan.CheckSites(plan.DrySite, plan.WetSite);
        if (!Word.IsMatch(plan.NewGreeting)) throw new ArgumentException("Set newGreeting to one word (letters, digits, - or _): the host's greeting changes to it for the broadcast check.");
        return plan;
    }
}

/// <summary>
/// <c>hosted</c> (#31): the mod on a host, which is the server of its world and a client with a local player in one
/// process. <see cref="HostRounds"/> places the fixture world, hosts it (protected), and between its two rounds saves
/// with confirmation and restarts the hosted world.
/// <list type="number">
/// <item>First round, on the host: MyMod's patches are applied; no marker before, the mod marks the dry site and refuses the
/// wet one; the host's saved objects show one marker at the dry site. The host's admin changes the greeting: MyMod
/// broadcasts it to everybody, and on a host the broadcast's handler runs in the host's own process ("(host)" in its log,
/// read live from an owned host); the greeting is then set back.</item>
/// <item>After the restart: the host still has one marker at the dry site, none at the wet site.</item>
/// </list>
/// A second client joining the host is not part of it: the toolkit does not provide one yet.
/// </summary>
public static class HostedScenario
{
    public static void Run(HostedPlan plan, Func<ClientSession> openClient, ScenarioReport report, string output, string? hostLog, CancellationToken cancellation = default)
    {
        var timeout = TimeSpan.FromSeconds(plan.Client.JoinSeconds);
        report.Provenance["hostBroadcast"] = hostLog == null ? "not observed: an attached host's log is its operator's" : "the owned host's live BepInEx log";
        new HostRounds { Client = plan.Client, Report = report, Output = output, Cancellation = cancellation }.Run(openClient, round =>
        {
            var host = round.Server; // The same actor as round.Client.
            if (round.Index > 0)
            {
                round.Step("host: the marker is still at the dry site after the restart, none at the wet site", () => RequireMarkers(host, plan, dry: 1));
                return;
            }
            round.Step("host: the mod's Harmony patches are applied", () =>
                HarmonyCensus.Read(host, Capabilities.Harmony, LifecyclePlan.ModPlugin).Check(LifecyclePlan.ModPlugin, DrySiteScenario.Patches).RequireApplied());
            round.Step("no marker at either site before the mod acts", () => RequireMarkers(host, plan, dry: 0));
            round.Step("the mod marks the dry site", () => DrySiteScenario.RequireReply(host.Execute(DrySiteScenario.Mark(plan.DrySite)), "OK: marked "));
            round.Step("the mod refuses the wet site", () => DrySiteScenario.RequireReply(host.Execute(DrySiteScenario.Mark(plan.WetSite)), "REFUSED: "));
            round.Step("host: one marker at the dry site, none at the wet site", () => RequireMarkers(host, plan, dry: 1));
            if (hostLog == null) return;
            round.Step("the mod's greeting broadcast runs its handler on the host, which is server and client at once", () =>
            {
                string before = SyncedConfig.Read(host, Capabilities.Config, LifecyclePlan.ModPlugin, "Server", "Greeting").Value
                    ?? throw new InvalidOperationException("The host has no greeting entry.");
                if (before == plan.NewGreeting) throw new InvalidOperationException($"The host already holds \"{plan.NewGreeting}\"; choose another newGreeting.");
                using var log = new LogWait(hostLog); // Opened before the change: only this change's line counts.
                Greet(host, plan.NewGreeting);
                try
                {
                    var line = log.WaitAsync(Received(plan.NewGreeting), timeout, cancellation: cancellation).GetAwaiter().GetResult();
                    report.Provenance["hostBroadcastLine"] = line.Text;
                }
                finally { Greet(host, before); } // The host's config file is the operator's install's: leave it as it was.
            });
        });
    }

    private static void RequireMarkers(GameActor host, HostedPlan plan, int dry)
    {
        DrySiteScenario.RequireServerMarkers(host, plan.DrySite, dry);
        DrySiteScenario.RequireServerMarkers(host, plan.WetSite, 0);
    }

    private static void Greet(GameActor host, string greeting)
    {
        var reply = host.Execute("mymod_greeting " + greeting);
        if (!reply.Output.Contains("OK: greeting " + greeting)) throw new InvalidOperationException("MyMod did not confirm the greeting: " + string.Join(" | ", reply.Output));
    }

    /// <summary>MyMod's log line when the greeting's broadcast reaches its handler on a host.</summary>
    public static Regex Received(string greeting) => new($"Greeting \"{Regex.Escape(greeting)}\" received from -?\\d+ \\(host\\)", RegexOptions.CultureInvariant);
}

/// <summary>
/// The hosted run's own entry point, beside the pinned server runner (a host has no dedicated server to pin):
/// <c>validate-host|host &lt;plan.json&gt; &lt;new-output-directory&gt;</c>. <c>validate-host</c> checks the plan and runs
/// the same preflight <see cref="HostRounds"/> starts with (<see cref="ClientRunPlan.Preflight(IEnumerable{string})"/>: the
/// fixture world's hashes and own world UID, and an owned client's install, with its ValheimCLI set against its
/// <c>cliManifest</c> when the plan names one), and copies or launches nothing. <c>host</c> runs
/// <see cref="HostedScenario"/>, then scans the owned client's logs, writes <c>result.json</c> and <c>junit.xml</c> and
/// prints PASS or FAIL.
/// </summary>
public static class HostedRun
{
    public const string RunMode = "host", ValidateMode = "validate-host";

    public static int Run(string[] args)
    {
        if (args.Length != 3 || (args[0] != RunMode && args[0] != ValidateMode))
        {
            Console.Error.WriteLine($"Usage: mymod-system-test {ValidateMode}|{RunMode} <hosted-plan.json> <new-output-directory>");
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
        var report = new ScenarioReport("mymod-hosted-test");
        report.Provenance["mode"] = args[0];
        string output = Path.GetFullPath(args[2]);
        bool ownOutput = false;
        var logs = new List<RunLog>();
        try
        {
            if (Path.Exists(output)) throw new IOException("Use a new output directory; existing evidence is never overwritten.");
            var plan = HostedPlan.ReadValidated(args[1]);
            report.Provenance["planSha256"] = WorldFixture.Hash(args[1]);
            report.Provenance["scenario"] = plan.Scenario;
            report.Provenance["clientMode"] = plan.Client.Mode;
            Directory.CreateDirectory(output); ownOutput = true;
            if (args[0] == ValidateMode)
            {
                // The run's first step, alone: a wrong fixture or install fails here as it would before the run copies anything.
                report.Provenance["cliPreflight"] = plan.Client.CliPreflight;
                report.Step(plan.Client.Owned ? "preflight the fixture world and the owned client's install, before anything is copied or started" : "preflight the fixture world, before it is copied",
                    () => plan.Client.Preflight(CliCapabilities.HostedRounds));
            }
            else
            {
                string? hostLog = plan.Client.Owned ? Path.Combine(plan.Client.Install, "BepInEx", "LogOutput.log") : null;
                // Kept when the rounds close the client, or when its startup fails; scanned below.
                HostedScenario.Run(plan, () => ClientSession.Open(plan.Client, output, logs, cancellation.Token), report, output, hostLog, cancellation.Token);
            }
        }
        catch (Exception error)
        {
            report.RecordFailure("runner failed", error);
            Console.Error.WriteLine(error.Message);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (logs.Count != 0) report.ScanLogs(logs);
            if (ownOutput) report.Write(output);
        }
        Console.WriteLine(!report.Passed ? "FAIL" : args[0] == RunMode ? "PASS" : "VALIDATED (plan and the run's preflight only; nothing was copied and no game was launched)");
        return report.Passed ? 0 : 1;
    }
}
