using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>One process's reviewed dependency lock and optional, explicit config/script/asset files.</summary>
public sealed class HostedCampaignRole
{
    public string DependencyLock { get; set; } = "";
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
    /// <summary>Optional pinned world fixture for a scenario runner built on this preparation.</summary>
    public string World { get; set; } = "";
    /// <summary>The dedicated server's public or LAN game address, including port, for direct-join scenarios.</summary>
    public string Join { get; set; } = "";
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
        if (string.IsNullOrWhiteSpace(manifest.Profile)) throw new InvalidDataException("Name the environment profile in profile.");
        manifest.Profile = Path.GetFullPath(manifest.Profile, directory);
        if (manifest.World.Length != 0) manifest.World = Path.GetFullPath(manifest.World, directory);
        if (manifest.Server == null || manifest.Clients == null) throw new InvalidDataException("A hosted campaign needs a server and named clients.");
        void Resolve(HostedCampaignRole role)
        {
            if (string.IsNullOrWhiteSpace(role.DependencyLock)) throw new InvalidDataException("Every campaign role needs its own reviewed dependencyLock.");
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

    /// <summary>Review the fixture, dependency locks, selected files and distinct client identities without contacting a host.</summary>
    public static void Check(string manifestFile) => _ = ReadInputs(manifestFile);

    private static Inputs ReadInputs(string manifestFile)
    {
        var manifest = HostedCampaignManifest.Read(manifestFile);
        var profile = EnvironmentProfile.Read(manifest.Profile);
        if (profile.Server == null) throw new ArgumentException("A hosted campaign needs a dedicated server in the environment profile.");
        if (profile.Clients.ContainsKey("server"))
            throw new ArgumentException("The role name server is reserved for the dedicated server; rename that client.");
        if (!manifest.Clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(profile.Clients.Keys))
            throw new ArgumentException("The campaign's clients must match the environment profile's named clients exactly.");
        var roles = new List<(string Name, GameRole Role, HostedCampaignRole Input)> { ("server", profile.Server, manifest.Server) };
        roles.AddRange(profile.Clients.Select(client => (client.Key, client.Value, manifest.Clients[client.Key])));
        if (manifest.Server.Character != null) throw new ArgumentException("The dedicated server has no character.");
        if (profile.Clients.Count > 0 && profile.SteamAccounts is not { CheckSignedIn: true })
            throw new ArgumentException("A campaign with native clients requires steamAccounts.checkSignedIn=true and pinned Steam identities.");
        // A single account cannot run two clients at once. Check whether the pool has a distinct assignment for
        // every named client before copying gigabytes of game files to their hosts. The actual leases are still
        // acquired by the runner just before each client starts.
        if (profile.Clients.Count > 1 && profile.SteamAccounts is { } accounts)
        {
            var choices = profile.Clients.Select(client => (client.Key,
                Names: accounts.Candidates(client.Value).Select(account => account.SteamId!).ToArray()))
                .OrderBy(pair => pair.Names.Length).ToArray();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Assign(int index)
            {
                if (index == choices.Length) return true;
                foreach (string name in choices[index].Names)
                    if (used.Add(name))
                    {
                        if (Assign(index + 1)) return true;
                        used.Remove(name);
                    }
                return false;
            }
            if (!Assign(0))
                throw new ArgumentException("The Steam account pool cannot assign a different account to every simultaneous client.");
        }
        // Validate all local inputs before touching any host.
        foreach (var role in roles)
            if (role.Input.LoaderPackage != null) _ = BepInExLoaderPackage.Read(role.Input.LoaderPackage);
        var selections = roles.ToDictionary(role => role.Name, role =>
            HostedRuntimeStage.FromDependencies(role.Input.DependencyLock).Concat(role.Input.Files).ToArray(), StringComparer.Ordinal);
        var characters = new Dictionary<string, HostedCharacterSelection>(StringComparer.Ordinal);
        foreach (var (name, _, input) in roles.Where(role => role.Name != "server"))
        {
            if (input.Character == null) throw new ArgumentException($"Client {name} needs a registered, disposable character.");
            characters[name] = HostedCharacterStage.Select(input.Character, Path.GetDirectoryName(Path.GetFullPath(manifestFile))!);
        }
        if (characters.Values.Select(character => character.Handle.PlayerId).Distinct().Count() != characters.Count)
            throw new ArgumentException("Simultaneous clients need different registered character player IDs; copies of one seed are one player.");
        foreach (var (name, files) in selections)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!File.Exists(file.Source)) throw new FileNotFoundException($"The {name} role's selected file is missing: {file.Source}", file.Source);
                if (!paths.Add(file.RelativePath)) throw new InvalidDataException($"The {name} role selects {file.RelativePath} more than once.");
            }
        }
        if (manifest.World.Length != 0)
        {
            _ = WorldIdentity.Read(manifest.World);
            _ = WorldFixture.Manifest(manifest.World);
        }
        return new Inputs(manifest, profile, roles, selections, characters);
    }

    public static async Task<PreparedHostedCampaign> PrepareAsync(string manifestFile, string outputDirectory, TimeSpan timeout,
        Func<string, IGameHost>? hostFactory = null, CancellationToken cancellation = default)
    {
        var inputs = ReadInputs(manifestFile);
        var (manifest, profile, roles, selections, characters) = inputs;
        // A local macOS client cannot enter its first scene from a locked console. Check before
        // copying gigabytes or starting the dedicated server, not minutes later at client launch.
        if (profile.Clients.Values.Any(role => profile.Hosts[role.Host] is { Kind: "local", Platform: "macos" }))
            MacGuiSession.Require();
        // Read every client's host before copying any actor. Lock-time checks still run before launch.
        var identityFailures = new ConcurrentBag<Exception>();
        await Task.WhenAll(profile.Clients.Select(async client =>
        {
            try
            {
                var host = hostFactory?.Invoke(client.Value.Host) ?? profile.CreateHost(client.Value.Host);
                var observed = await SteamSignedInUsers.ReadAsync(host, timeout, cancellation).ConfigureAwait(false);
                var matching = profile.SteamAccounts!.Candidates(client.Value)
                    .Where(account => observed.State == SteamSignedInState.Matches &&
                        SteamPoolAccount.AccountId(account.SteamId) == observed.AccountId).ToArray();
                if (matching.Length != 1)
                    throw new InvalidOperationException($"Client {client.Key} Steam identity is unverified or does not uniquely match its expected account on {host.Name}; no runtime was staged.");
                client.Value.SteamAccount = matching[0].Name;
            }
            catch (Exception error) { identityFailures.Add(error); }
        })).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (!identityFailures.IsEmpty) throw new AggregateException("Campaign Steam identity preflight failed.", identityFailures);
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Use a new private campaign output directory: " + output);
        Directory.CreateDirectory(output);
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
                    listings[name] = await HostedRuntimeStage.PrepareAsync(host, name == "server" ? HostedRuntimeKind.Server : HostedRuntimeKind.Client,
                        role.Install, runtime, stage, selections[name], timeout, cancellation,
                        item.Input.LoaderPackage == null ? null : BepInExLoaderPackage.Read(item.Input.LoaderPackage)).ConfigureAwait(false);
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
