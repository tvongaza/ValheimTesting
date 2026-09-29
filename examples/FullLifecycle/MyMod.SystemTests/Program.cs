using MyMod.SystemTests;
using Valheim.Testing.Game;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with this mod's plan and scenario. The toolkit owns the
// lifecycle: plan checks, fixture copies, provenance, the owned server and its startup events, teardown, the report and
// the result banner. The mod supplies the plan fields, the session capability its test adapter serves, and the scenario.
return await PinnedServerRun.MainAsync(args, new PinnedServerRunOptions<LifecyclePlan>
{
    Name = "mymod-system-test",
    ReadPlan = LifecyclePlan.ReadValidated,
    SessionCapability = "mymod.testing/session",
    SessionTokenVariable = LifecyclePlan.SessionTokenVariable,
    CheckMode = (mode, plan) =>
    {
        if (mode == "run" && plan.Client == null) throw new ArgumentException("A run looks from a client: add the client section.");
    },
    Provenance = (plan, provenance) =>
    {
        provenance["clientMode"] = plan.Client?.Mode ?? "none";
        provenance["humanReview"] = plan.Review.Enabled ? "requested" : "not requested";
    },
    Scenario = run =>
    {
        DrySiteScenario.Run(run.Plan, run.Server, run.Session.Restart, () => ClientSession.Open(run.Plan.Client!, run.Output, run.Cancellation),
            server => OwnedServerSession.WaitUntilJoinable(server, "mymod.testing/session", TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation),
            run.Report, run.Output, run.Cancellation);
        return Task.CompletedTask;
    },
});
