using MyMod.SystemTests;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with this mod's plan and scenario. The toolkit owns the
// lifecycle: plan checks, fixture copies, provenance, the owned server and its startup events, teardown, the report and
// the result banner. The mod supplies the plan fields, the session capability its test adapter serves, and the scenario
// (DrySiteScenario.RunnerOptions). prepare-server creates a fresh world and writes a dry-site-server plan for it (see
// ServerFixture); it runs before any plan exists, so it is outside the pinned runner.
if (args.Length > 0 && args[0] == ServerFixture.Mode) return ServerFixture.Run(args);
return await PinnedServerRun.MainAsync(args, DrySiteScenario.RunnerOptions());
