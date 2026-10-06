using MyMod.SystemTests;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// The plan of a hosted run: one game client, with MyMod and its adapter, hosts a pinned fixture world from its menu (a
/// listen server). There is no dedicated server, so this is not a <see cref="ServerRunPlan"/>: the client section's
/// <c>hostWorld</c> names the world. Strict pins only, as every plan of this example. As a campaign (<c>campaign run</c>, a
/// campaign without a dedicated server), its <see cref="Peer"/> joins the host from another machine.
/// </summary>
public sealed class HostedPlan
{
    public const string HostedScenarioName = "hosted";
    private static readonly Regex Word = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);

    public string Scenario { get; set; } = "";
    /// <summary>The hosting client, with its <c>hostWorld</c> section.</summary>
    public ClientRunPlan Client { get; set; } = new();
    /// <summary>A campaign's second actor: a client that joins the host (<c>joinsHost</c>), with MyMod and its adapter. Only as a campaign.</summary>
    public ClientRunPlan? Peer { get; set; }
    /// <summary>The campaign's client names: the hosting client and its peer.</summary>
    public const string HostClient = "host", PeerClient = "peer";
    public Site DrySite { get; set; } = new();
    public Site WetSite { get; set; } = new();
    /// <summary>Exact known log lines and reasons for this disposable run; other errors still fail teardown.</summary>
    public Dictionary<string, LogClassification> LogScan { get; set; } = [];
    /// <summary>The word the host's greeting changes to for the broadcast check; it is set back afterwards.</summary>
    public string NewGreeting { get; set; } = "";

    /// <summary>Reads and validates a standalone hosted plan (<c>validate-host</c>, <c>host</c>): no peer, which only a campaign places.</summary>
    public static HostedPlan ReadValidated(string path)
    {
        var plan = Validated(Read(path));
        if (plan.Peer != null) throw new ArgumentException("A peer joins the host from another machine: run this plan as a campaign (campaign run <session.json> <plan.json> <new-output-directory>).");
        return plan;
    }

    /// <summary>Reads a plan without its rules: a campaign template, whose installs, characters and fixture the campaign binds.</summary>
    public static HostedPlan Read(string path) =>
        JsonSerializer.Deserialize<HostedPlan>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Empty plan.");

    /// <summary>
    /// The example's rules that hold before a campaign binds anything (its scenario, the peer's kind, both clients' MyMod pins, no
    /// control, the sites, the greeting and the log classifications), for <c>campaign check</c> and before <c>campaign run</c> writes
    /// to any host. <see cref="Validated"/> applies them with the client sections' own rules.
    /// </summary>
    public static HostedPlan CheckTemplate(HostedPlan plan)
    {
        if (plan.Scenario != HostedScenarioName) throw new ArgumentException($"A hosted plan's scenario is {HostedScenarioName}; the other scenarios run on an owned server with run.");
        if (plan.Client.HostWorld == null) throw new ArgumentException("Add the client's hostWorld section: the fixture world it hosts.");
        if (plan.Peer is { } peer)
        {
            if (!peer.JoinsHost) throw new ArgumentException("The peer joins the host: set its joinsHost.");
            if (!peer.Pinned) throw new ArgumentException("This example runs with strict pins only: remove the peer's \"pinning\".");
            foreach (string plugin in new[] { AcceptancePlan.ModPlugin, AcceptancePlan.AdapterPlugin })
                if (!peer.Pins.TryGetValue(plugin, out var md5) || md5 == "absent")
                    throw new ArgumentException($"The peer runs MyMod and its adapter: pin {plugin} by its MD5.");
        }
        if (!plan.Client.Pinned) throw new ArgumentException("This example runs with strict pins only: remove \"pinning\".");
        LogScanner.CheckClassifications(plan.LogScan);
        foreach (string plugin in new[] { AcceptancePlan.ModPlugin, AcceptancePlan.AdapterPlugin })
            if (!plan.Client.Pins.TryGetValue(plugin, out var md5) || md5 == "absent")
                throw new ArgumentException($"The host runs MyMod and its adapter: pin {plugin} by its MD5.");
        if (ControlPlugins.All.FirstOrDefault(c => plan.Client.Pins.TryGetValue(c.Guid, out var value) && value != "absent") is { } control)
            throw new ArgumentException($"The hosted scenario has no control run: remove {control.Guid}.");
        AcceptancePlan.CheckSites(plan.DrySite, plan.WetSite);
        if (!Word.IsMatch(plan.NewGreeting)) throw new ArgumentException("Set newGreeting to one word (letters, digits, - or _): the host's greeting changes to it for the broadcast check.");
        return plan;
    }

    /// <summary>The example's rules on a read plan, or a campaign's plan once bound to its prepared actors.</summary>
    public static HostedPlan Validated(HostedPlan plan)
    {
        CheckTemplate(plan);
        plan.Client.Validate();
        plan.Peer?.Validate();
        return plan;
    }
}

/// <summary>
/// <c>hosted</c> (#31): the mod on a host, which is the server of its world and a client with a local player in one
/// process. The runner's <see cref="GameSession"/> has the toolkit's <see cref="HostingClientActor"/> as its
/// <see cref="GameSession.Host"/>: before the scenario it placed the fixture world, opened the client, checked MyMod's
/// Harmony patches on it and hosts the world (protected).
/// <list type="number">
/// <item>On the host: no marker before, the mod marks the dry site and refuses the wet one; the host's saved objects show one
/// marker at the dry site. The host's admin changes the greeting: MyMod broadcasts it to everybody, and on a host the
/// broadcast's handler runs in the host's own process ("(host)" in its log, read live from an owned host); the greeting is
/// then set back.</item>
/// <item>A confirmed save, then the host's restart (<see cref="HostingClientActor.Restart"/>: it leaves, which saves, and
/// hosts the world again): the host still has one marker at the dry site, none at the wet site.</item>
/// </list>
/// As a campaign with a <see cref="HostedPlan.Peer"/>, the peer (another machine, on its own Steam account) joins the host after
/// the host marked the site, and the host sees it; after the restart, which disconnects it, it joins again.
/// The session's teardown closes the peer, has the host leave its world, closes it and moves the world into the evidence.
/// </summary>
public static class HostedScenario
{
    public static async Task Run(GameSession session, HostedPlan plan)
    {
        bool peer = session.Clients.ContainsKey(HostedPlan.PeerClient);
        var owned = session.Host ?? throw new ArgumentException("The hosted scenario runs on a session with a hosting client.");
        var report = session.Report;
        var timeout = TimeSpan.FromSeconds(plan.Client.JoinSeconds);
        string? hostLog = owned.LiveLog;
        report.Provenance["hostBroadcast"] = hostLog == null ? "not observed: an attached host's log is its operator's" : "the owned host's live BepInEx log";
        var host = owned.Game; // The server of its world and its client.
        report.Step("no marker at either site before the mod acts", () => RequireMarkers(host, plan, dry: 0));
        report.Step("the mod marks the dry site", () => host.Execute(DrySiteScenario.Mark(plan.DrySite)).RequireLine("OK: marked ", "MyMod did not mark the dry site"));
        report.Step("the mod refuses the wet site", () => host.Execute(DrySiteScenario.Mark(plan.WetSite)).RequireLine("REFUSED: ", "MyMod did not refuse the wet site"));
        report.Step("host: one marker at the dry site, none at the wet site", () => RequireMarkers(host, plan, dry: 1));
        if (peer)
        {
            await session.Join(HostedPlan.PeerClient);
            report.Step("host: its peer is connected", () => CampaignSteps.RequirePeers(host, 1));
        }
        if (hostLog != null)
            report.Step("the mod's greeting broadcast runs its handler on the host, which is server and client at once", () =>
            {
                string before = SyncedConfig.Read(host, Capabilities.Config, AcceptancePlan.ModPlugin, "Server", "Greeting").Value
                    ?? throw new InvalidOperationException("The host has no greeting entry.");
                if (before == plan.NewGreeting) throw new InvalidOperationException($"The host already holds \"{plan.NewGreeting}\"; choose another newGreeting.");
                using var log = new LogWait(hostLog); // Opened before the change: only this change's line counts.
                Greet(host, plan.NewGreeting);
                try
                {
                    var line = log.WaitAsync(Received(plan.NewGreeting), timeout, cancellation: session.Cancellation).GetAwaiter().GetResult();
                    report.Provenance["hostBroadcastLine"] = line.Text;
                }
                finally { Greet(host, before); } // The host's config file is the operator's install's: leave it as it was.
            });
        report.Step(StepPhase.Setup, "confirmed world save", () => new SessionControl(host).Save(plan.Client.HostWorld!.WorldUid, TimeSpan.FromSeconds(plan.Client.HostWorld.SaveSeconds)));
        report.Step(StepPhase.Setup, "restart the hosted world" + (owned.ProtectPlayer ? ", protected" : ""), () => host = owned.Restart());
        report.Step("host: the marker is still at the dry site after the restart, none at the wet site", () => RequireMarkers(host, plan, dry: 1));
        if (!peer) return;
        // The restart disconnected the peer: it is back at its menu, then joins the restarted world.
        await session.Rejoin(HostedPlan.PeerClient);
        report.Step("host: its peer is connected again after the restart", () => CampaignSteps.RequirePeers(host, 1));
    }

    /// <summary>
    /// The runner's options for a hosted plan (<see cref="PinnedServerRun.MainAsync{TPlan}(string[], HostedRunOptions{TPlan})"/>,
    /// modes <c>validate-host</c> and <c>host</c>): the plan's rules, its hosting client, MyMod's declaration, the log
    /// classifications and the scenario.
    /// </summary>
    public static HostedRunOptions<HostedPlan> RunnerOptions(Func<GameSession, HostedPlan, Task>? scenario = null) => new()
    {
        Name = "mymod-hosted-test",
        ReadPlan = HostedPlan.ReadValidated,
        Host = plan => plan.Client,
        Mod = AcceptancePlan.Mod,
        CheckPlan = plan => HostedPlan.Validated(plan),
        LogScan = plan => plan.LogScan,
        Provenance = (plan, provenance) => provenance["scenario"] = plan.Scenario,
        Scenario = scenario ?? Run,
    };

    /// <summary>A campaign's two clients: the hosting client and its peer.</summary>
    public static IReadOnlyDictionary<string, ClientRunPlan> CampaignClients(HostedPlan plan) => new Dictionary<string, ClientRunPlan>
    {
        [HostedPlan.HostClient] = plan.Client,
        [HostedPlan.PeerClient] = plan.Peer ?? throw new ArgumentException("A hosted campaign needs the peer section: the client that joins the host."),
    };

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
