using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

namespace ExampleMod.Tests;

/// <summary>Native local-host gate: one owned client both hosts and measures, with a disposable packaged character.</summary>
[Trait("Category", "Native")]
public sealed class HostedMarkerSessionTests
{
    private static string PlanFile => Path.Combine(ExampleSession.Directory, "hosted-dry-site-lifecycle.plan.json");

    public sealed class HostedSessionFactAttribute : FactAttribute
    {
        public HostedSessionFactAttribute()
        {
            if (!File.Exists(PlanFile)) Skip = $"No {Path.GetFileName(PlanFile)} beside the test project; the optional local-host gate needs its private plan.";
        }
    }

    [HostedSessionFact]
    public async Task TheLocalHostingClientKeepsItsMarkerAfterRehosting()
    {
        var plan = HostedMarkerPlan.ReadValidated(PlanFile);
        Assert.Equal(DefaultSmokeCharacter.Name, plan.Client.Character);
        string runParent = Path.Combine(AppContext.BaseDirectory, "session-runs");
        Directory.CreateDirectory(runParent);
        string output = Path.Combine(runParent, "hosted-" + Guid.NewGuid().ToString("N"));
        string seedRoot = output + "-character-source";
        var seed = DefaultSmokeCharacter.Prepare(seedRoot);
        IDisposable? staged = null;
        bool stoppedAndPassed = false;
        try
        {
            string steam = EnvironmentInventory.Read(null).SteamUserData
                ?? throw new DirectoryNotFoundException("Steam userdata is required to check for a same-named Cloud character.");
            string saves = HostedWorld.DefaultSaveDirectory(HostedWorld.CurrentPlatform);
            staged = DefaultSmokeCharacter.StageForRun(seed, Path.Combine(saves, "characters_local"), steam);
            int exit = await PinnedServerRun.MainAsync(["host", PlanFile, output], HostedMarkerScenario.RunnerOptions);
            Assert.True(exit == 0, $"The local host run returned {exit}; inspect {Path.Combine(output, "result.json")}. The staged character is retained until client shutdown is verified.");
            stoppedAndPassed = true;
        }
        finally
        {
            // A failed run may have left the owned game alive. Retain its registered character and source
            // for a human to inspect; never delete a save that a running game may still be writing.
            if (stoppedAndPassed)
            {
                staged?.Dispose();
                Directory.Delete(seedRoot, recursive: true);
            }
        }
    }
}
