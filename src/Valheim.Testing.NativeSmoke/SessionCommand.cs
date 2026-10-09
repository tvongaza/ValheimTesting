using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>
/// A session file (<c>session.json</c>: the actors, their locks, characters and the world, placed from the inventory): <c>check</c>
/// reports every independent problem with its inputs and actor assignment without contacting a host, and with <c>--hosts</c> also
/// runs the read-only host checks (installs, loaders, conflicting use, signed-in Steam accounts, copy space, run journals).
/// Mutable state is checked again under the leases before launch.
/// </summary>
internal static class SessionCommand
{
    internal const string Usage = "valheim-test session check SESSION [--hosts] [--json]";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        var rest = args.Skip(1).ToList();
        bool json = rest.Remove("--json"), hosts = rest.Remove("--hosts");
        if (args.Length == 0 || args[0] != "check" || rest.Count != 1 || rest[0].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: " + Usage);
            return 2;
        }
        CampaignPreflightReport report;
        try
        {
            report = hosts
                ? await HostedCampaignPreparation.InspectAsync(rest[0], HostedTimeouts.Quick).ConfigureAwait(false)
                : HostedCampaignPreparation.Inspect(rest[0]);
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException or HostOperationException)
        {
            error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (string line in report.Detected) output.WriteLine("detected: " + line);
            foreach (var actor in report.Actors)
            {
                output.WriteLine($"{actor.Name}: {actor.Kind} on {actor.Host} ({actor.Platform})" +
                    (actor.Environment == null ? "" : $" via {actor.Environment}: {actor.SelectionReason}"));
                if (actor.CharactersDirectory != null)
                    output.WriteLine($"  characters_local {actor.CharactersDirectory}; Steam userdata {actor.SteamUserDataDirectory}");
            }
            if (report.Ready) output.WriteLine(hosts
                ? "READY: read-only host checks passed. Mutable state is rechecked under lease before launch."
                : "ELIGIBLE: local files, fixture and actor assignments passed. Host readiness is checked by --hosts, and again under lease before launch.");
            else foreach (var problem in report.Problems)
                output.WriteLine($"REFUSED {problem.Actor} {problem.Input}: {problem.Message}");
        }
        return report.Ready ? 0 : 3;
    }
}
