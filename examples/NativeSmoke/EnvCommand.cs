using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>Read-only local campaign eligibility; runtime readiness is checked again under the host leases.</summary>
internal static class EnvCommand
{
    internal const string Usage = "valheim-test env preflight MANIFEST [--hosts] [--json]";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        bool json = args.Contains("--json", StringComparer.Ordinal), hosts = args.Contains("--hosts", StringComparer.Ordinal);
        if (args.Length is < 2 or > 4 || args[0] != "preflight" || args[1].StartsWith("--", StringComparison.Ordinal) ||
            args.Skip(2).Distinct(StringComparer.Ordinal).Count() != args.Length - 2 ||
            args.Skip(2).Any(arg => arg is not ("--json" or "--hosts")))
        {
            error.WriteLine("Usage: " + Usage);
            return 2;
        }
        CampaignPreflightReport report;
        try
        {
            report = hosts
                ? await HostedCampaignPreparation.InspectAsync(args[1], TimeSpan.FromSeconds(60)).ConfigureAwait(false)
                : HostedCampaignPreparation.Inspect(args[1]);
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
            foreach (var actor in report.Actors)
                output.WriteLine($"{actor.Name}: {actor.Kind} on {actor.Host} ({actor.Platform})" +
                    (actor.Environment == null ? "" : $" via {actor.Environment}: {actor.SelectionReason}"));
            if (report.Ready) output.WriteLine(hosts
                ? "READY: read-only host checks passed. Mutable state is rechecked under lease before launch."
                : "ELIGIBLE: local files, fixture and actor assignments passed. Host readiness is checked under lease before launch.");
            else foreach (var problem in report.Problems)
                output.WriteLine($"REFUSED {problem.Actor} {problem.Input}: {problem.Message}");
        }
        return report.Ready ? 0 : 3;
    }
}
