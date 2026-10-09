using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

// Shared host-operation budgets. Game startup and scenario waits keep their plan-specific deadlines.
internal static class HostedTimeouts
{
    internal static readonly TimeSpan Quick = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan Long = TimeSpan.FromMinutes(15);

    internal static TimeSpan ClientStart(ClientRunPlan plan) =>
        TimeSpan.FromSeconds(Math.Max(30, plan.StartSeconds));

    internal static TimeSpan MacBundleAssessment(TimeSpan hostProbe) =>
        hostProbe > TimeSpan.FromSeconds(ClientTimeouts.DefaultStartSeconds)
            ? hostProbe : TimeSpan.FromSeconds(ClientTimeouts.DefaultStartSeconds);
}
