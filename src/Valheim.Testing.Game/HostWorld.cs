using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The <c>hostWorld</c> section of a <see cref="ClientRunPlan"/>: the client hosts this fixture world from its menu (a
/// listen server) instead of joining a server. A host's world is the client's own local world, so the runner copies the
/// pinned fixture into the client's local worlds for the run (<see cref="HostedWorld"/>) and never uses a world already
/// there. Unknown fields are refused with the rest of the plan.
/// </summary>
public sealed class HostWorldPlan
{
    /// <summary>
    /// The fixture, in either layout the game (1.0.16) loads: a chunked save as Valheim 1.0 writes it, one directory
    /// <c>&lt;name&gt;/</c> at the root holding <c>_main.&lt;n&gt;.fwl2</c> and its chunks; or the older pair, one
    /// <c>&lt;name&gt;.fwl</c> at the root with its data beside it (<c>&lt;name&gt;.db</c>). Every entry is named for the world. Pinned by SHA256 (<see cref="WorldFixture.Manifest"/>) unless the client plan's pinning is <c>none</c>.
    /// </summary>
    public PinnedDirectory World { get; set; } = new();
    /// <summary>
    /// The fixture world's exact UID, required even without pins: the host must load that world, and the game silently
    /// creates a fresh world when the named one is missing.
    /// </summary>
    public string WorldUid { get; set; } = "";
    /// <summary>Hosts a crossplay world (the game's PlayFab backend, <c>--crossplay true</c>).</summary>
    public bool Crossplay { get; set; }
    /// <summary>
    /// The client's data directory, which holds <c>worlds_local</c>. Default: this user's Valheim data directory for the
    /// client's platform (<see cref="HostedWorld.DefaultSaveDirectory"/>); set it when the client runs as another user.
    /// </summary>
    public string? SaveDirectory { get; set; }
    /// <summary>The confirmed save's timeout between rounds, 1 to 600 seconds.</summary>
    public int SaveSeconds { get; set; } = 120;

    /// <summary>
    /// Refuses the section before anything is copied: an unpinned or unnamed fixture (hashes are required when
    /// <paramref name="pinned"/>; listed hashes must name one world), no exact world UID, a relative save directory or a
    /// save timeout out of range.
    /// </summary>
    public void Validate(bool pinned)
    {
        World.Validate(pinned);
        if (World.Sha256.Count != 0) _ = HostedWorld.NameOf(World.Sha256.Keys);
        if (!long.TryParse(WorldUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("hostWorld.worldUid: give the fixture world's exact UID. The host must load that world, and the game silently creates a fresh one when it is missing.");
        if (SaveDirectory != null && !Path.IsPathFullyQualified(SaveDirectory))
            throw new ArgumentException("hostWorld.saveDirectory: give the full path of the client's data directory (the one that holds worlds_local), or leave it out for this user's default.");
        if (SaveSeconds is < 1 or > 600) throw new ArgumentException("hostWorld.saveSeconds is 1 to 600.");
    }
}

/// <summary>
/// A fixture world placed in a game client's local worlds for one hosted run. <see cref="Place"/> copies and verifies
/// the fixture into the output directory, as the server runner does (that copy stays as the input evidence), then copies
/// it into <c>worlds_local</c>, refusing when anything named for the world is already there: a user's world is never
/// overwritten or used. <see cref="Collect"/> (also <see cref="Dispose"/>) moves the world, with whatever the game wrote
/// for it (saves, <c>.old</c> files, backups), out of the client's worlds into <c>host-world</c> in the output directory.
/// Collect only once the client no longer hosts it: after an owned client stopped, or after the host left to its menu.
/// </summary>
public sealed class HostedWorld : IDisposable
{
    private readonly string _output;
    private string? _target;

    /// <summary>The world's name, the stem of the fixture's <c>.fwl</c>: what <c>cli_start_host_world</c> starts.</summary>
    public string Name { get; }
    public string WorldUid { get; }
    /// <summary>The client's <c>worlds_local</c> directory.</summary>
    public string WorldsDirectory { get; }
    /// <summary>The verified copy of the fixture in the output directory.</summary>
    public string FixtureCopy { get; }
    /// <summary>Where <see cref="Collect"/> moved the world, or null before it has.</summary>
    public string? CollectedTo { get; private set; }

    private HostedWorld(string name, string worldUid, string worlds, string copy, string output)
    { Name = name; WorldUid = worldUid; WorldsDirectory = worlds; FixtureCopy = copy; _output = output; }

    /// <summary>
    /// Copies <paramref name="plan"/>'s fixture into <paramref name="output"/> (verified against its hashes, or recorded as
    /// found for an unpinned plan without them) and then into <paramref name="saveDirectory"/>'s <c>worlds_local</c>.
    /// </summary>
    public static HostedWorld Place(HostWorldPlan plan, string saveDirectory, string output, bool pinned)
    {
        if (!Path.IsPathFullyQualified(saveDirectory)) throw new ArgumentException("Give the client's data directory as a full path.", nameof(saveDirectory));
        bool verified = pinned || plan.World.Sha256.Count != 0;
        var copy = verified ? WorldFixture.Copy(plan.World.Source, output, plan.World.Sha256) : WorldFixture.CopyAsFound(plan.World.Source, output);
        copy.Preserve = true;
        string name = NameOf(copy.SourceHashes.Keys);
        string worlds = Path.Combine(saveDirectory, "worlds_local");
        Directory.CreateDirectory(worlds);
        if (Entries(worlds, name).FirstOrDefault() is { } existing)
            throw new InvalidOperationException($"The client's worlds already hold {Path.GetFileName(existing)} in {worlds}: a fixture world is never copied over a world. Remove or rename that one yourself, or rename the fixture world.");
        try
        {
            foreach (var item in copy.SourceHashes)
            {
                string target = Path.Combine(worlds, item.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var from = File.OpenRead(Path.Combine(copy.DirectoryPath, item.Key)))
                using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    from.CopyTo(to);
                if (WorldFixture.Hash(target) != item.Value) throw new IOException("The placed fixture world changed while copying: " + item.Key);
            }
        }
        catch
        {
            // Nothing named for the world was there before, so everything named for it now is this copy's.
            foreach (string entry in Entries(worlds, name).ToArray())
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
            throw;
        }
        return new HostedWorld(name, plan.WorldUid, worlds, copy.DirectoryPath, output);
    }

    /// <summary>
    /// The world's name from a fixture's relative paths, in either layout the game loads: a chunked save (exactly one
    /// directory <c>&lt;name&gt;/</c> at the root with a <c>_main.&lt;n&gt;.fwl2</c> in it, as Valheim 1.0 writes worlds) or the
    /// older pair (exactly one <c>&lt;name&gt;.fwl</c> at the root). The name is at least 3 characters and a valid file name,
    /// and every other entry is named for it (<c>&lt;name&gt;.*</c>, <c>&lt;name&gt;_*</c> or inside <c>&lt;name&gt;/</c>).
    /// </summary>
    public static string NameOf(IEnumerable<string> relativePaths)
    {
        var paths = relativePaths.ToArray();
        var worlds = paths.Where(path => path.IndexOfAny(['/', '\\']) < 0 && path.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase)).ToArray();
        var chunked = paths.Where(path => path.Split('/', '\\') is [_, var file] && Regex.IsMatch(file, @"^_main\.\d+\.fwl2$", RegexOptions.CultureInvariant))
            .Select(path => path.Split('/', '\\')[0]).Distinct(StringComparer.Ordinal).ToArray();
        if (worlds.Length + chunked.Length != 1)
            throw new ArgumentException($"A host fixture holds exactly one world: one <name>/ directory with _main.<n>.fwl2 (Valheim 1.0's chunked save) or one <name>.fwl at its root, not {worlds.Length + chunked.Length}.");
        string name = chunked.Length == 1 ? chunked[0] : worlds[0][..^".fwl".Length];
        if (name.Length < 3 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsWhiteSpace))
            throw new ArgumentException($"The fixture world's name \"{name}\" must be at least 3 characters, one token and a valid file name on every platform.");
        foreach (string path in paths)
        {
            string first = path.Split('/', '\\')[0];
            if (!Named(first, name)) throw new ArgumentException($"The host fixture holds {path}, which is not named for its world {name}; keep only the world's files.");
        }
        if (chunked.Length == 0 && paths.Length < 2) throw new ArgumentException($"The host fixture holds only {worlds[0]}: add the world's data ({name}.db), or the game generates the world afresh.");
        return name;
    }

    /// <summary>Where Unity keeps a game client's data on <paramref name="platform"/> (company IronGate, product Valheim), which holds <c>worlds_local</c>.</summary>
    public static string DefaultSaveDirectory(ClientPlatform platform)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return platform switch
        {
            ClientPlatform.Windows => Path.Combine(home, "AppData", "LocalLow", "IronGate", "Valheim"),
            ClientPlatform.MacOS => Path.Combine(home, "Library", "Application Support", "IronGate", "Valheim"),
            _ => Path.Combine(home, ".config", "unity3d", "IronGate", "Valheim"),
        };
    }

    /// <summary>This machine's platform, for an attached client (whose install the runner does not read).</summary>
    public static ClientPlatform CurrentPlatform => OperatingSystem.IsWindows() ? ClientPlatform.Windows : OperatingSystem.IsMacOS() ? ClientPlatform.MacOS : ClientPlatform.Linux;

    /// <summary>
    /// Moves every entry named for the world out of the client's worlds into <c>host-world</c> (then <c>host-world-2</c>
    /// and so on) in the output directory. Runs once; a failure leaves the rest in place and is rethrown.
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
        Directory.CreateDirectory(_target);
        foreach (string entry in Entries(WorldsDirectory, Name).ToArray()) Move(entry, Path.Combine(_target, Path.GetFileName(entry)));
        CollectedTo = _target;
    }

    public void Dispose() => Collect();

    private static bool Named(string entry, string name) =>
        entry.Equals(name, StringComparison.OrdinalIgnoreCase) || entry.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) || entry.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Entries(string worlds, string name) =>
        Directory.Exists(worlds) ? Directory.EnumerateFileSystemEntries(worlds).Where(entry => Named(Path.GetFileName(entry), name)) : Enumerable.Empty<string>();

    private static void Move(string from, string to)
    {
        if (!Directory.Exists(from)) { File.Move(from, to); return; }
        Directory.CreateDirectory(to);
        foreach (string child in Directory.EnumerateFileSystemEntries(from).ToArray()) Move(child, Path.Combine(to, Path.GetFileName(child)));
        Directory.Delete(from);
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
    /// is false (<see cref="SessionControl.WaitForWorld"/>). The whole start is bounded by <paramref name="timeout"/>.
    /// </summary>
    public static SessionState Start(GameActor host, ClientRunPlan plan, string worldName, TimeSpan timeout, CancellationToken cancellation = default, bool protectPlayer = true)
    {
        var world = plan.HostWorld ?? throw new ArgumentException("The client plan has no hostWorld section.", nameof(plan));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (string.IsNullOrEmpty(worldName) || worldName.Any(char.IsWhiteSpace)) throw new ArgumentException("The world name must be a single token.", nameof(worldName));
        var clock = Stopwatch.StartNew();
        var session = new SessionControl(host);
        var before = session.Read();
        if (before.Phase != "menu" || before.WorldPresent) throw new InvalidOperationException("A hosted world starts from the client's idle main menu.");
        session.EnableDevcommands();
        var selected = host.Execute("cli_select_character " + plan.Character, requireSuccess: false);
        if (!selected.Output.Any(line => line.StartsWith("OK: Selected character '", StringComparison.Ordinal)))
            throw new InvalidOperationException("The character was not selected: " + string.Join(" | ", selected.Output));
        string crossplay = world.Crossplay ? "true" : "false";
        try
        {
            var reply = host.Execute($"cli_start_host_world {worldName} --public false --crossplay {crossplay}", requireSuccess: false); // Exactly once.
            string? line = reply.Output.FirstOrDefault(l => l.StartsWith("OK: Starting hosted world '", StringComparison.Ordinal));
            string ending = $"; open=true, public=False, crossplay={(world.Crossplay ? "True" : "False")}, backend={(world.Crossplay ? "PlayFab" : "Steamworks")}, passwordSet=False";
            if (line == null || !line.StartsWith($"OK: Starting hosted world '{worldName}' using ", StringComparison.Ordinal) || !line.EndsWith(ending, StringComparison.Ordinal))
                throw new InvalidOperationException("The hosted world did not start as planned: " + (line ?? string.Join(" | ", reply.Output)));
        }
        finally { host.InvalidateEnvironment(); } // A start that may have begun changes the world.
        host.VerifyEnvironment(plan.MenuExpectations); // Plugins only: the world is still loading.
        session.WaitForWorld(world.WorldUid, Left(), cancellation, protectPlayer: false);
        host.VerifyEnvironment(plan.WorldExpectations(world.WorldUid));
        return session.WaitForWorld(world.WorldUid, Left(), cancellation, protectPlayer);

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

/// <summary>
/// The hosted (listen-server) twin of <see cref="ClientRounds"/>: one game client hosts a fixture world and is both the
/// server and the client of each check. <see cref="Run"/> places the fixture world (<see cref="HostedWorld"/>), opens the
/// client, then for each of <see cref="Rounds"/>:
/// <list type="number">
/// <item>hosts the world with the plan's disposable character, protected (<see cref="HostWorlds.Start"/>; from the second round on, this is the host world's restart);</item>
/// <item>runs the mod's measurement, whose <see cref="ClientRound.Server"/> and <see cref="ClientRound.Client"/> are the same host;</item>
/// <item>between rounds, a confirmed world save (<see cref="SessionControl.Save"/>); after every round the host leaves to its menu, which saves again.</item>
/// </list>
/// The report records <c>role</c> <c>host</c>, <c>hostWorld</c>, <c>hostCrossplay</c>, <c>hostRounds</c> and
/// <c>hostRoundsCompleted</c>. The first failure stops the rounds and is rethrown. The client is closed in every outcome
/// (an owned client stopped, an attached one detached), then the world is moved into the evidence
/// (<c>hostWorldEvidence</c>). After a failure an attached client may still host the world, so its world is left in place
/// and named in <c>hostWorldLeftInPlace</c>: remove it once the client has left it.
/// </summary>
public sealed class HostRounds
{
    /// <summary>The client plan, with its <see cref="ClientRunPlan.HostWorld"/> section.</summary>
    public required ClientRunPlan Client { get; init; }
    public required ScenarioReport Report { get; init; }
    public required string Output { get; init; }
    /// <summary>The client's opening step's name; the default says whether it is launched or attached, with plugins pinned.</summary>
    public string? OpenStep { get; init; }
    /// <summary>The rounds' names, which prefix their steps and evidence files: letters, digits, <c>-</c> and <c>_</c>, all different.</summary>
    public IReadOnlyList<string> Rounds { get; init; } = ["first", "after-restart"];
    public CancellationToken Cancellation { get; init; }

    /// <summary>Runs the rounds, opening the client with <paramref name="openClient"/> (for example <see cref="ClientSession.Open"/>).</summary>
    public void Run(Func<ClientSession> openClient, Action<ClientRound> measure)
    {
        var plan = Client.HostWorld ?? throw new ArgumentException("Client: the plan has no hostWorld section; a client that joins a server runs with ClientRounds.");
        ClientRounds.CheckRoundNames(Rounds);
        Report.Provenance["role"] = "host";
        Report.Provenance["hostCrossplay"] = plan.Crossplay ? "true" : "false";
        Report.Provenance["hostRounds"] = string.Join(",", Rounds);
        var completed = new List<string>();
        HostedWorld? world = null;
        ClientSession? session = null;
        bool passed = false;
        try
        {
            string saveDirectory = plan.SaveDirectory ?? HostedWorld.DefaultSaveDirectory(Client.Owned ? ClientLaunch.Detect(Client.Install) : HostedWorld.CurrentPlatform);
            Report.Step("place the disposable fixture world in the client's local worlds", () => world = HostedWorld.Place(plan, saveDirectory, Output, Client.Pinned));
            var placed = world!;
            Report.Provenance["hostWorld"] = placed.Name;
            Report.Step(OpenStep ?? (Client.Owned ? "launch the owned client to its menu, plugins pinned" : "attach to the operator's client at its menu, plugins pinned"),
                () => session = openClient());
            var host = session!.Actor;
            for (int i = 0; i < Rounds.Count; i++)
            {
                var round = new ClientRound(Rounds[i], i, i == Rounds.Count - 1, host, host, Report, Output);
                round.Step(i == 0 ? "host the fixture world with the disposable character, protected" : "restart the hosted world, protected",
                    () => HostWorlds.Start(host, Client, placed.Name, TimeSpan.FromSeconds(Client.JoinSeconds), Cancellation));
                measure(round);
                if (!round.Last)
                    Report.Step(ClientRounds.Between("confirmed world save", i, Rounds), () => new SessionControl(host).Save(plan.WorldUid, TimeSpan.FromSeconds(plan.SaveSeconds)));
                round.Step("the host leaves to its menu", () =>
                {
                    new SessionControl(host).Leave();
                    host.VerifyEnvironment(Client.MenuExpectations); // A transition always needs fresh pins.
                });
                completed.Add(round.Name);
                Report.Provenance["hostRoundsCompleted"] = string.Join(",", completed);
            }
            passed = true;
        }
        finally
        {
            // The world may be moved once no client can still host it: none was opened, the owned one stopped, or every round ended with the host at its menu.
            bool released = session == null || passed;
            Exception? teardown = null;
            if (session != null)
                try { Report.Step(session.Owned ? "stop only the owned client" : "detach from the operator's client", session.Dispose); released |= session.Owned; }
                catch (Exception error) { teardown = error; } // Recorded as its own failed step.
            if (world != null)
            {
                if (!released) Report.Provenance["hostWorldLeftInPlace"] = world.WorldsDirectory + " (" + world.Name + ")";
                else
                    try { Report.Step("move the hosted world from the client's local worlds into the evidence", world.Collect); Report.Provenance["hostWorldEvidence"] = world.CollectedTo!; }
                    catch (Exception error) { teardown ??= error; }
            }
            // A failure already on its way out is the one to rethrow; a failed teardown fails a passing run.
            if (passed && teardown != null) ExceptionDispatchInfo.Capture(teardown).Throw();
        }
    }
}
