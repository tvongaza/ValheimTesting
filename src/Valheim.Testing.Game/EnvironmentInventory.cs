using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>One preferred launch recipe in a private operator inventory; it is never a running process.</summary>
public sealed class EnvironmentRecipe
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary><c>server</c>, <c>client</c> or <c>hosting-client</c>. The latter is one process with both capabilities.</summary>
    public List<string> Roles { get; set; } = [];
    public string Install { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int CliPort { get; set; }
    public int LocalCliPort { get; set; }
    public int GamePort { get; set; }
    public string? LoaderPackage { get; set; }

    internal GameRole Role() => new()
    {
        Host = Host, Install = Install, Runtime = Runtime, CliPort = CliPort,
        LocalCliPort = LocalCliPort, GamePort = GamePort,
    };
}

/// <summary>Why a named campaign actor was assigned to one inventory recipe.</summary>
public sealed record EnvironmentAssignment(string Actor, string Environment, string Role, string Host, string Reason);

/// <summary>The resolved fixed profile used by the existing host lifecycle, and its reviewable assignments.</summary>
public sealed record ResolvedEnvironmentInventory(EnvironmentProfile Profile,
    IReadOnlyList<EnvironmentAssignment> Assignments);

/// <summary>
/// Ordered, private operator inventory. Client identities are discovered from their signed-in Steam environments;
/// a campaign supplies actors and its own dependency locks. Resolution is deterministic and has no host effects.
/// </summary>
public sealed class EnvironmentInventory
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public Dictionary<string, HostProfile> Hosts { get; set; } = [];
    public List<EnvironmentRecipe> Environments { get; set; } = [];
    /// <summary>The host that stores Steam-account leases; required if any client recipe exists.</summary>
    public string LeaseHost { get; set; } = "";
    /// <summary>An absolute lease directory on <see cref="LeaseHost"/>, shared by inventories using these Steam accounts.</summary>
    public string LeaseDirectory { get; set; } = "";

    public static EnvironmentInventory Read(string path)
    {
        string content = File.ReadAllText(path);
        var inventory = JsonSerializer.Deserialize<EnvironmentInventory>(content, Json)
            ?? throw new InvalidDataException("Empty environment inventory.");
        inventory.Validate(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return inventory;
    }

    /// <summary>Validate the whole inventory before any actor assignment or host contact.</summary>
    public void Validate(string directory)
    {
        var errors = new List<string>();
        if (Hosts == null || Environments == null || Environments.Any(recipe => recipe == null) ||
            Hosts.Any(host => host.Value == null))
            throw new ArgumentException("Invalid environment inventory: hosts and environments must contain objects, not null.");
        if (Hosts.Count == 0) errors.Add("List at least one host.");
        foreach (var (name, host) in Hosts)
        {
            if (!NamePattern.IsMatch(name)) errors.Add("Invalid host name: " + name);
            host.Validate(name, errors);
        }
        if (Environments.Count == 0) errors.Add("List at least one environment recipe in preference order.");
        foreach (var recipe in Environments)
        {
            if (recipe.Roles == null)
            { errors.Add($"Environment {recipe.Name} needs a roles list."); continue; }
            if (!NamePattern.IsMatch(recipe.Name ?? "")) errors.Add("Invalid environment name: " + recipe.Name);
            if (recipe.Host == null || !Hosts.TryGetValue(recipe.Host, out var host))
            { errors.Add($"Environment {recipe.Name} names unknown host {recipe.Host}."); continue; }
            if (recipe.Roles.Count == 0 || recipe.Roles.Any(role => role is not ("server" or "client" or "hosting-client")) ||
                recipe.Roles.Distinct(StringComparer.Ordinal).Count() != recipe.Roles.Count)
                errors.Add($"Environment {recipe.Name} needs distinct server, client or hosting-client roles.");
            if (recipe.Roles.Contains("server") && recipe.Roles.Count != 1)
                errors.Add($"Environment {recipe.Name}: a dedicated server recipe cannot also be a client process.");
            if (!host.IsAbsolutePath(recipe.Install) || !host.IsAbsolutePath(recipe.Runtime))
                errors.Add($"Environment {recipe.Name} needs absolute install and runtime paths on {recipe.Host}.");
            if (recipe.CliPort is < 1024 or > 65535 || recipe.LocalCliPort is < 0 or > 65535)
                errors.Add($"Environment {recipe.Name} needs valid ValheimCLI ports.");
            bool serves = recipe.Roles.Contains("server") || recipe.Roles.Contains("hosting-client");
            if (serves ? recipe.GamePort is < 1024 or > 65534 : recipe.GamePort != 0)
                errors.Add($"Environment {recipe.Name} needs a gamePort only when it serves a world.");
            foreach (string role in recipe.Roles.Where(role => role is "server" or "client"))
                recipe.Role().Validate(role == "server" ? "server" : "client " + recipe.Name, host, errors);
            if (recipe.Roles.Contains("server") && host.Platform == "macos")
                errors.Add($"Environment {recipe.Name}: remote macOS dedicated servers are not supported by this campaign runner.");
            if (recipe.LoaderPackage != null)
                recipe.LoaderPackage = Path.GetFullPath(recipe.LoaderPackage, directory);
        }
        foreach (var duplicate in Environments.GroupBy(recipe => recipe.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add("Environment " + duplicate.Key + " is listed twice.");
        bool hasClients = Environments.Any(recipe => recipe.Roles.Contains("client") || recipe.Roles.Contains("hosting-client"));
        if (hasClients && (string.IsNullOrWhiteSpace(LeaseHost) || !Hosts.TryGetValue(LeaseHost, out var leaseHost) ||
            string.IsNullOrWhiteSpace(LeaseDirectory) || !leaseHost.IsAbsolutePath(LeaseDirectory)))
            errors.Add("Client environments need a listed leaseHost and an absolute leaseDirectory on it.");
        if (errors.Count != 0) throw new ArgumentException("Invalid environment inventory: " + string.Join(" ", errors));
    }

    /// <summary>Assign the campaign's actors in order, backtracking when an earlier choice blocks a later one.</summary>
    public ResolvedEnvironmentInventory Resolve(HostedCampaignManifest campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var actors = new[] { (Name: "server", Kind: "server", Input: campaign.Server) }
            .Concat(campaign.Clients.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (Name: pair.Key, Kind: "client", Input: pair.Value))).ToArray();
        var names = actors.Select(actor => actor.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var actor in actors)
            foreach (string other in actor.Input.DifferentHostFrom)
                if (other == actor.Name || !names.Contains(other))
                    throw new ArgumentException($"Actor {actor.Name} names unknown or self differentHostFrom actor {other}.");
        var chosen = new Dictionary<string, EnvironmentRecipe>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        string? failedActor = null;
        var refusals = new List<string>();
        bool Search(int index)
        {
            if (index == actors.Length) return true;
            var actor = actors[index];
            var wanted = actor.Input.EnvironmentCandidates;
            var candidates = wanted.Count == 0 ? Environments : wanted.Select(name =>
                Environments.FirstOrDefault(recipe => recipe.Name == name) ??
                throw new ArgumentException($"Actor {actor.Name} requests unknown environment {name}.")).ToList();
            var skipped = new List<string>();
            foreach (var recipe in candidates)
            {
                string? refusal = Refusal(actor.Name, actor.Kind, actor.Input, recipe, chosen, actors);
                if (refusal != null) { skipped.Add(recipe.Name + ": " + refusal); continue; }
                chosen[actor.Name] = recipe;
                reasons[actor.Name] = skipped.Count == 0 ? "first compatible recipe in inventory order"
                    : "first compatible recipe after " + string.Join("; ", skipped);
                if (Search(index + 1)) return true;
                chosen.Remove(actor.Name);
                skipped.Add(recipe.Name + ": leaves a later actor without a compatible environment");
            }
            if (failedActor == null) { failedActor = actor.Name; refusals.AddRange(skipped); }
            return false;
        }
        if (!Search(0)) throw new ArgumentException($"No environment assignment for {failedActor}: " + string.Join("; ", refusals));
        var profile = new EnvironmentProfile
        {
            Hosts = Hosts,
            Server = chosen["server"].Role(),
            Clients = campaign.Clients.Keys.ToDictionary(name => name, name => chosen[name].Role(), StringComparer.Ordinal),
        };
        if (profile.Clients.Count > 0)
        {
            profile.SteamAccounts = new SteamAccountsProfile
            {
                LeaseHost = LeaseHost, CheckSignedIn = true, ObservedLeaseDirectory = LeaseDirectory,
            };
        }
        profile.Validate();
        foreach (var actor in actors)
            if (actor.Input.LoaderPackage == null) actor.Input.LoaderPackage = chosen[actor.Name].LoaderPackage;
        var assignments = actors.Select(actor => new EnvironmentAssignment(actor.Name, chosen[actor.Name].Name,
            actor.Kind == "server" ? "dedicated-server" : "client", chosen[actor.Name].Host,
            reasons[actor.Name])).ToArray();
        return new ResolvedEnvironmentInventory(profile, assignments);
    }

    private static string? Refusal(string actor, string kind, HostedCampaignRole input, EnvironmentRecipe recipe,
        IReadOnlyDictionary<string, EnvironmentRecipe> chosen,
        IReadOnlyList<(string Name, string Kind, HostedCampaignRole Input)> actors)
    {
        if (!recipe.Roles.Contains(kind)) return "does not support " + kind;
        if (input.LoaderPackage != null && recipe.LoaderPackage != null &&
            !input.LoaderPackage.Equals(recipe.LoaderPackage, StringComparison.Ordinal))
            return "campaign and recipe name different loader packages";
        if (chosen.Values.Any(other => other.Name == recipe.Name)) return "already assigned to another actor";
        if (chosen.Values.Any(other => other.Host == recipe.Host && other.CliPort == recipe.CliPort))
            return "ValheimCLI port conflicts on host " + recipe.Host;
        if (recipe.LocalCliPort != 0 && chosen.Values.Any(other => other.LocalCliPort == recipe.LocalCliPort))
            return "local ValheimCLI tunnel port conflicts";
        if (kind == "client")
        {
            if (chosen.Any(other => other.Key != "server" && other.Value.Host == recipe.Host))
                return "another client uses this host's desktop session";
        }
        foreach (var other in chosen)
        {
            bool distinct = actors.First(item => item.Name == actor).Input.DifferentHostFrom.Contains(other.Key) ||
                actors.First(item => item.Name == other.Key).Input.DifferentHostFrom.Contains(actor);
            if (distinct && other.Value.Host == recipe.Host) return "differentHostFrom requires a different host than " + other.Key;
        }
        return null;
    }
}
