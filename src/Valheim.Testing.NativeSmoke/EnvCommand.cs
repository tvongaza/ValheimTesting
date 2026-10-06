using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>
/// The environments: what the inventory holds (<c>list</c>), whether a one-off can run on it (<c>preflight</c>, read only), and
/// what earlier runs left on each host, from their journals (<c>status</c>, <c>recover</c>, <c>teardown</c>). A session's own
/// check is <see cref="SessionCommand"/>.
/// </summary>
internal static class EnvCommand
{
    internal const string Usage = "valheim-test env list|preflight [--inventory FILE] [--json] | valheim-test env status [--inventory FILE] [--json] | " +
        "valheim-test env recover|teardown --run ID [--inventory FILE] [--json] | valheim-test env teardown --copy PATH [--inventory FILE] | " +
        "valheim-test env teardown --run ID --machine-gone [--inventory FILE]";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length != 0 && args[0] is "status" or "recover" or "teardown") return await Journal(args[0], args[1..], output, error).ConfigureAwait(false);
        var rest = args.Skip(1).ToList();
        bool json = rest.Remove("--json");
        string? inventoryFile = Option(rest, "--inventory");
        if (args.Length == 0 || args[0] is not ("list" or "preflight") || rest.Count != 0)
        {
            // The old form, env preflight SESSION [--hosts], names the command that checks a session now.
            bool session = args.Length != 0 && args[0] == "preflight" && rest.Any(arg => !arg.StartsWith("--", StringComparison.Ordinal));
            error.WriteLine("Usage: " + Usage + (session ? ". A session's check is valheim-test session check SESSION [--hosts] [--json]." : ""));
            return 2;
        }
        return await Inventory(inventoryFile, args[0] == "preflight", json, output, error).ConfigureAwait(false);
    }

    // The value after a single option, removed from rest; a missing value (or another option in its place) leaves the option
    // in rest, which the caller refuses as a usage error.
    private static string? Option(List<string> rest, string name)
    {
        int at = rest.IndexOf(name);
        if (at < 0 || at + 1 >= rest.Count || rest[at + 1].StartsWith("--", StringComparison.Ordinal)) return null;
        string value = rest[at + 1];
        rest.RemoveRange(at, 2);
        return value;
    }

    // No session: what the inventory holds (this machine, or the file with its local environments filled in). list shows it and
    // gives no verdict; preflight shows the same and says whether a one-off can run on it: a server and a client, and no run of
    // another process going on this machine or left unrecovered by its journal (#257).
    private static async Task<int> Inventory(string? file, bool preflight, bool json, TextWriter output, TextWriter error)
    {
        EnvironmentInventory inventory;
        try { inventory = EnvironmentInventory.Read(file == null ? null : Path.GetFullPath(file)); }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
        var missingRoles = new[] { "server", "client" }.Where(role => !inventory.Environments.Any(recipe => recipe.Roles.Contains(role))).ToArray();
        var problems = preflight ? await inventory.LocalJournalProblemsAsync().ConfigureAwait(false) : [];
        bool ready = missingRoles.Length == 0 && problems.Count == 0;
        if (json)
        {
            var listing = new Dictionary<string, object?>
            {
                ["Detected"] = inventory.Detected, ["Missing"] = inventory.Missing,
                ["Environments"] = inventory.Environments.Select(recipe => new { recipe.Name, recipe.Host, recipe.Roles, recipe.Install, recipe.Runtime, recipe.CliPort, recipe.GamePort }),
            };
            if (preflight) { listing["Problems"] = problems; listing["Ready"] = ready; }
            output.WriteLine(JsonSerializer.Serialize(listing, new JsonSerializerOptions { WriteIndented = true }));
            return preflight && !ready ? 3 : 0;
        }
        foreach (string line in inventory.Detected) output.WriteLine("detected: " + line);
        foreach (string line in inventory.Missing) output.WriteLine("NOT FOUND: " + line);
        foreach (var recipe in inventory.Environments)
            output.WriteLine($"{recipe.Name}: {string.Join(" and ", recipe.Roles)} on {recipe.Host}; install {recipe.Install}; runtime {recipe.Runtime}; " +
                $"ValheimCLI port {recipe.CliPort}" + (recipe.Roles.Contains("server") ? $", game port {recipe.GamePort}" : ""));
        if (!preflight) return 0;
        foreach (var problem in problems) output.WriteLine($"REFUSED {problem.Actor} {problem.Input}: {problem.Message}");
        output.WriteLine(missingRoles.Length != 0 ? "REFUSED: the inventory has no " + string.Join(" and no ", missingRoles) + " environment."
            : problems.Count != 0 ? "REFUSED: a run on this machine is going or was left unrecovered; see valheim-test env status."
            : "ELIGIBLE: the inventory has a server and a client environment, and this machine's journal holds no run going or left unrecovered. " +
              "A session's inputs and host readiness are checked by valheim-test session check SESSION [--hosts].");
        return ready ? 0 : 3;
    }

    // From each host's run journal: status (what earlier runs left; changes nothing), recover (clear what one run provably
    // left, except what it kept on purpose) or teardown (that too, or one unjournalled copy by --copy; with --machine-gone, only the
    // Steam leases of a run whose machine is gone for good). Exit 0 when nothing is left (status: of any run; recover and
    // teardown: of that run); 3 otherwise or when refused; 2 for a usage error.
    private static async Task<int> Journal(string action, string[] args, TextWriter output, TextWriter error)
    {
        var rest = args.ToList();
        bool json = rest.Remove("--json"), machineGone = action == "teardown" && rest.Remove("--machine-gone");
        string? file = Option(rest, "--inventory"), run = action == "status" ? null : Option(rest, "--run"), copy = action == "teardown" ? Option(rest, "--copy") : null;
        if (rest.Count != 0 || (action != "status" && (run == null) == (copy == null)) || (copy != null && json) || (machineGone && (run == null || json)))
        {
            error.WriteLine("Usage: " + Usage);
            return 2;
        }
        try
        {
            string? inventory = file == null ? null : Path.GetFullPath(file);
            bool clean = action == "status" ? await EnvironmentRuns.WriteRunStatusAsync(inventory, output, json).ConfigureAwait(false)
                : copy != null ? await EnvironmentRuns.TeardownCopyAsync(inventory, copy, output).ConfigureAwait(false)
                : machineGone ? await EnvironmentRuns.ReleaseLeasesOfGoneRunAsync(inventory, run!, output).ConfigureAwait(false)
                : await EnvironmentRuns.RecoverRunAsync(inventory, run!, action == "teardown", output, json).ConfigureAwait(false);
            return clean ? 0 : 3;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or HostOperationException)
        {
            error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
    }
}
