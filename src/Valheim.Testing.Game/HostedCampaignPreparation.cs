using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>One process's reviewed dependency lock and optional, explicit config/script/asset files.</summary>
public sealed class HostedCampaignRole
{
    public string DependencyLock { get; set; } = "";
    /// <summary>Ordered environment names from the private inventory; empty considers every recipe for this actor's role.</summary>
    public List<string> EnvironmentCandidates { get; set; } = [];
    /// <summary>Actor names that must be assigned to a different host.</summary>
    public List<string> DifferentHostFrom { get; set; } = [];
    /// <summary>Optional local manifest for a reviewed loader/core set applied only to the disposable runtime.</summary>
    public string? LoaderPackage { get; set; }
    public List<HostedRuntimeFile> Files { get; set; } = [];
    /// <summary>Client only: a registered character with a fresh filename and its host's local save folders.</summary>
    public HostedCampaignCharacter? Character { get; set; }
}

/// <summary>
/// Private setup input for one dedicated server and named clients. The profile supplies hosts, source installs and
/// account leases; each role has its own dependency lock so server-only mods never appear on a client by accident.
/// Paths to local inputs may be relative to this manifest. No code or dependencies are downloaded implicitly.
/// </summary>
public sealed class HostedCampaignManifest
{
    public string Profile { get; set; } = "";
    /// <summary>Private ordered host/environment inventory. Use this instead of a fixed profile.</summary>
    public string Inventory { get; set; } = "";
    /// <summary>Optional pinned world fixture for a scenario runner built on this preparation.</summary>
    public string World { get; set; } = "";
    /// <summary>The dedicated server's public or LAN game address, including port, for direct-join scenarios.</summary>
    public string Join { get; set; } = "";
    /// <summary>Optional reviewed world UID; a different fixture is refused before copying.</summary>
    public string WorldUid { get; set; } = "";
    public HostedCampaignRole Server { get; set; } = new();
    public Dictionary<string, HostedCampaignRole> Clients { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    };

    public static HostedCampaignManifest Read(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var manifest = JsonSerializer.Deserialize<HostedCampaignManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty hosted campaign manifest.");
        if (string.IsNullOrWhiteSpace(manifest.Profile) == string.IsNullOrWhiteSpace(manifest.Inventory))
            throw new InvalidDataException("Name exactly one of profile (fixed legacy assignment) or inventory (ordered environments).");
        if (manifest.Profile.Length != 0) manifest.Profile = Path.GetFullPath(manifest.Profile, directory);
        if (manifest.Inventory.Length != 0) manifest.Inventory = Path.GetFullPath(manifest.Inventory, directory);
        if (manifest.World.Length != 0) manifest.World = Path.GetFullPath(manifest.World, directory);
        if (manifest.Server == null || manifest.Clients == null) throw new InvalidDataException("A hosted campaign needs a server and named clients.");
        void Resolve(HostedCampaignRole role)
        {
            if (string.IsNullOrWhiteSpace(role.DependencyLock)) throw new InvalidDataException("Every campaign role needs its own reviewed dependencyLock.");
            if (role.EnvironmentCandidates == null || role.DifferentHostFrom == null || role.Files == null)
                throw new InvalidDataException("A campaign role's environmentCandidates, differentHostFrom and files must be lists, not null.");
            role.DependencyLock = Path.GetFullPath(role.DependencyLock, directory);
            if (role.LoaderPackage != null) role.LoaderPackage = Path.GetFullPath(role.LoaderPackage, directory);
            foreach (var file in role.Files)
                if (string.IsNullOrWhiteSpace(file.Source)) throw new InvalidDataException("An extra staged file has no source path.");
            role.Files = role.Files.Select(file => file with { Source = Path.GetFullPath(file.Source, directory) }).ToList();
            if (role.Character != null && !string.IsNullOrWhiteSpace(role.Character.Store))
                role.Character.Store = Path.GetFullPath(role.Character.Store, directory);
        }
        Resolve(manifest.Server);
        foreach (var client in manifest.Clients.Values) Resolve(client);
        return manifest;
    }
}

/// <summary>
/// Prepares every role, writes a private profile naming only the new immutable installs, and retires those installs
/// after the caller's normal pinned runner stops its processes. Run the scenario while this object is alive.
/// </summary>
public sealed class PreparedHostedCampaign : IAsyncDisposable
{
    private readonly EnvironmentProfile _profile;
    private readonly IReadOnlyList<(string Host, string Runtime, string Stage)> _copies;
    private readonly IReadOnlyList<(string Host, HostedCampaignCharacter Character)> _characters;
    private readonly Func<string, IGameHost>? _hostFactory;
    private readonly TimeSpan _timeout;
    private bool _retired;

    internal PreparedHostedCampaign(EnvironmentProfile profile, string profileFile, IReadOnlyDictionary<string, HostListing> listings,
        IReadOnlyDictionary<string, HostedRuntimeFile[]> selections,
        IReadOnlyList<(string Host, string Runtime, string Stage)> copies,
        IReadOnlyList<(string Host, HostedCampaignCharacter Character)> characters, Func<string, IGameHost>? hostFactory, TimeSpan timeout)
    {
        _profile = profile; ProfileFile = profileFile; Listings = listings; Selections = selections;
        _copies = copies; _characters = characters; _hostFactory = hostFactory; _timeout = timeout;
    }
    public string ProfileFile { get; }
    public IReadOnlyDictionary<string, HostListing> Listings { get; }
    /// <summary>Local reviewed files selected for each process, for deriving ValheimCLI MD5 pins and provenance.</summary>
    public IReadOnlyDictionary<string, HostedRuntimeFile[]> Selections { get; }

    /// <summary>Strict ValheimCLI pins derived from this role's actual selected plugin DLLs, never typed hashes.</summary>
    public Dictionary<string, string> PluginPins(string role)
    {
        if (!Selections.TryGetValue(role, out var files)) throw new ArgumentException("No prepared role " + role, nameof(role));
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.RelativePath.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) &&
                                               file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            PluginAssembly metadata;
            try { metadata = PluginMetadata.Read(file.Source); }
            catch (Exception error) when (error is BadImageFormatException or InvalidDataException) { continue; }
            foreach (var plugin in metadata.Plugins)
                if (!pins.TryAdd(plugin.Guid, Valheim.Testing.Game.PluginPins.Md5(file.Source)))
                    throw new InvalidDataException($"The {role} runtime selects BepInEx plugin {plugin.Guid} more than once.");
        }
        return pins;
    }

    /// <summary>
    /// Bind the reviewed, staged roles to a mod's pinned plan. A mod supplies its own named client sections and
    /// scenario assertions; this method handles any number of clients without assuming client-a/client-b.
    /// </summary>
    public void ApplyTo(ServerRunPlan plan, HostedCampaignManifest manifest,
        IReadOnlyDictionary<string, ClientRunPlan> clients, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(_profile.Clients.Keys))
            throw new ArgumentException("Bind exactly the prepared named clients to the plan.", nameof(clients));
        if (manifest.World.Length == 0 || manifest.Join.Length == 0)
            throw new ArgumentException("A direct-join campaign needs a fixture world and join address.");
        var identity = WorldIdentity.Read(manifest.World);
        // The dedicated server's -savedir points at the directory containing worlds_local. A bare
        // <name>/ chunked fixture beside that directory makes Valheim silently create a new world.
        // Keep the user's fixture read-only and build the exact server layout in this private output.
        string serverWorld = Path.Combine(Path.GetFullPath(outputDirectory), "server-world");
        if (Path.Exists(serverWorld)) throw new IOException("The campaign's server-world layout already exists; use a new output directory.");
        var sourceWorld = WorldFixture.Manifest(manifest.World);
        var serverHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, hash) in sourceWorld)
        {
            string targetRelative = Path.Combine("worlds_local", relative);
            string target = Path.Combine(serverWorld, targetRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(manifest.World, relative), target);
            if (!WorldFixture.Hash(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The campaign's server-world copy changed while it was staged: " + relative);
            // WorldFixture uses paths relative to the machine running the campaign.
            // Keep its native separator so a Windows-run campaign can verify the copy.
            serverHashes.Add(targetRelative, hash);
        }
        plan.World = new PinnedDirectory { Source = serverWorld, Sha256 = serverHashes };
        plan.Runtime = new PinnedDirectory { Source = _profile.Server!.Install,
            Sha256 = new Dictionary<string, string>(Listings["server"].Files, StringComparer.Ordinal) };
        plan.RuntimePins = HostInstall.Pins(Listings["server"]);
        plan.Pins = PluginPins("server");
        plan.Pins["worlduid"] = identity.UidText;
        plan.Port = _profile.Server.CliPort;
        foreach (var (name, client) in clients)
        {
            var role = _profile.Clients[name];
            client.Mode = "owned";
            client.Install = role.Install;
            client.Port = role.CliPort;
            client.InstallPins = HostInstall.Pins(Listings[name]);
            client.Pins = PluginPins(name);
            client.Character = manifest.Clients[name].Character?.FileName ??
                throw new ArgumentException($"Client {name} has no registered character.");
            client.Join = manifest.Join;
            string path = Path.Combine(Path.GetFullPath(outputDirectory), name + "-cli-manifest.json");
            NativeDependencyLock.ReadReady(manifest.Clients[name].DependencyLock).CliManifest.Write(path);
            client.CliManifest = path;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_retired) return;
        var failures = new List<Exception>();
        foreach (var character in _characters.Reverse())
        {
            try
            {
                IGameHost host = _hostFactory?.Invoke(character.Host) ?? _profile.CreateHost(character.Host);
                await using var claim = await host.AcquireLockAsync(_profile.Hosts[character.Host].Lock,
                    "character-retire " + Guid.NewGuid().ToString("N"), _timeout).ConfigureAwait(false);
                await HostedRuntimeStage.RequireStoppedAsync(host, _timeout).ConfigureAwait(false);
                await HostedCharacterStage.RetireAsync(host, character.Character, _timeout).ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(new IOException($"Failed to retire the disposable character on {character.Host}", error)); }
        }
        foreach (var copy in _copies.Reverse())
        {
            try
            {
                IGameHost host = _hostFactory?.Invoke(copy.Host) ?? _profile.CreateHost(copy.Host);
                await using var claim = await host.AcquireLockAsync(_profile.Hosts[copy.Host].Lock,
                    "campaign-retire " + Guid.NewGuid().ToString("N"), _timeout).ConfigureAwait(false);
                await HostedRuntimeStage.RequireStoppedAsync(host, _timeout, runtime: copy.Runtime, clientSession: false).ConfigureAwait(false);
                await HostedRuntimeStage.RetireAsync(host, copy.Runtime, copy.Stage, _timeout).ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(new IOException($"Failed to retire the prepared {copy.Host} runtime {copy.Runtime}", error)); }
        }
        if (failures.Count != 0) throw new AggregateException("Some prepared runtimes remain; inspect them before another run.", failures);
        _retired = true;
    }
}

/// <summary>One command's setup half: reviewed inputs become pinned, separate disposable server/client installs.</summary>
public static class HostedCampaignPreparation
{
    private sealed record Inputs(HostedCampaignManifest Manifest, EnvironmentProfile Profile,
        List<(string Name, GameRole Role, HostedCampaignRole Input)> Roles,
        Dictionary<string, HostedRuntimeFile[]> Selections, Dictionary<string, HostedCharacterSelection> Characters);

    /// <summary>Report all independent local setup problems. This does not contact a host, lease an account or copy data.</summary>
    public static CampaignPreflightReport Inspect(string manifestFile) => InspectInputs(manifestFile).Report;

    /// <summary>
    /// Read-only host readiness for a statically eligible campaign. Hosts are checked concurrently and every independent
    /// refusal is retained; preparation and launch repeat mutable checks after taking their leases.
    /// </summary>
    public static async Task<CampaignPreflightReport> InspectAsync(string manifestFile, TimeSpan timeout,
        Func<string, IGameHost>? hostFactory = null, CancellationToken cancellation = default)
    {
        var inspection = InspectInputs(manifestFile);
        return (await InspectHostsAsync(inspection, timeout, hostFactory, cancellation).ConfigureAwait(false)).Report;
    }

    private sealed record HostInspection(CampaignPreflightReport Report, IReadOnlyDictionary<string, string> Accounts,
        IReadOnlyDictionary<string, string> ObservedSteamIds, IReadOnlyDictionary<string, HostListing> SourceListings);

    private static async Task<HostInspection> InspectHostsAsync(Inspection inspection, TimeSpan timeout,
        Func<string, IGameHost>? hostFactory, CancellationToken cancellation)
    {
        if (inspection.Inputs == null) return new HostInspection(inspection.Report, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, HostListing>());
        var inputs = inspection.Inputs!;
        var failures = new ConcurrentBag<CampaignPreflightProblem>(inspection.Report.Problems);
        var matchedAccounts = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var observedSteamIds = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var sourceListings = new ConcurrentDictionary<string, HostListing>(StringComparer.Ordinal);
        var hostChecks = inputs.Roles.GroupBy(role => role.Role.Host, StringComparer.Ordinal).Select(async group =>
        {
            IGameHost host;
            try { host = hostFactory?.Invoke(group.Key) ?? inputs.Profile.CreateHost(group.Key); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failures.Add(new(group.Key, "host", error.Message));
                return;
            }
            if (group.Any(role => role.Name != "server") &&
                inputs.Profile.Hosts[group.Key] is { Kind: "local", Platform: "macos" })
            {
                try { MacGuiSession.Require(); }
                catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException)
                { failures.Add(new(group.Key, "desktop session", error.Message)); }
            }
            try
            {
                await HostedRuntimeStage.RequireStoppedAsync(host, timeout, cancellation,
                    clientSession: group.Any(role => role.Name != "server")).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or HostOperationException or IOException)
            { failures.Add(new(group.Key, "session", error.Message)); }
            await Task.WhenAll(group.Select(async item =>
            {
                try
                {
                    await HostInstall.RequirePortFreeAsync(host, item.Role.CliPort, timeout, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or HostOperationException or IOException)
                { failures.Add(new(item.Name, "ValheimCLI port", error.Message)); }
                if (!inspection.Report.Problems.Any(problem => problem.Actor == item.Name && problem.Input == "loader"))
                {
                    try
                    {
                        var loader = item.Input.LoaderPackage == null ? null : BepInExLoaderPackage.Read(item.Input.LoaderPackage);
                        sourceListings[item.Name] = await HostedRuntimeStage.InspectSourceAsync(host,
                            item.Name == "server" ? HostedRuntimeKind.Server : HostedRuntimeKind.Client,
                            item.Role.Install, loader, timeout, cancellation).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or HostOperationException or IOException)
                    { failures.Add(new(item.Name, "game and loader", error.Message)); }
                }
                if (item.Name == "server") return;
                if (inputs.Profile.SteamAccounts == null) return; // The static report already names the missing pool.
                try
                {
                    var observed = await SteamSignedInUsers.ReadAsync(host, timeout, cancellation).ConfigureAwait(false);
                    if (inputs.Profile.SteamAccounts!.ObservedLeaseDirectory != null)
                    {
                        if (observed.State != SteamSignedInState.Matches || observed.AccountId is not { } id)
                            throw new InvalidOperationException($"Client {item.Name} has no verifiable signed-in Steam identity on {host.Name}.");
                        string steamId = SteamPoolAccount.SteamId64(id);
                        observedSteamIds[item.Name] = steamId;
                        matchedAccounts[item.Name] = SteamPoolAccount.LeaseKey(steamId);
                    }
                    else
                    {
                        var matching = inputs.Profile.SteamAccounts.Candidates(item.Role)
                            .Where(account => observed.State == SteamSignedInState.Matches &&
                                SteamPoolAccount.AccountId(account.SteamId) == observed.AccountId).ToArray();
                        if (matching.Length != 1)
                            throw new InvalidOperationException($"Client {item.Name} Steam identity is unverified or does not uniquely match its expected account on {host.Name}.");
                        matchedAccounts[item.Name] = matching[0].Name;
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or HostOperationException or IOException)
                { failures.Add(new(item.Name, "Steam identity", error.Message)); }
            })).ConfigureAwait(false);
        });
        await Task.WhenAll(hostChecks).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (inputs.Profile.SteamAccounts?.ObservedLeaseDirectory != null)
            foreach (var shared in observedSteamIds.GroupBy(account => account.Value, StringComparer.Ordinal).Where(group => group.Count() > 1))
                failures.Add(new("clients", "Steam identities", "Clients " + string.Join(", ", shared.Select(account => account.Key).Order(StringComparer.Ordinal)) +
                    " use the same signed-in Steam account; choose different client environments before launch."));
        return new HostInspection(new CampaignPreflightReport(failures.OrderBy(problem => problem.Actor, StringComparer.Ordinal)
            .ThenBy(problem => problem.Input, StringComparer.Ordinal).ToArray()) { Actors = inspection.Report.Actors },
            matchedAccounts, observedSteamIds, sourceListings);
    }

    /// <summary>Require the same shared preflight used by preparation, before any host is contacted.</summary>
    public static void Check(string manifestFile) => Inspect(manifestFile).RequireReady();

    private sealed record Inspection(Inputs? Inputs, CampaignPreflightReport Report);

    private static Inspection InspectInputs(string manifestFile)
    {
        var problems = new List<CampaignPreflightProblem>();
        static bool Expected(Exception error) => error is ArgumentException or IOException or InvalidDataException or
            UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException;
        bool Try(string actor, string input, Action check)
        {
            try { check(); return true; }
            catch (Exception error) when (Expected(error))
            {
                problems.Add(new CampaignPreflightProblem(actor, input, error.Message));
                return false;
            }
        }

        HostedCampaignManifest? manifest = null;
        Try("campaign", "manifest", () => manifest = HostedCampaignManifest.Read(manifestFile));
        if (manifest == null) return new Inspection(null, new CampaignPreflightReport(problems));

        EnvironmentProfile? profile = null;
        ResolvedEnvironmentInventory? resolved = null;
        if (manifest.Inventory.Length != 0)
            Try("campaign", "inventory", () =>
            {
                resolved = EnvironmentInventory.Read(manifest.Inventory).Resolve(manifest);
                profile = resolved.Profile;
            });
        else
            Try("campaign", "profile", () => profile = EnvironmentProfile.Read(manifest.Profile));
        if (profile != null)
        {
            if (profile.Server == null) problems.Add(new("server", "role", "A hosted campaign needs a dedicated server."));
            if (profile.Clients.ContainsKey("server"))
                problems.Add(new("server", "role", "The role name server is reserved for the dedicated server."));
            if (!manifest.Clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(profile.Clients.Keys))
                problems.Add(new("campaign", "roles", "The campaign's clients must match the profile's named clients exactly."));
            if (profile.Clients.Count > 0 && profile.SteamAccounts is not { CheckSignedIn: true })
                problems.Add(new("clients", "Steam identities", "Native clients require checked Steam leases: fixed profiles pin accounts, inventories discover signed-in accounts."));
            if (profile.Clients.Count > 1 && profile.SteamAccounts is { ObservedLeaseDirectory: null } accounts)
            {
                var choices = profile.Clients.Select(client =>
                    accounts.Candidates(client.Value).Select(account => account.SteamId!).ToArray())
                    .OrderBy(names => names.Length).ToArray();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool Assign(int index)
                {
                    if (index == choices.Length) return true;
                    foreach (string name in choices[index])
                        if (used.Add(name))
                        {
                            if (Assign(index + 1)) return true;
                            used.Remove(name);
                        }
                    return false;
                }
                if (!Assign(0)) problems.Add(new("clients", "Steam identities",
                    "The Steam account pool cannot assign a different account to every simultaneous client."));
            }
        }
        if (manifest.Server.Character != null)
            problems.Add(new("server", "character", "A dedicated server has no character."));

        var selected = new Dictionary<string, HostedRuntimeFile[]>(StringComparer.Ordinal);
        var characters = new Dictionary<string, HostedCharacterSelection>(StringComparer.Ordinal);
        var manifestRoles = new[] { (Name: "server", Input: manifest.Server) }
            .Concat(manifest.Clients.OrderBy(client => client.Key, StringComparer.Ordinal)
                .Select(client => (Name: client.Key, Input: client.Value))).ToArray();
        foreach (var (name, input) in manifestRoles)
        {
            if (manifest.Profile.Length != 0 && input.EnvironmentCandidates.Count != 0)
                problems.Add(new(name, "environment candidates", "environmentCandidates needs an inventory, not a fixed profile."));
            if (manifest.Profile.Length != 0)
                foreach (string other in input.DifferentHostFrom)
                {
                    var otherRole = other == "server" ? profile?.Server : profile?.Clients.GetValueOrDefault(other);
                    var thisRole = name == "server" ? profile?.Server : profile?.Clients.GetValueOrDefault(name);
                    if (other == name || otherRole == null)
                        problems.Add(new(name, "host constraint", $"differentHostFrom names unknown or self actor {other}."));
                    else if (thisRole?.Host == otherRole.Host)
                        problems.Add(new(name, "host constraint", $"differentHostFrom requires a different host than {other}."));
                }
            if (input.LoaderPackage != null)
                Try(name, "loader", () => _ = BepInExLoaderPackage.Read(input.LoaderPackage));
            Try(name, "dependencies and CLI packs", () =>
            {
                var files = HostedRuntimeStage.FromDependencies(input.DependencyLock).Concat(input.Files).ToArray();
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (!File.Exists(file.Source)) throw new FileNotFoundException("The selected file is missing: " + file.Source, file.Source);
                    if (!paths.Add(file.RelativePath)) throw new InvalidDataException("Selected file is installed more than once: " + file.RelativePath);
                }
                selected[name] = files;
            });
            if (name == "server") continue;
            if (input.Character == null)
                problems.Add(new(name, "character", "A native client needs a registered, disposable character."));
            else
                Try(name, "character", () => characters[name] = HostedCharacterStage.Select(input.Character,
                    Path.GetDirectoryName(Path.GetFullPath(manifestFile))!));
        }
        if (characters.Values.Select(character => character.Handle.PlayerId).Distinct().Count() != characters.Count)
            problems.Add(new("clients", "characters", "Simultaneous clients need different registered character player IDs; copies of one seed are one player."));

        if (manifest.World.Length != 0)
            Try("server", "world fixture", () =>
            {
                var identity = WorldIdentity.Read(manifest.World);
                if (manifest.WorldUid.Length > 0 && !identity.UidText.Equals(manifest.WorldUid, StringComparison.Ordinal))
                    throw new InvalidDataException("The fixture world UID does not match the campaign's reviewed worldUid.");
                _ = WorldFixture.Manifest(manifest.World);
            });
        else if (manifest.WorldUid.Length > 0)
            problems.Add(new("server", "world fixture", "worldUid needs a fixture world."));

        CampaignPreflightActor[] actors = profile == null ? [] : manifestRoles
            .Where(role => role.Name == "server" ? profile.Server != null : profile.Clients.ContainsKey(role.Name))
            .Select(role =>
            {
                var selectedRole = role.Name == "server" ? profile.Server! : profile.Clients[role.Name];
                var assignment = resolved?.Assignments.FirstOrDefault(item => item.Actor == role.Name);
                return new CampaignPreflightActor(role.Name, role.Name == "server" ? "dedicated-server" : "client",
                    selectedRole.Host, profile.Hosts.TryGetValue(selectedRole.Host, out var host) ? host.Platform : "unknown")
                { Environment = assignment?.Environment, SelectionReason = assignment?.Reason };
            }).ToArray();
        var report = new CampaignPreflightReport(problems) { Actors = actors };
        if (profile?.Server == null || !manifest.Clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(profile.Clients.Keys))
            return new Inspection(null, report);
        var roles = new List<(string Name, GameRole Role, HostedCampaignRole Input)>
            { ("server", profile.Server, manifest.Server) };
        roles.AddRange(profile.Clients.Select(client => (client.Key, client.Value, manifest.Clients[client.Key])));
        return new Inspection(new Inputs(manifest, profile, roles, selected, characters), report);
    }

    /// <summary>Turn observed, distinct signed-in identities into the private fixed profile used by the runner.</summary>
    internal static void CompleteObservedSteamAccounts(EnvironmentProfile profile, IReadOnlyDictionary<string, string> observedIds)
    {
        var section = profile.SteamAccounts ?? throw new ArgumentException("The inventory has no Steam lease section.", nameof(profile));
        string directory = section.ObservedLeaseDirectory ?? throw new ArgumentException("This profile does not use observed Steam identities.", nameof(profile));
        if (!profile.Clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(observedIds.Keys) ||
            observedIds.Values.Distinct(StringComparer.Ordinal).Count() != observedIds.Count)
            throw new ArgumentException("Every client needs a different, verified signed-in Steam identity.", nameof(observedIds));
        var pool = new SteamAccountPool
        {
            Pool = "steam-clients", LeaseDirectory = directory, SteamGuard = SteamAccountPool.SignedIn,
            Accounts = observedIds.Values.Select(id => new SteamPoolAccount
            { Name = SteamPoolAccount.LeaseKey(id), SteamId = id }).ToList(),
        };
        section.ObservedLeaseDirectory = null;
        section.InlinePool = pool;
        section.Accounts = pool;
        foreach (var (client, id) in observedIds)
            profile.Clients[client].SteamAccount = SteamPoolAccount.LeaseKey(id);
        profile.Validate();
    }

    public static async Task<PreparedHostedCampaign> PrepareAsync(string manifestFile, string outputDirectory, TimeSpan timeout,
        Func<string, IGameHost>? hostFactory = null, CancellationToken cancellation = default)
    {
        var inspection = InspectInputs(manifestFile);
        inspection.Report.RequireReady();
        var readiness = await InspectHostsAsync(inspection, timeout, hostFactory, cancellation).ConfigureAwait(false);
        readiness.Report.RequireReady();
        var inputs = inspection.Inputs!;
        var (manifest, profile, roles, selections, characters) = inputs;
        if (profile.SteamAccounts?.ObservedLeaseDirectory != null)
            CompleteObservedSteamAccounts(profile, readiness.ObservedSteamIds);
        else
            foreach (var client in profile.Clients)
                client.Value.SteamAccount = readiness.Accounts[client.Key];
        profile.Validate();
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Use a new private campaign output directory: " + output);
        Directory.CreateDirectory(output);
        if (manifest.Inventory.Length != 0)
            File.WriteAllText(Path.Combine(output, "environment-assignments.json"),
                JsonSerializer.Serialize(readiness.Report.Actors, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        foreach (var (name, role, _) in roles)
        {
            const string configName = "BepInEx/config/valheimCLI.valheimCLI.cfg";
            if (selections[name].Any(file => file.RelativePath.Equals(configName, StringComparison.OrdinalIgnoreCase))) continue;
            string config = Path.Combine(output, "inputs", name, "valheimCLI.valheimCLI.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "[Server]\nEnabled = true\nAllowOnServerClients = true\nPort = " +
                role.CliPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            selections[name] = [.. selections[name], new HostedRuntimeFile(config, configName)];
        }
        string id = Guid.NewGuid().ToString("N");
        var copies = new ConcurrentBag<(string Host, string Runtime, string Stage)>();
        var stagedCharacters = new ConcurrentBag<(string Host, HostedCampaignCharacter Character)>();
        var listings = new ConcurrentDictionary<string, HostListing>(StringComparer.Ordinal);
        try
        {
            // A host has one claim for the whole preparation. Independent actors prepare concurrently, including
            // a server and client on the same host; their runtime and character paths are disjoint. We wait for
            // every actor to settle before releasing claims or cleaning up a failed campaign.
            await Task.WhenAll(roles.GroupBy(item => item.Role.Host, StringComparer.Ordinal).Select(async group =>
            {
                cancellation.ThrowIfCancellationRequested();
                string hostName = group.Key;
                IGameHost host = hostFactory?.Invoke(hostName) ?? profile.CreateHost(hostName);
                await using var claim = await host.AcquireLockAsync(profile.Hosts[hostName].Lock,
                    "campaign-prepare " + id + " " + hostName, timeout, cancellation).ConfigureAwait(false);
                await HostedRuntimeStage.RequireStoppedAsync(host, timeout, cancellation, clientSession: group.Any(item => item.Name != "server")).ConfigureAwait(false);
                await Task.WhenAll(group.Select(async item =>
                {
                    var (name, role, _) = item;
                    string parent = HostInstall.Join(role.Runtime, "vt-prep-" + id + "-" + name);
                    string runtime = HostInstall.Join(parent, "runtime"), stage = HostInstall.Join(parent, "staging");
                    listings[name] = await HostedRuntimeStage.PrepareWithInspectedSourceAsync(host, name == "server" ? HostedRuntimeKind.Server : HostedRuntimeKind.Client,
                        role.Install, runtime, stage, selections[name], timeout, cancellation,
                        item.Input.LoaderPackage == null ? null : BepInExLoaderPackage.Read(item.Input.LoaderPackage),
                        readiness.SourceListings[name]).ConfigureAwait(false);
                    copies.Add((hostName, runtime, stage));
                    if (characters.TryGetValue(name, out var character))
                    {
                        await HostedCharacterStage.StageAsync(host, character, HostInstall.Join(parent, "character-stage"), timeout, cancellation).ConfigureAwait(false);
                        stagedCharacters.Add((hostName, character.Input));
                    }
                    role.Install = runtime;
                })).ConfigureAwait(false);
            })).ConfigureAwait(false);
            // The pool is relative to the original profile. Preserve its absolute resolved path in the generated copy.
            if (profile.SteamAccounts?.PoolFile is { } pool) profile.SteamAccounts.Pool = pool;
            string preparedProfile = Path.Combine(output, "profile.json");
            File.WriteAllText(preparedProfile, JsonSerializer.Serialize(profile, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            }) + "\n");
            _ = EnvironmentProfile.Read(preparedProfile);
            return new PreparedHostedCampaign(profile, preparedProfile, listings, selections, copies.ToArray(), stagedCharacters.ToArray(), hostFactory, timeout);
        }
        catch (Exception original)
        {
            try
            {
                await new PreparedHostedCampaign(profile, "", listings, selections, copies.ToArray(), stagedCharacters.ToArray(), hostFactory, timeout).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup) { throw new AggregateException("Campaign preparation failed and cleanup was not proven.", original, cleanup); }
            throw;
        }
    }
}
