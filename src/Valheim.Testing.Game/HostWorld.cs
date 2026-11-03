using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>
/// The <c>hostWorld</c> section of a <see cref="ClientRunPlan"/>: the client hosts this fixture world from its menu (a
/// listen server) instead of joining a dedicated server. The world is the client's own local world, so the runner copies the
/// pinned fixture into the client's local worlds for the run (<see cref="HostedWorld"/>) and never uses a world already
/// there. Unknown fields are refused with the rest of the plan.
/// </summary>
public sealed class HostWorldPlan
{
    /// <summary>
    /// The fixture: one Valheim 1.0 world folder <c>&lt;name&gt;/</c> at the root, exactly as the game saved it (its
    /// <c>_main.&lt;n&gt;</c> save set and chunk files), optionally with the game's <c>_backup_auto-</c> copies of it
    /// (<see cref="WorldIdentity.Read"/>). Every file is pinned by SHA256 (<see cref="WorldFixture.Manifest"/>).
    /// </summary>
    public PinnedDirectory World { get; set; } = new();
    /// <summary>Pending game creation instead of a fixture. World/WorldUid must be empty until the existing owner verifies the actual created metadata.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SeededWorldRequest? Create { get; set; }
    internal SeededWorldRequest? RequestedCreation { get; private set; }

    /// <summary>
    /// The fixture world's exact UID, required even without pins: the host must load that world, and the game silently
    /// creates a fresh world when the named one is missing.
    /// </summary>
    public string WorldUid { get; set; } = "";
    /// <summary>Hosts a crossplay world (the game's PlayFab backend, <c>--crossplay true</c>).</summary>
    public bool Crossplay { get; set; }
    /// <summary>
    /// The client's data directory, which holds <c>worlds_local</c>. Default: this user's Valheim data directory for the
    /// client's platform (<see cref="HostedWorld.DefaultSaveDirectory"/>). Set it when a Windows client runs as
    /// another user. A native macOS hosted client must use its signed-in user's default; Valheim 1.0.16 ignored -savedir.
    /// </summary>
    public string? SaveDirectory { get; set; }
    /// <summary>The confirmed save's timeout between rounds, 1 to 600 seconds.</summary>
    public int SaveSeconds { get; set; } = 120;

    /// <summary>
    /// Refuses the section before anything is copied: a fixture without a manifest, a manifest that is not one complete world
    /// by its file names (<see cref="HostedWorld.NameOf"/>), no exact world UID, a relative save directory or a save timeout out of range.
    /// </summary>
    public void Validate()
    {
        if (Create != null)
        {
            if (World == null || !string.IsNullOrEmpty(World.Source) || World.Sha256.Count != 0 || !string.IsNullOrEmpty(WorldUid))
                throw new ArgumentException("hostWorld.create is mutually exclusive with a fixture source, manifest and UID.");
            ValidateSaveOptions();
            return;
        }
        World.Validate();
        _ = HostedWorld.NameOf(World.Sha256.Keys);
        if (!long.TryParse(WorldUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("hostWorld.worldUid: give the fixture world's exact UID. The host must load that world, and the game silently creates a fresh one when it is missing.");
        ValidateSaveOptions();
    }

    private void ValidateSaveOptions()
    {
        if (SaveDirectory != null && !Path.IsPathFullyQualified(SaveDirectory))
            throw new ArgumentException("hostWorld.saveDirectory: give the full path of the client's data directory (the one that holds worlds_local), or leave it out for this user's default.");
        if (SaveSeconds is < 1 or > 600) throw new ArgumentException("hostWorld.saveSeconds is 1 to 600.");
    }

    /// <summary>
    /// Before the fixture is copied: its files must still be the pinned ones (when the plan lists hashes), they must be one
    /// complete world as the game loads it, and the world its own metadata states (<see cref="WorldIdentity.Read"/>) must have <see cref="WorldUid"/>. A world name reused for
    /// another snapshot has another UID, and is refused here as the wrong fixture, naming the UID it holds, instead of after
    /// the game loaded it. Returns that identity.
    /// </summary>
    public WorldIdentity Preflight() => Preflight(null, World.Source);

    internal WorldIdentity.Admission? Admission { get; set; }

    internal WorldIdentity Preflight(GameBuild? target, string source)
    {
        if (Create != null) throw new InvalidOperationException("The seeded world is unresolved: no fixture identity or UID exists before verified game creation.");
        if (World.Sha256.Count != 0 && source == World.Source) WorldFixture.Verify(source, World.Sha256);
        var identity = target == null ? WorldIdentity.Read(source) : WorldIdentity.Read(source, target);
        RequireUid(identity);
        return identity;
    }

    internal void BindCreated(SeededWorldRequest request, WorldIdentity.Admission admitted)
    {
        if (Create != request || RequestedCreation != null) throw new InvalidOperationException("The seeded request changed or was already resolved.");
        Validate(); admitted.Verify();
        if (admitted.Identity.Name != request.Name || admitted.Identity.SeedName != request.Seed)
            throw new InvalidDataException("The game-created metadata does not match the requested name and exact seed.");
        World = new PinnedDirectory { Source = admitted.Source, Sha256 = new(admitted.Files, StringComparer.Ordinal) };
        WorldUid = admitted.Identity.UidText; Admission = admitted;
        RequestedCreation = request; Create = null;
    }

    internal void RequireUid(WorldIdentity identity)
    {
        if (identity.UidText != WorldUid)
            throw new InvalidOperationException($"hostWorld.worldUid is {WorldUid}, but the fixture {World.Source} holds world {identity.Name} with UID {identity.UidText} ({identity.File}): " +
                "the same name, another world. Pin the UID of this exact fixture (WorldIdentity.Read(fixture).UidText), or point hostWorld.world at the fixture the UID belongs to.");
    }
}

/// <summary>
/// A fixture world placed in a hosting client's local worlds for one hosted run. Its one placement owner is the hosting layer's
/// journalled site (<c>HostedWorldOnHost</c> in <c>Valheim.Testing.GameSessions</c>), on a campaign client's host or on this machine:
/// it refuses when anything named for the world (<see cref="IsNamedFor"/>) is already in <c>worlds_local</c> (a user's world is never
/// overwritten or used, and a refused run copies nothing), copies and verifies the fixture into the output directory (that copy stays
/// as the input evidence), journals the placement and places the fixture in <c>worlds_local</c>. <see cref="Collect"/> (also
/// <see cref="Dispose"/>) moves the world, with whatever the game wrote for it (saves, <c>.old</c> files, backups), out of the client's
/// worlds into the run's folder on its host, and fetches it into <c>host-world</c> in the output directory. Collect only once the client
/// no longer hosts it: after an owned client stopped, or after the host left to its menu.
/// </summary>
public sealed class HostedWorld : IDisposable
{
    private readonly string _output;
    private string? _target;

    /// <summary>The world's name, the fixture's world folder: what <c>cli_start_host_world</c> starts.</summary>
    public string Name { get; }
    public string WorldUid { get; }
    /// <summary>The client's <c>worlds_local</c> directory, a path on the client's host (<see cref="Host"/>).</summary>
    public string WorldsDirectory { get; }
    /// <summary>The host the journalled site placed the world on: a campaign client's, or this machine.</summary>
    public string Host { get; }
    // Moves the world out on its host into the run's folder there and fetches it into the given evidence folder (HostedWorldOnHost).
    private readonly Func<string, string> _collectOnHost;
    /// <summary>The verified copy of the fixture in the output directory.</summary>
    public string FixtureCopy { get; }
    /// <summary>Where <see cref="Collect"/> moved the world, or null before it has.</summary>
    public string? CollectedTo { get; private set; }

    private HostedWorld(string name, string worldUid, string worlds, string copy, string output, string host, Func<string, string> collectOnHost)
    { Name = name; WorldUid = worldUid; WorldsDirectory = worlds; FixtureCopy = copy; _output = output; Host = host; _collectOnHost = collectOnHost; }

    /// <summary>A world placed in a hosting client's worlds through its journalled site (<c>HostedWorldOnHost.Place</c>).</summary>
    internal static HostedWorld OnHost(string name, string worldUid, string worlds, string copy, string output, string host, Func<string, string> collect) =>
        new(name, worldUid, worlds, copy, output, host, collect);

    /// <summary>
    /// The world's name from a fixture's relative paths (a pinned manifest's keys, before any file is read): the one folder
    /// <c>&lt;name&gt;/</c> at the root, as Valheim 1.0 saves a world. The paths follow <see cref="WorldIdentity.Read"/>'s layout
    /// rules, which this applies by name alone: one complete save set and its chunk files in <c>&lt;name&gt;/</c>, optionally the
    /// game's backup folders (<c>&lt;name&gt;_backup_auto-&lt;date&gt;/</c> and the like), and nothing else (so the placement's move-out takes back
    /// everything it placed). A pre-1.0 <c>&lt;name&gt;.fwl</c> and <c>&lt;name&gt;.db</c> is refused. The name is at least 3
    /// characters and a valid file name.
    /// </summary>
    public static string NameOf(IEnumerable<string> relativePaths) => WorldIdentity.Layout(relativePaths).Name;

    // Set only by Fakes.FakeClientDataDirectory: the innermost open scope, whose directory a no-game test's simulated client keeps.
    internal static readonly AsyncLocal<Fakes.FakeClientDataDirectory?> SimulatedClientData = new();
    internal const string MacSaveRelative = "Library/Application Support/IronGate/Valheim", WindowsSaveRelative = "AppData/LocalLow/IronGate/Valheim";

    /// <summary>
    /// Where Unity keeps a game client's data on <paramref name="platform"/> (company IronGate, product Valheim), which holds <c>worlds_local</c>.
    /// The only way to change it is a <see cref="Fakes.FakeClientDataDirectory"/> scope, for no-game tests.
    /// </summary>
    public static string DefaultSaveDirectory(ClientPlatform platform)
    {
        if (SimulatedClientData.Value is { } simulated) return simulated.Directory;
        GameLaunch.RequireClientHost(platform);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, (platform == ClientPlatform.Windows ? WindowsSaveRelative : MacSaveRelative).Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>This machine's platform, for an attached client (whose install the runner does not read).</summary>
    public static ClientPlatform CurrentPlatform => GameLaunch.CurrentClientHost;

    /// <summary>
    /// The first <c>-savedir</c> launch argument in <paramref name="launchArguments"/> (<c>-savedir</c> or <c>--savedir</c>, bare or
    /// <c>=value</c>, any case), or null. The one test of what counts as one: a standalone macOS hosted client and every campaign
    /// client refuse it by this.
    /// </summary>
    internal static string? SaveDirectoryArgument(IEnumerable<string> launchArguments) =>
        launchArguments.FirstOrDefault(argument => argument.Equals("-savedir", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("--savedir", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-savedir=", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("--savedir=", StringComparison.OrdinalIgnoreCase));

    // Valheim 1.0.16 on macOS ignored -savedir for a hosted client and read worlds_local from the signed-in user's
    // default data directory. Check before the placement creates or copies anything; a world with the same name can have another UID.
    internal static void RequireNativeSaveDirectory(ClientPlatform platform, string? requested, IEnumerable<string> launchArguments,
        string defaultDirectory)
    {
        if (platform != ClientPlatform.MacOS) return;
        if (SaveDirectoryArgument(launchArguments) != null)
            throw new InvalidOperationException("A native macOS hosted client cannot use -savedir: Valheim 1.0.16 ignored it and read the signed-in user's default worlds_local. Remove that launch argument before staging the fixture.");
        if (requested != null && !Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(defaultDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            throw new InvalidOperationException("hostWorld.saveDirectory is not the signed-in Mac user's default Valheim data directory. Valheim 1.0.16 ignored a custom -savedir for hosted worlds; omit saveDirectory and stage only a disposable, pinned fixture in the default worlds_local.");
    }

    /// <summary>
    /// Moves every entry named for the world out of the client's worlds into the run's folder on its host (handed over, as a server's
    /// world copy is) and fetches it into <c>host-world</c> (then <c>host-world-2</c> and so on) in the output directory. Runs once;
    /// a failure leaves the rest in place and is rethrown.
    /// </summary>
    public void Collect()
    {
        if (CollectedTo != null) return;
        if (_target == null)
        {
            string target = Path.Combine(_output, "host-world");
            for (int n = 2; Path.Exists(target); n++) target = Path.Combine(_output, $"host-world-{n}");
            _target = target;
        }
        CollectedTo = _collectOnHost(_target);
    }

    public void Dispose() => Collect();

    /// <summary>
    /// Whether a <c>worlds_local</c> entry is one the game reads or writes for world <paramref name="name"/>: <c>&lt;name&gt;</c> (Valheim
    /// 1.0's save folder), the game's backups <c>&lt;name&gt;_backup*</c>, or <c>&lt;name&gt;.*</c> (a world of that name the user still
    /// holds from before Valheim 1.0, which a run never places), in any case: a hosting client runs on Windows or macOS, whose folders
    /// ignore case, so to the game <c>campaign</c> is <c>Campaign</c>. The hosting layer's placement refuses any of them already there,
    /// so a user's world is never used, and its move-out takes exactly them (its host scripts select by this same rule).
    /// </summary>
    internal static bool IsNamedFor(string entry, string name)
    {
        string l = entry.ToLowerInvariant(), n = name.ToLowerInvariant();
        return l == n || l.StartsWith(n + ".", StringComparison.Ordinal) || l.StartsWith(n + "_backup", StringComparison.Ordinal);
    }
}

/// <summary>
/// Starts and restarts a hosted world on a game client at its menu, with <c>cli_start_host_world</c>. The host is the
/// server of its world (<see cref="SessionState.Server"/> true, not dedicated) and has a local player.
/// </summary>
public static class HostWorlds
{
    /// <summary>
    /// From the client's idle main menu: turns devcommands on (ValheimCLI refuses its save and leave, mutating extension
    /// commands, without them), selects the plan's character (<c>cli_select_character</c>), then starts
    /// <paramref name="worldName"/> once with <c>cli_start_host_world &lt;name&gt; --public false --crossplay true|false</c>
    /// and requires the reply to confirm the world, <c>open=true, public=False</c>, the planned crossplay flag and backend,
    /// and no password. The command only starts the world, so this re-pins the client at once with its menu pins, waits
    /// for the plan's world UID to be ready (a missing fixture would be created fresh, with another UID, and fail here),
    /// verifies the world pins, then waits for the host's player and protects it unless <paramref name="protectPlayer"/>
    /// is false (<see cref="SessionControl.WaitForWorld"/>). When the world loads between two reads, the strict menu pins
    /// refuse it ("loaded but not listed"); the exact world pins are then checked at once, and a world with another UID fails
    /// as the wrong world rather than as a load failure. The whole start is bounded by <paramref name="timeout"/>.
    /// </summary>
    public static SessionState Start(GameActor host, ClientRunPlan plan, string worldName, TimeSpan timeout, CancellationToken cancellation = default, bool protectPlayer = true)
    {
        var world = plan.HostWorld ?? throw new ArgumentException("The client plan has no hostWorld section.", nameof(plan));
        if (world.Create != null) throw new InvalidOperationException("Create and verify the requested world before issuing a host command or publishing a UID.");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (string.IsNullOrEmpty(worldName) || worldName.Any(char.IsWhiteSpace)) throw new ArgumentException("The world name must be a single token.", nameof(worldName));
        var clock = Stopwatch.StartNew();
        var session = new SessionControl(host);
        var before = session.Read();
        if (before.Phase != "menu" || before.WorldPresent) throw new InvalidOperationException("A hosted world starts from the client's idle main menu.");
        session.EnableDevcommands();
        host.Execute("cli_select_character " + plan.Character).RequireLine("OK: Selected character '", "The character was not selected");
        string crossplay = world.Crossplay ? "true" : "false";
        try
        {
            var reply = host.Execute($"cli_start_host_world {worldName} --public false --crossplay {crossplay}"); // Exactly once.
            string? line = reply.Line("OK: Starting hosted world '");
            string ending = $"; open=true, public=False, crossplay={(world.Crossplay ? "True" : "False")}, backend={(world.Crossplay ? "PlayFab" : "Steamworks")}, passwordSet=False";
            if (line == null || !line.StartsWith($"OK: Starting hosted world '{worldName}' using ", StringComparison.Ordinal) || !line.EndsWith(ending, StringComparison.Ordinal))
                throw new InvalidOperationException("The hosted world did not start as planned: " + (line ?? reply.Describe()));
        }
        finally { host.InvalidateEnvironment(); } // A start that may have begun changes the world.
        using (host.BusyReadRetries(Left(), cancellation))
        {
            try
            {
                host.VerifyEnvironment(plan.MenuExpectations); // Plugins only: the world is still loading.
                session.WaitForWorld(world.WorldUid, Left(), cancellation, protectPlayer: false);
            }
            // The world loaded between two reads, so the menu pins no longer hold ("loaded but not listed"). The exact world pins
            // below decide whether it is the fixture's world; the wait for readiness then goes on.
            catch (Exception error) when (SessionControl.LoadedButNotListed(error)) { }
            try { host.VerifyEnvironment(plan.WorldExpectations(world.WorldUid)); }
            catch (InvalidOperationException error) when (error.Message.Contains("worlduid:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The hosted world loaded, but it is not world UID {world.WorldUid}: {error.Message}. The client hosted another world under the fixture's name " +
                    "(a wrong fixture, not a load failure; a load failure is reported as one). HostWorldPlan.Preflight reads the fixture's own UID before the run.", error);
            }
        }
        var started = session.WaitForWorld(world.WorldUid, Left(), cancellation, protectPlayer);
        host.PublishWorld(plan, world.WorldUid); // The one place a hosting client's world pins are published; any transition retracts them.
        return started;

        TimeSpan Left()
        {
            var left = timeout - clock.Elapsed;
            return left > TimeSpan.Zero ? left : throw new WaitTimeoutException("the hosted world", clock.Elapsed, null);
        }
    }

    /// <summary>
    /// Leaves the hosted world (the host saves it on the way out), re-pins the client at its menu and starts the same
    /// world again (<see cref="Start"/>): the host's restart. Confirm a save first (<see cref="SessionControl.Save"/>) when
    /// the check is about persistence.
    /// </summary>
    public static SessionState Restart(GameActor host, ClientRunPlan plan, string worldName, TimeSpan timeout, CancellationToken cancellation = default, bool protectPlayer = true)
    {
        new SessionControl(host).Leave();
        host.VerifyEnvironment(plan.MenuExpectations); // A transition always needs fresh pins.
        return Start(host, plan, worldName, timeout, cancellation, protectPlayer);
    }
}
