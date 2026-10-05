using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>
/// Read-only local campaign eligibility (runtime readiness is checked again under the host leases), and what earlier runs left
/// on each host, from their journals (<c>status</c>).
/// </summary>
internal static class EnvCommand
{
    internal const string Usage = "valheim-test env preflight [MANIFEST [--hosts] | --inventory FILE] [--json] | valheim-test env status [--inventory FILE] [--json]";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length != 0 && args[0] == "status") return await Status(args[1..], output, error).ConfigureAwait(false);
        var rest = args.Skip(1).ToList();
        bool json = rest.Remove("--json"), hosts = rest.Remove("--hosts");
        string? inventoryFile = null;
        int at = rest.IndexOf("--inventory");
        if (at >= 0 && at + 1 < rest.Count) { inventoryFile = rest[at + 1]; rest.RemoveRange(at, 2); }
        string? manifest = rest.Count == 1 && !rest[0].StartsWith("--", StringComparison.Ordinal) ? rest[0] : null;
        if (args.Length == 0 || args[0] != "preflight" || rest.Count > (manifest == null ? 0 : 1) || (at >= 0 && inventoryFile == null) ||
            rest.Contains("--json") || rest.Contains("--hosts") || (manifest != null && inventoryFile != null) || (hosts && manifest == null))
        {
            error.WriteLine("Usage: " + Usage);
            return 2;
        }
        return manifest == null ? Inventory(inventoryFile, json, output, error) : await Campaign(manifest, hosts, json, output, error).ConfigureAwait(false);
    }

    // No campaign: what the inventory holds (this machine, or the file with its local environments filled in). A one-off needs a server and a client.
    private static int Inventory(string? file, bool json, TextWriter output, TextWriter error)
    {
        EnvironmentInventory inventory;
        try { inventory = EnvironmentInventory.Read(file == null ? null : Path.GetFullPath(file)); }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
        var missingRoles = new[] { "server", "client" }.Where(role => !inventory.Environments.Any(recipe => recipe.Roles.Contains(role))).ToArray();
        if (json)
            output.WriteLine(JsonSerializer.Serialize(new
            {
                inventory.Detected, inventory.Missing,
                Environments = inventory.Environments.Select(recipe => new { recipe.Name, recipe.Host, recipe.Roles, recipe.Install, recipe.Runtime, recipe.CliPort, recipe.GamePort }),
                Ready = missingRoles.Length == 0,
            }, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Detected(inventory.Detected, output);
            foreach (string line in inventory.Missing) output.WriteLine("NOT FOUND: " + line);
            output.WriteLine(missingRoles.Length == 0
                ? "ELIGIBLE: the inventory has a server and a client environment. Campaign inputs and host readiness are checked when a campaign is given."
                : "REFUSED: the inventory has no " + string.Join(" and no ", missingRoles) + " environment.");
        }
        return missingRoles.Length == 0 ? 0 : 3;
    }

    private static async Task<int> Campaign(string manifest, bool hosts, bool json, TextWriter output, TextWriter error)
    {
        CampaignPreflightReport report;
        try
        {
            report = hosts
                ? await HostedCampaignPreparation.InspectAsync(manifest, TimeSpan.FromSeconds(60)).ConfigureAwait(false)
                : HostedCampaignPreparation.Inspect(manifest);
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
            Detected(report.Detected, output);
            foreach (var actor in report.Actors)
            {
                output.WriteLine($"{actor.Name}: {actor.Kind} on {actor.Host} ({actor.Platform})" +
                    (actor.Environment == null ? "" : $" via {actor.Environment}: {actor.SelectionReason}"));
                if (actor.CharactersDirectory != null)
                    output.WriteLine($"  characters_local {actor.CharactersDirectory}; Steam userdata {actor.SteamUserDataDirectory}");
            }
            if (report.Ready) output.WriteLine(hosts
                ? "READY: read-only host checks passed. Mutable state is rechecked under lease before launch."
                : "ELIGIBLE: local files, fixture and actor assignments passed. Host readiness is checked under lease before launch.");
            else foreach (var problem in report.Problems)
                output.WriteLine($"REFUSED {problem.Actor} {problem.Input}: {problem.Message}");
        }
        return report.Ready ? 0 : 3;
    }

    // What earlier runs left on every host of the inventory, from each host's run journal. Changes nothing. Exit 0 when every
    // host was read and every run left nothing; 3 otherwise; 2 for a usage error.
    private static async Task<int> Status(string[] args, TextWriter output, TextWriter error)
    {
        var rest = args.ToList();
        bool json = rest.Remove("--json");
        string? file = null;
        int at = rest.IndexOf("--inventory");
        if (at >= 0 && at + 1 < rest.Count) { file = rest[at + 1]; rest.RemoveRange(at, 2); }
        if (rest.Count != 0 || (at >= 0 && file == null))
        {
            error.WriteLine("Usage: " + Usage);
            return 2;
        }
        try { return await EnvironmentInventory.WriteRunStatusAsync(file == null ? null : Path.GetFullPath(file), output, json).ConfigureAwait(false) ? 0 : 3; }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
    }

    private static void Detected(IReadOnlyList<string> lines, TextWriter output)
    {
        foreach (string line in lines) output.WriteLine("detected: " + line);
    }
}
