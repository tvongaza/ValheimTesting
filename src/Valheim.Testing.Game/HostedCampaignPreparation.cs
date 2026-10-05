using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using valheimCLI;

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
/// Private setup input for one dedicated server and named clients: the campaign's actor declaration. The inventory supplies hosts,
/// source installs and the Steam lease location; each role has its own dependency lock so server-only mods never appear on a
/// client by accident.
/// Paths to local inputs may be relative to this manifest. No code or dependencies are downloaded implicitly.
/// </summary>
public sealed class HostedCampaignManifest
{
    /// <summary>
    /// The private ordered host/environment inventory (<see cref="EnvironmentInventory"/>) the actors are assigned from. Left
    /// out, this machine alone (<see cref="EnvironmentInventory.Read(string?)"/> with no file).
    /// </summary>
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
        string json = File.ReadAllText(path);
        using (var document = JsonDocument.Parse(json))
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("profile", out _))
                throw new InvalidDataException("A campaign no longer takes a fixed environment profile: replace profile with inventory, " +
                    "a private environment inventory listing the hosts and environments its actors are assigned from.");
        var manifest = JsonSerializer.Deserialize<HostedCampaignManifest>(json, Json)
            ?? throw new InvalidDataException("Empty hosted campaign manifest.");
        if (manifest.Inventory == null) throw new InvalidDataException("A campaign's inventory is a path, not null; leave it out for this machine.");
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
/// Every role prepared: the resolved environment, in memory, names only the new immutable installs and the observed Steam
/// identities. <see cref="PinnedServerRun.RunCampaignAsync{TPlan}"/> runs a plan on it and retires those installs after its
/// processes stop. Run the scenario while this object is alive.
/// </summary>
public sealed class PreparedHostedCampaign : IAsyncDisposable
{
    private readonly ResolvedEnvironment _profile;
    private readonly IReadOnlyList<(string Host, string Actor, string Runtime, string Stage)> _copies;
    private readonly IReadOnlyList<(string Host, string Actor, HostedCampaignCharacter Character)> _characters;
    private readonly Func<string, IGameHost>? _hostFactory;
    private readonly TimeSpan _timeout;

    internal PreparedHostedCampaign(HostedCampaignManifest manifest, ResolvedEnvironment profile, IReadOnlyDictionary<string, HostListing> listings,
        IReadOnlyDictionary<string, HostedRuntimeFile[]> selections,
        IReadOnlyList<(string Host, string Actor, string Runtime, string Stage)> copies,
        IReadOnlyList<(string Host, string Actor, HostedCampaignCharacter Character)> characters, Func<string, IGameHost>? hostFactory, TimeSpan timeout,
        RunJournal journal)
    {
        Journal = journal;
        Manifest = manifest; _profile = profile; Listings = listings; Selections = selections;
        _copies = copies; _characters = characters; _hostFactory = hostFactory; _timeout = timeout;
    }
    /// <summary>The campaign as it was read for this preparation: binding uses it, not the file read again later.</summary>
    internal HostedCampaignManifest Manifest { get; }
    /// <summary>The resolved environment the runner places the server and clients with; never written to a file.</summary>
    internal ResolvedEnvironment Environment => _profile;
    public IReadOnlyDictionary<string, HostListing> Listings { get; }
    /// <summary>Local reviewed files selected for each process, for deriving ValheimCLI MD5 pins and provenance.</summary>
    public IReadOnlyDictionary<string, HostedRuntimeFile[]> Selections { get; }

    /// <summary>Strict ValheimCLI pins derived from this role's actual selected plugin DLLs, never typed hashes.</summary>
    public Dictionary<string, string> PluginPins(string role)
    {
        if (!Selections.TryGetValue(role, out var files)) throw new ArgumentException("No prepared role " + role, nameof(role));
        return HostedCampaignPreparation.PluginPins(role, files);
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
            if (!FileHash.Sha256(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The campaign's server-world copy changed while it was staged: " + relative);
            // WorldFixture uses paths relative to the machine running the campaign.
            // Keep its native separator so a Windows-run campaign can verify the copy.
            serverHashes.Add(targetRelative, hash);
        }
        plan.World = new PinnedDirectory { Source = serverWorld, Sha256 = serverHashes };
        plan.Runtime = new PinnedDirectory { Source = _profile.Server!.Install,
            Sha256 = new Dictionary<string, string>(Listings["server"].Files, StringComparer.Ordinal) };
        plan.RuntimePins = HostInstall.Pins(Listings["server"]);
        // The selected files' own hashes replace the plan's plugin pins; its world expectations stay, worlduid is the fixture's.
        plan.Pins = Bound(plan.Pins, PluginPins("server"));
        plan.Pins["worlduid"] = identity.UidText;
        plan.Port = _profile.Server.CliPort;
        foreach (var (name, client) in clients)
        {
            var role = _profile.Clients[name];
            client.Mode = "owned";
            client.Install = role.Install;
            client.Port = role.CliPort;
            client.InstallPins = HostInstall.Pins(Listings[name]);
            client.Pins = Bound(client.Pins, PluginPins(name));
            client.Character = manifest.Clients[name].Character?.FileName ??
                throw new ArgumentException($"Client {name} has no registered character.");
            client.Join = manifest.Join;
            string path = Path.Combine(Path.GetFullPath(outputDirectory), name + "-cli-manifest.json");
            NativeDependencyLock.ReadReady(manifest.Clients[name].DependencyLock).CliManifest.Write(path);
            client.CliManifest = path;
        }
    }

    // A plan's own world keys and absent plugins, then the role's selected plugins by their files' MD5.
    private static Dictionary<string, string> Bound(IReadOnlyDictionary<string, string> planned, Dictionary<string, string> selected)
    {
        foreach (var (key, value) in planned.Where(pin => Expectations.IsWorldKey(pin.Key) || pin.Value == "absent"))
            if (!selected.ContainsKey(key)) selected[key] = value;
        return selected;
    }

    internal IReadOnlyList<(string Host, string Actor, string Runtime, string Stage)> Copies => _copies;
    internal IReadOnlyList<(string Host, string Actor, HostedCampaignCharacter Character)> Characters => _characters;
    internal IGameHost HostFor(string name) => _hostFactory?.Invoke(name) ?? _profile.CreateHost(name);
    internal string LockOf(string name) => _profile.Hosts[name].Lock;
    internal TimeSpan Timeout => _timeout;
    /// <summary>The run's journal: the preparation wrote each copy and character there before making it.</summary>
    internal RunJournal Journal { get; }
    internal string JournalOf(string host) => RunJournal.DirectoryFor(_profile.Hosts[host]);
    /// <summary>Every character and prepared install is retired (<see cref="RunRetirement.CampaignAsync"/>).</summary>
    internal bool Retired { get; set; }

    /// <summary>
    /// Retires the disposable characters and prepared installs (<see cref="RunRetirement"/>), taking each host's lock for it.
    /// A run retires them itself under the locks it holds; this is for a preparation no run used.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Retired) return;
        var failures = await new RunRetirement(null, "").CampaignAsync(this, []).ConfigureAwait(false);
        if (failures.Count != 0) throw new AggregateException("Some prepared runtimes remain; inspect them before another run.", failures);
    }
}

/// <summary>One command's setup half: reviewed inputs become pinned, separate disposable server/client installs.</summary>
public static class HostedCampaignPreparation
{
    internal sealed record Inputs(HostedCampaignManifest Manifest, ResolvedEnvironment Profile,
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

    private sealed record HostInspection(CampaignPreflightReport Report,
        IReadOnlyDictionary<string, string> ObservedSteamIds, IReadOnlyDictionary<string, HostListing> SourceListings,
        IReadOnlyDictionary<string, CharacterDirectories> CharacterDirectories);

    // Every host check reports a refusal under its own input and goes on; only an unexpected exception type still escapes.
    // One list for all of them: a probe's new refusal type (a loader package for another platform, a host that cannot
    // tell which ports are in use) otherwise crashed the whole concurrent preflight instead of filling one line.
    private static bool HostCheckRefusal(Exception error) => error is ArgumentException or InvalidOperationException or IOException or
        InvalidDataException or PlatformNotSupportedException or UnauthorizedAccessException;

    private static async Task<HostInspection> InspectHostsAsync(Inspection inspection, TimeSpan timeout,
        Func<string, IGameHost>? hostFactory, CancellationToken cancellation)
    {
        if (inspection.Inputs == null) return new HostInspection(inspection.Report,
            new Dictionary<string, string>(), new Dictionary<string, HostListing>(), new Dictionary<string, CharacterDirectories>());
        var inputs = inspection.Inputs!;
        var failures = new ConcurrentBag<CampaignPreflightProblem>(inspection.Report.Problems);
        var observedSteamIds = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var sourceListings = new ConcurrentDictionary<string, HostListing>(StringComparer.Ordinal);
        var characterDirectories = new ConcurrentDictionary<string, CharacterDirectories>(StringComparer.Ordinal);
        var hostChecks = inputs.Roles.GroupBy(role => role.Role.Host, StringComparer.Ordinal).Select(async group =>
        {
            IGameHost host;
            try { host = hostFactory?.Invoke(group.Key) ?? inputs.Profile.CreateHost(group.Key); }
            catch (Exception error) when (HostCheckRefusal(error))
            {
                failures.Add(new(group.Key, "host", error.Message));
                return;
            }
            if (group.Any(role => role.Name != "server") &&
                inputs.Profile.Hosts[group.Key] is { Kind: "local", Platform: "macos" })
            {
                try { MacGuiSession.Require(); }
                catch (Exception error) when (HostCheckRefusal(error))
                { failures.Add(new(group.Key, "desktop session", error.Message)); }
            }
            try
            {
                await HostedRuntimeStage.RequireStoppedAsync(host, timeout, cancellation,
                    clientSession: group.Any(role => role.Name != "server")).ConfigureAwait(false);
            }
            catch (Exception error) when (HostCheckRefusal(error))
            { failures.Add(new(group.Key, "session", error.Message)); }
            if (group.Any(role => role.Name == "server"))
            {
                try { await HostServer.RequireTaskLogonAsync(host, timeout, cancellation).ConfigureAwait(false); }
                catch (Exception error) when (HostCheckRefusal(error))
                { failures.Add(new("server", "server task", error.Message)); }
            }
            var capacities = new ConcurrentBag<(string Actor, HostCopyCapacity Capacity)>();
            await Task.WhenAll(group.Select(async item =>
            {
                try
                {
                    await HostInstall.RequirePortFreeAsync(host, item.Role.CliPort, timeout, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (HostCheckRefusal(error))
                { failures.Add(new(item.Name, "ValheimCLI port", error.Message)); }
                try
                {
                    capacities.Add((item.Name, await HostCopyCapacityProbe.InspectAsync(host, item.Role.Install,
                        item.Role.Runtime, timeout, cancellation).ConfigureAwait(false)));
                }
                catch (Exception error) when (HostCheckRefusal(error))
                { failures.Add(new(item.Name, "copy space", error.Message)); }
                if (!inspection.Report.Problems.Any(problem => problem.Actor == item.Name && problem.Input == "loader"))
                {
                    try
                    {
                        var loader = item.Input.LoaderPackage == null ? null : BepInExLoaderPackage.Read(item.Input.LoaderPackage);
                        sourceListings[item.Name] = await HostedRuntimeStage.InspectSourceAsync(host,
                            item.Name == "server" ? HostedRuntimeKind.Server : HostedRuntimeKind.Client,
                            item.Role.Install, loader, timeout, cancellation).ConfigureAwait(false);
                    }
                    catch (Exception error) when (HostCheckRefusal(error))
                    { failures.Add(new(item.Name, "game and loader", error.Message)); }
                }
                if (item.Name == "server") return;
                var folders = inputs.Characters.TryGetValue(item.Name, out var character) ? ResolveFoldersAsync(character) : Task.CompletedTask;
                async Task ResolveFoldersAsync(HostedCharacterSelection selection)
                {
                    try
                    {
                        // Given folders are checked, left-out ones resolved, on the client's own host.
                        var directories = await HostedCharacterStage.ResolveDirectoriesAsync(host, inputs.Profile.Hosts[group.Key].Platform,
                            selection.Input, timeout, cancellation).ConfigureAwait(false);
                        if (directories.Missing != null) failures.Add(new(item.Name, "character folders", directories.Missing));
                        else characterDirectories[item.Name] = directories;
                    }
                    catch (Exception error) when (HostCheckRefusal(error))
                    { failures.Add(new(item.Name, "character folders", error.Message)); }
                }
                try
                {
                    // The client's Steam identity is the one signed in on its host, never one written in a file.
                    var observed = await SteamSignedInUsers.ReadAsync(host, timeout, cancellation).ConfigureAwait(false);
                    if (observed.State != SteamSignedInState.Matches || observed.AccountId is not { } id)
                        throw new InvalidOperationException($"Client {item.Name} has no verifiable signed-in Steam identity on {host.Name}.");
                    observedSteamIds[item.Name] = SteamPoolAccount.SteamId64(id);
                }
                catch (Exception error) when (HostCheckRefusal(error))
                { failures.Add(new(item.Name, "Steam identity", error.Message)); }
                await folders.ConfigureAwait(false);
            })).ConfigureAwait(false);
            try { HostCopyCapacityProbe.RequireCombined(group.Key, capacities); }
            catch (Exception error) when (HostCheckRefusal(error)) { failures.Add(new(group.Key, "copy space", error.Message)); }
        });
        await Task.WhenAll(hostChecks).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        foreach (var shared in observedSteamIds.GroupBy(account => account.Value, StringComparer.Ordinal).Where(group => group.Count() > 1))
            failures.Add(new("clients", "Steam identities", "Clients " + string.Join(", ", shared.Select(account => account.Key).Order(StringComparer.Ordinal)) +
                " use the same signed-in Steam account; choose different client environments before launch."));
        var actors = inspection.Report.Actors.Select(actor => characterDirectories.TryGetValue(actor.Name, out var directories)
            ? actor with { CharactersDirectory = directories.Characters, SteamUserDataDirectory = directories.UserData } : actor).ToArray();
        return new HostInspection(new CampaignPreflightReport(failures.OrderBy(problem => problem.Actor, StringComparer.Ordinal)
            .ThenBy(problem => problem.Input, StringComparer.Ordinal).ToArray()) { Actors = actors, Detected = inspection.Report.Detected },
            observedSteamIds, sourceListings, characterDirectories);
    }

    /// <summary>The BepInEx plugins among a role's selected files, by GUID, with each DLL's MD5.</summary>
    internal static Dictionary<string, string> PluginPins(string role, IEnumerable<HostedRuntimeFile> files)
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.RelativePath.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) &&
                                               file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            PluginAssembly metadata;
            try { metadata = PluginMetadata.Read(file.Source); }
            catch (Exception error) when (error is BadImageFormatException or InvalidDataException) { continue; }
            foreach (var plugin in metadata.Plugins)
                if (!pins.TryAdd(plugin.Guid, FileHash.Md5(file.Source)))
                    throw new InvalidDataException($"The {role} runtime selects BepInEx plugin {plugin.Guid} more than once.");
        }
        return pins;
    }

    /// <summary>
    /// Stage-1 agreement of a plan with the campaign, before any host is contacted: the campaign names the fixture world and
    /// direct-join address binding needs; the plan binds exactly the campaign's clients; every plugin the plan (or a client
    /// section) pins by hash is one its role's dependency lock selects, since binding replaces those pins with the selected
    /// files' own hashes and would otherwise drop one, and none it pins <c>absent</c> is selected; and no launch argument still
    /// holds an angle-bracket placeholder such as <c>&lt;fixture password&gt;</c>. Every problem is reported at once, after the
    /// campaign's own preflight (<see cref="Check"/>) passed.
    /// </summary>
    public static void CheckPlan(string manifestFile, ServerRunPlan plan, IReadOnlyDictionary<string, ClientRunPlan> clients) =>
        CheckPlan(InspectInputs(manifestFile), plan, clients);

    internal static void CheckPlan(Inspection inspection, ServerRunPlan plan, IReadOnlyDictionary<string, ClientRunPlan> clients)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(clients);
        inspection.Report.RequireReady();
        var inputs = inspection.Inputs!;
        var problems = new List<string>();
        if (inputs.Manifest.World.Length == 0 || inputs.Manifest.Join.Length == 0)
            problems.Add("Set world (the fixture) and join (the server's address) in the campaign manifest.");
        if (!clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(inputs.Manifest.Clients.Keys))
            problems.Add($"The plan binds clients {string.Join(", ", clients.Keys.Order(StringComparer.Ordinal))}; the campaign declares {string.Join(", ", inputs.Manifest.Clients.Keys.Order(StringComparer.Ordinal))}.");
        void Pins(string role, IReadOnlyDictionary<string, string> pins)
        {
            if (!inputs.Selections.TryGetValue(role, out var files)) return;
            var selected = PluginPins(role, files);
            foreach (var (guid, value) in pins.Where(pin => !Expectations.IsWorldKey(pin.Key)).OrderBy(pin => pin.Key, StringComparer.Ordinal))
                if (value == "absent" && selected.ContainsKey(guid))
                    problems.Add($"The plan pins plugin {guid} absent for {role}, but its dependency lock selects it.");
                else if (value != "absent" && !selected.ContainsKey(guid))
                    problems.Add($"The plan pins plugin {guid} for {role}, which its dependency lock does not select.");
        }
        void Placeholders(string role, IEnumerable<string> arguments)
        {
            foreach (string argument in arguments.Where(argument => argument.Contains('<') || argument.Contains('>')))
                problems.Add($"Replace the {role}'s placeholder argument {argument} before preparing any host.");
        }
        Pins("server", plan.Pins);
        Placeholders("server", plan.Arguments);
        foreach (var (name, client) in clients.Where(client => inputs.Manifest.Clients.ContainsKey(client.Key)))
        {
            Pins(name, client.Pins);
            Placeholders(name, client.LaunchArguments);
        }
        if (problems.Count != 0) throw new ArgumentException("The plan does not agree with the campaign: " + string.Join(" ", problems));
    }

    /// <summary>Require the same shared preflight used by preparation, before any host is contacted.</summary>
    public static void Check(string manifestFile) => Inspect(manifestFile).RequireReady();

    internal sealed record Inspection(Inputs? Inputs, CampaignPreflightReport Report);

    internal static Inspection InspectInputs(string manifestFile)
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

        ResolvedEnvironment? profile = null;
        ResolvedEnvironmentInventory? resolved = null;
        IReadOnlyList<string> detected = [];
        if (manifest.Clients.ContainsKey("server"))
            problems.Add(new("server", "role", "The actor name server is reserved for the dedicated server."));
        else Try("campaign", "inventory", () =>
        {
            var inventory = EnvironmentInventory.Read(manifest.Inventory.Length == 0 ? null : manifest.Inventory);
            detected = inventory.Detected;
            resolved = inventory.Resolve(manifest);
            profile = resolved.Environment;
        });
        if (manifest.Server.Character != null)
            problems.Add(new("server", "character", "A dedicated server has no character."));

        var selected = new Dictionary<string, HostedRuntimeFile[]>(StringComparer.Ordinal);
        var characters = new Dictionary<string, HostedCharacterSelection>(StringComparer.Ordinal);
        var manifestRoles = new[] { (Name: "server", Input: manifest.Server) }
            .Concat(manifest.Clients.OrderBy(client => client.Key, StringComparer.Ordinal)
                .Select(client => (Name: client.Key, Input: client.Value))).ToArray();
        foreach (var (name, input) in manifestRoles)
        {
            string? loaderPackage = resolved?.LoaderPackages.GetValueOrDefault(name) ?? input.LoaderPackage;
            if (loaderPackage != null)
                Try(name, "loader", () => _ = BepInExLoaderPackage.Read(loaderPackage));
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
        var report = new CampaignPreflightReport(problems) { Actors = actors, Detected = detected };
        if (profile?.Server == null || !manifest.Clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(profile.Clients.Keys))
            return new Inspection(null, report);
        HostedCampaignRole RoleInput(string name, HostedCampaignRole input)
        {
            string? loaderPackage = resolved?.LoaderPackages.GetValueOrDefault(name) ?? input.LoaderPackage;
            if (loaderPackage == input.LoaderPackage) return input;
            return new HostedCampaignRole
            {
                DependencyLock = input.DependencyLock, EnvironmentCandidates = input.EnvironmentCandidates,
                DifferentHostFrom = input.DifferentHostFrom, LoaderPackage = loaderPackage,
                Files = input.Files, Character = input.Character,
            };
        }
        var roles = new List<(string Name, GameRole Role, HostedCampaignRole Input)>
            { ("server", profile.Server, RoleInput("server", manifest.Server)) };
        roles.AddRange(profile.Clients.Select(client => (client.Key, client.Value,
            RoleInput(client.Key, manifest.Clients[client.Key]))));
        return new Inspection(new Inputs(manifest, profile, roles, selected, characters), report);
    }

    /// <summary>Turn observed, distinct signed-in identities into the in-memory lease pool the runner leases from.</summary>
    internal static void CompleteObservedSteamAccounts(ResolvedEnvironment profile, IReadOnlyDictionary<string, string> observedIds)
    {
        var section = profile.SteamAccounts ?? throw new ArgumentException("The inventory has no Steam lease section.", nameof(profile));
        string directory = section.ObservedLeaseDirectory ?? throw new ArgumentException("This profile does not use observed Steam identities.", nameof(profile));
        if (!profile.Clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(observedIds.Keys) ||
            observedIds.Values.Distinct(StringComparer.Ordinal).Count() != observedIds.Count)
            throw new ArgumentException("Every client needs a different, verified signed-in Steam identity.", nameof(observedIds));
        var pool = new SteamAccountPool
        {
            Pool = "steam-clients", LeaseDirectory = directory,
            Accounts = observedIds.Values.Select(id => new SteamPoolAccount
            { Name = SteamPoolAccount.LeaseKey(id), SteamId = id }).ToList(),
        };
        section.ObservedLeaseDirectory = null;
        section.Accounts = pool;
        foreach (var (client, id) in observedIds)
            profile.Clients[client].SteamAccount = SteamPoolAccount.LeaseKey(id);
        profile.Validate();
    }

    public static Task<PreparedHostedCampaign> PrepareAsync(string manifestFile, string outputDirectory, TimeSpan timeout,
        Func<string, IGameHost>? hostFactory = null, CancellationToken cancellation = default) =>
        PrepareAsync(InspectInputs(manifestFile), outputDirectory, timeout, hostFactory, cancellation);

    // A preparation that failed ends its run in the journal of every host it was to touch, best effort: the entry is a record,
    // and the preparation's own failure is the one to report.
    private static async Task JournalEndAsync(RunJournal journal, ResolvedEnvironment profile, IEnumerable<(string Name, GameRole Role, HostedCampaignRole Input)> roles,
        Func<string, IGameHost>? hostFactory, TimeSpan timeout, bool cleaned)
    {
        foreach (string hostName in roles.Select(item => item.Role.Host).Distinct(StringComparer.OrdinalIgnoreCase))
            try
            {
                await journal.AppendAsync(hostFactory?.Invoke(hostName) ?? profile.CreateHost(hostName), RunJournal.DirectoryFor(profile.Hosts[hostName]), "run",
                    JournalEntry.Of(JournalEntry.RunEnded, ("state", "failed in preparation"), ("cleanupVerified", cleaned ? "true" : "false")), timeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal the failed preparation's end on {hostName}: {error.Message}"); }
    }

    /// <summary>
    /// Prepares every actor under run <paramref name="runId"/> (one is minted when null): each actor's install is
    /// <c>&lt;runtime&gt;/vt-prep-&lt;runId&gt;-&lt;actor&gt;</c>, and every copy and character is journalled on its host before it is made
    /// (<see cref="RunJournal"/>).
    /// </summary>
    internal static async Task<PreparedHostedCampaign> PrepareAsync(Inspection inspection, string outputDirectory, TimeSpan timeout,
        Func<string, IGameHost>? hostFactory, CancellationToken cancellation, string? runId = null)
    {
        inspection.Report.RequireReady();
        var readiness = await InspectHostsAsync(inspection, timeout, hostFactory, cancellation).ConfigureAwait(false);
        readiness.Report.RequireReady();
        var inputs = inspection.Inputs!;
        var (manifest, profile, roles, selections, characters) = inputs;
        if (profile.SteamAccounts != null) CompleteObservedSteamAccounts(profile, readiness.ObservedSteamIds);
        profile.Validate();
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Use a new private campaign output directory: " + output);
        Directory.CreateDirectory(output);
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
        var journal = new RunJournal(runId ?? RunJournal.NewRunId());
        string id = journal.RunId;
        var copies = new ConcurrentBag<(string Host, string Actor, string Runtime, string Stage)>();
        var stagedCharacters = new ConcurrentBag<(string Host, string Actor, HostedCampaignCharacter Character)>();
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
                var capacities = await Task.WhenAll(group.Select(async item =>
                    (item.Name, Capacity: await HostCopyCapacityProbe.InspectAsync(host, item.Role.Install,
                        item.Role.Runtime, timeout, cancellation).ConfigureAwait(false)))).ConfigureAwait(false);
                HostCopyCapacityProbe.RequireCombined(hostName, capacities.Select(item => (Actor: item.Name, item.Capacity)));
                string journalDirectory = RunJournal.DirectoryFor(profile.Hosts[hostName]);
                await Task.WhenAll(group.Select(async item =>
                {
                    var (name, role, _) = item;
                    string parent = HostInstall.Join(role.Runtime, "vt-prep-" + id + "-" + name);
                    string runtime = HostInstall.Join(parent, "runtime"), stage = HostInstall.Join(parent, "staging");
                    // Journalled before the copy: an interrupted preparation leaves a record of every path it may own.
                    await journal.AppendAsync(host, journalDirectory, name, JournalEntry.Of(JournalEntry.CopyIntended,
                        ("runtime", runtime), ("stage", stage), ("parent", parent)), timeout, cancellation).ConfigureAwait(false);
                    listings[name] = await HostedRuntimeStage.PrepareWithInspectedSourceAsync(host, name == "server" ? HostedRuntimeKind.Server : HostedRuntimeKind.Client,
                        role.Install, runtime, stage, selections[name], timeout, cancellation,
                        item.Input.LoaderPackage == null ? null : BepInExLoaderPackage.Read(item.Input.LoaderPackage),
                        readiness.SourceListings[name]).ConfigureAwait(false);
                    copies.Add((hostName, name, runtime, stage));
                    await journal.AppendAsync(host, journalDirectory, name, JournalEntry.Of(JournalEntry.CopyDone,
                        ("runtime", runtime), ("files", listings[name].Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))), timeout, cancellation).ConfigureAwait(false);
                    if (characters.TryGetValue(name, out var selected))
                    {
                        // The folders the host check resolved, never the manifest's object.
                        var folders = readiness.CharacterDirectories[name];
                        var character = selected with { Input = selected.Input.WithDirectories(folders.Characters!, folders.UserData!) };
                        await journal.AppendAsync(host, journalDirectory, name, JournalEntry.Of(JournalEntry.CharacterIntended,
                            ("characters", folders.Characters!), ("userData", folders.UserData!), ("fileName", character.Input.FileName)), timeout, cancellation).ConfigureAwait(false);
                        await HostedCharacterStage.StageAsync(host, character, HostInstall.Join(parent, "character-stage"), timeout, cancellation).ConfigureAwait(false);
                        stagedCharacters.Add((hostName, name, character.Input));
                        await journal.AppendAsync(host, journalDirectory, name, JournalEntry.Of(JournalEntry.CharacterDone,
                            ("fileName", character.Input.FileName)), timeout, cancellation).ConfigureAwait(false);
                    }
                    role.Install = runtime;
                })).ConfigureAwait(false);
            })).ConfigureAwait(false);
            return new PreparedHostedCampaign(manifest, profile, listings, selections, copies.ToArray(), stagedCharacters.ToArray(), hostFactory, timeout, journal);
        }
        catch (Exception original)
        {
            try
            {
                await new PreparedHostedCampaign(manifest, profile, listings, selections, copies.ToArray(), stagedCharacters.ToArray(), hostFactory, timeout, journal).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                await JournalEndAsync(journal, profile, roles, hostFactory, timeout, cleaned: false).ConfigureAwait(false);
                throw new AggregateException("Campaign preparation failed and cleanup was not proven.", original, cleanup);
            }
            await JournalEndAsync(journal, profile, roles, hostFactory, timeout, cleaned: true).ConfigureAwait(false);
            throw;
        }
    }
}
