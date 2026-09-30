using MyMod.SystemTests;
using Valheim.Testing.Game;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with this mod's plan and scenario. The toolkit owns the
// lifecycle: plan checks, fixture copies, provenance, the owned server and its startup events, teardown, the report and
// the result banner. The mod supplies the plan fields, the session capability its test adapter serves, and the scenario.
// prepare-server creates a fresh world and writes a dry-site-server plan for it (see ServerFixture); it runs before any
// plan exists, so it is outside the pinned runner. A hosted run has no dedicated server to pin, so validate-host and host
// have their own entry point (HostedRun).
if (args.Length > 0 && args[0] == ServerFixture.Mode) return ServerFixture.Run(args);
if (args.Length > 0 && args[0] == CharacterFixture.Mode) return CharacterFixture.Run(args);
if (args.Length > 0 && args[0] is HostedRun.RunMode or HostedRun.ValidateMode) return HostedRun.Run(args);
return await PinnedServerRun.MainAsync(args, new PinnedServerRunOptions<LifecyclePlan>
{
    Name = "mymod-system-test",
    ReadPlan = LifecyclePlan.ReadValidated,
    SessionCapability = "mymod.testing/session",
    SessionTokenVariable = LifecyclePlan.SessionTokenVariable,
    CheckMode = (mode, plan) =>
    {
        if (mode == "run" && plan.Client == null && !plan.ServerOnly)
            throw new ArgumentException($"A run looks from a client: add the client section, or use the {LifecyclePlan.ServerScenario} scenario for the server half alone.");
    },
    Provenance = (plan, provenance) =>
    {
        provenance["clientMode"] = plan.Client?.Mode ?? "none";
        provenance["humanReview"] = plan.Review.Enabled ? "requested" : "not requested";
        provenance["expectFailure"] = plan.ExpectFailure ?? "none";
    },
    Scenario = run =>
    {
        if (run.Plan.ServerOnly)
        {
            var server = DrySiteServerScenario.Run(run.Plan, run.Server, run.Session.Restart, run.Report);
            if (run.Plan.PatchReload != null)
            {
                // It writes into the runtime copy's scripts folder, which must be on this machine.
                if (run.Profile != null) throw new ArgumentException("patchReload runs only with the server on this machine (no --profile).");
                PatchReloadScenario.Run(run.Plan, server, run.RuntimeDirectory, run.Output, run.Report, run.Cancellation);
            }
            return Task.CompletedTask;
        }
        Action<GameActor> joinable = server => OwnedServerSession.WaitUntilJoinable(server, "mymod.testing/session", TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation);
        if (run.Plan.Scenario == LifecyclePlan.LifecycleScenario)
        {
            DrySiteScenario.Run(run.Plan, run.Server, run.Session.Restart, () =>
                {
                    var client = ClientSession.Open(run.Plan.Client!, run.Output, run.Cancellation);
                    run.Logs.AddRange(client.Logs); // Scanned with the server's at teardown, after the scenario stops the client.
                    return client;
                },
                joinable, run.Report, run.Output, run.Cancellation);
            return Task.CompletedTask;
        }
        // The native campaign's scenarios (CampaignScenarios). Their in-run log reads open files on this machine, so a run
        // with --profile skips the server's; only the crossplay lobby is read on the server's host.
        bool local = run.Profile == null;
        CampaignScenarios.Run(new CampaignRun
        {
            Plan = run.Plan, Server = run.Server, RestartServer = run.Session.Restart, WaitUntilJoinable = joinable, Report = run.Report,
            Output = run.Output, Cancellation = run.Cancellation,
            OpenClient = (client, directory) =>
            {
                if (directory == null) return run.OpenClient(client); // Its logs join the teardown scan.
                // A second client in one run keeps its command record and logs apart from the first one's.
                string own = Path.Combine(run.Output, directory);
                Directory.CreateDirectory(own);
                var session = ClientSession.Open(client, own, run.Cancellation);
                run.Logs.AddRange(session.Logs);
                return session;
            },
            ServerLog = () => local ? Path.Combine(run.RuntimeDirectory, "BepInEx", "LogOutput.log") : null,
            ClientLog = client => local && client.Owned ? Path.Combine(client.Install, "BepInEx", "LogOutput.log") : null,
            // The lobby line is in the log the game writes to: the -logFile file when the plan passes one (the Windows server's
            // BepInEx log did not carry it), otherwise BepInEx's. A crossplay server on another machine (--profile): the host's own log.
            Lobby = server => local
                ? CrossplayServer.WaitForLobby(server, run.Plan.GameLogFile(run.RuntimeDirectory, run.WorldDirectory) ?? CrossplayServer.BepInExLog(run.RuntimeDirectory),
                    TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation)
                : CrossplayServer.WaitForLobby(server, run.ServerHost!, run.Plan.GameLogFile(run.RuntimeDirectory, run.WorldDirectory) ?? CrossplayServer.HostBepInExLog(run.RuntimeDirectory),
                    TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation),
        });
        return Task.CompletedTask;
    },
});
