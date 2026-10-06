using MyMod.SystemTests;
using Valheim.Testing.Game;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with this mod's plan and scenario. The toolkit owns the
// lifecycle: plan checks, fixture copies, provenance, the owned server and its startup events, teardown, the report and
// the result banner. The mod supplies the plan fields, the session capability its test adapter serves, and the scenario.
// prepare-server creates a fresh world and writes a dry-site-server plan for it (see ServerFixture); it runs before any
// plan exists, so it is outside the pinned runner. A hosted run has no dedicated server to pin, so validate-host and host
// have their own entry point (HostedRun).
if (args.Length > 0 && args[0] == ServerFixture.Mode) return ServerFixture.Run(args);
if (args.Length > 0 && args[0] is HostedRun.RunMode or HostedRun.ValidateMode) return HostedRun.Run(args);
var options = new PinnedServerRunOptions<LifecyclePlan>
{
    Name = "mymod-system-test",
    ReadPlan = path =>
    {
        var plan = LifecyclePlan.ReadValidated(path);
        // Two simultaneous clients are a campaign's actors (an inventory assigns their hosts and Steam identities).
        if (plan.Scenario is LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario)
            throw new ArgumentException($"The {plan.Scenario} scenario runs as a campaign: campaign run <campaign.json> <plan.json> <new-output-directory>.");
        return plan;
    },
    SessionCapability = "mymod.testing/session",
    SessionTokenVariable = LifecyclePlan.SessionTokenVariable,
    CheckPlan = plan =>
    {
        if (plan.Client == null && !plan.ServerOnly)
            throw new ArgumentException($"A run looks from a client: add the client section, or use the {LifecyclePlan.ServerScenario} scenario for the server half alone.");
        // A campaign template is bound to its prepared actors in memory; its own rules apply to the bound plan.
        if (plan.Scenario is LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario) LifecyclePlan.Validated(plan);
    },
    Provenance = (plan, provenance) =>
    {
        provenance["clientMode"] = plan.Client?.Mode ?? "none";
        provenance["humanReview"] = plan.Review.Enabled ? "requested" : "not requested";
        provenance["expectFailure"] = plan.ExpectFailure ?? "none";
    },
    Scenario = (session, plan) =>
    {
        var owned = session.Server!;
        if (plan.ServerOnly)
        {
            var server = DrySiteServerScenario.Run(plan, owned.Game, owned.Restart, session.Report);
            if (plan.PatchReload != null)
            {
                // It writes into the runtime copy's scripts folder, which must be on this machine.
                if (owned.Host != null) throw new ArgumentException("patchReload runs only with the server on this machine (no --inventory).");
                PatchReloadScenario.Run(plan, server, owned.RuntimeDirectory, session.Output, session.Report, session.Cancellation);
            }
            return Task.CompletedTask;
        }
        if (plan.Scenario == LifecyclePlan.LifecycleScenario)
        {
            // Its logs are scanned with the server's at teardown, after the scenario stops the client, and also after a failed startup.
            DrySiteScenario.Run(plan, owned.Game, owned, () => session.OpenClient(plan.Client!), session.Report, session.Output, session.Cancellation);
            return Task.CompletedTask;
        }
        // The native campaign's scenarios (CampaignScenarios). Their in-run log reads open files on this machine, so a server on
        // another host (--inventory or a campaign) skips the server's, and a campaign's clients skip theirs; only the crossplay
        // lobby is read on the server's host.
        bool local = owned.Host == null, localClients = session.CampaignClients.Count == 0;
        CampaignScenarios.Run(new CampaignRun
        {
            Plan = plan, Server = owned.Game, OwnedServer = owned, Report = session.Report,
            Output = session.Output, Cancellation = session.Cancellation, ServerHost = owned.Host, RemoteClients = !localClients,
            OpenClient = (client, directory) =>
            {
                if (directory == null) return session.OpenClient(client); // Its logs join the teardown scan.
                // A second client in one run keeps its command record and logs apart from the first one's, on this machine.
                var second = ClientActor.OnThisMachine(directory, client, Directory.CreateDirectory(Path.Combine(session.Output, directory)).FullName, session.Cancellation);
                try { return second.Start(); }
                finally { foreach (var log in second.Logs) session.AddLog(log); } // Also a failed startup's, for the scan.
            },
            OpenCampaignClient = (client, name) => session.OpenClient(client, name),
            ServerLog = () => local ? Path.Combine(owned.RuntimeDirectory, "BepInEx", "LogOutput.log") : null,
            ClientLog = client => localClients && client.Owned ? Path.Combine(client.Install, "BepInEx", "LogOutput.log") : null,
            // The lobby line is in the log the game writes to: the -logFile file when the plan passes one (the Windows server's
            // BepInEx log did not carry it), otherwise BepInEx's. A crossplay server on another machine (--inventory): the host's own log.
            Lobby = server => local
                ? CrossplayServer.WaitForLobby(server, plan.GameLogFile(owned.RuntimeDirectory, owned.WorldDirectory) ?? CrossplayServer.BepInExLog(owned.RuntimeDirectory),
                    TimeSpan.FromSeconds(plan.StartupSeconds), session.Cancellation)
                : CrossplayServer.WaitForLobby(server, owned.Host!, plan.GameLogFile(owned.RuntimeDirectory, owned.WorldDirectory) ?? CrossplayServer.HostBepInExLog(owned.RuntimeDirectory),
                    TimeSpan.FromSeconds(plan.StartupSeconds), session.Cancellation),
        });
        return Task.CompletedTask;
    },
};
if (ThreeActorCampaign.Handles(args)) return await ThreeActorCampaign.RunAsync(args, options);
return await PinnedServerRun.MainAsync(args, options);
