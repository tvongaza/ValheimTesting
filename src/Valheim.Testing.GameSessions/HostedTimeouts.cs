using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

// Shared host-operation budgets. Game startup and scenario waits keep their plan-specific deadlines.
internal static class HostedTimeouts
{
    internal static readonly TimeSpan Quick = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan Long = TimeSpan.FromMinutes(15);

    internal static TimeSpan ClientStart(ClientRunPlan plan) =>
        TimeSpan.FromSeconds(Math.Max(30, plan.StartSeconds));

    /// <summary>Reading a whole game assembly (a few MB) from a host: never less than two minutes, so a slow link is not an unknown build.</summary>
    internal static TimeSpan GameAssemblyRead(TimeSpan hostProbe) =>
        hostProbe > TimeSpan.FromMinutes(2) ? hostProbe : TimeSpan.FromMinutes(2);

    internal static TimeSpan MacBundleAssessment(TimeSpan hostProbe) =>
        hostProbe > TimeSpan.FromSeconds(ClientTimeouts.DefaultStartSeconds)
            ? hostProbe : TimeSpan.FromSeconds(ClientTimeouts.DefaultStartSeconds);
}
