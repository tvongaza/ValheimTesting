using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using valheimCLI;

namespace Valheim.Testing.Game;

/// <summary>A fixture directory pinned by the SHA256 of every file (see <see cref="WorldFixture.Manifest"/>).</summary>
public sealed class PinnedDirectory
{
    public string Source { get; set; } = "";
    public Dictionary<string, string> Sha256 { get; set; } = [];
    public void Validate() => Validate(requireHashes: true);
    /// <summary>Without <paramref name="requireHashes"/> the hashes may be left out for a runner that pins the copy as found; listed ones are still full SHA256.</summary>
    public void Validate(bool requireHashes)
    {
        // A run on another host may be assembled on macOS for a Windows host. The source then names an absolute path on the
        // host rather than one this process can open; the hosted runner verifies its bytes through HostListing.
        bool hostAbsolute = System.Text.RegularExpressions.Regex.IsMatch(Source, @"^[A-Za-z]:[\\/]") || Source.StartsWith(@"\\", StringComparison.Ordinal);
        if (!(Path.IsPathFullyQualified(Source) || hostAbsolute) || (requireHashes && Sha256.Count == 0)) throw new ArgumentException("A full source path and fixture hashes are required.");
        foreach (var hash in Sha256)
            if (hash.Value.Length != 64 || !hash.Value.All(Uri.IsHexDigit)) throw new ArgumentException("Use full SHA256 fixture hashes.");
    }
}

/// <summary>
/// A plan for owned dedicated-server runs on pinned fixtures (see <c>PinnedServerRun</c>). Derive a mod's plan
/// from it and add the mod's scenario fields; <see cref="Read{T}"/> refuses unknown fields. <c>{runtime}</c>,
/// <c>{world}</c> and <c>{port}</c> in arguments and environment values expand to the copies and the CLI port.
/// </summary>
public partial class ServerRunPlan
{
    /// <summary>Largest bounded server-start budget, in seconds, including mods that generate a network on first load.</summary>
    public const int MaximumStartupSeconds = 3600;
    public string Scenario { get; set; } = "";
    public PinnedDirectory Runtime { get; set; } = new();
    public PinnedDirectory World { get; set; } = new();
    /// <summary>Optional: documents the plan's platform and must match what the copied runtime contains.</summary>
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = [];
    /// <summary>
    /// Strict ValheimCLI expectations: <c>worlduid</c> and an exact MD5 for every listed plugin. A standalone runner
    /// observes any omitted pins from the fixture and staged profile before launch.
    /// </summary>
    public Dictionary<string, string> Pins { get; set; } = [];
    /// <summary>
    /// The runtime's game build, loader and patchers by SHA256 (<see cref="InstallPins.Of"/> computes them), checked
    /// on the copy before launch: what ValheimCLI cannot report in game. A standalone runner observes them from the staged
    /// profile when omitted; supplied values remain expected pins and are checked against the source install before staging.
    /// </summary>
    public InstallPins? RuntimePins { get; set; }
    public int Port { get; set; } = 5577;
    public int StartupSeconds { get; set; } = 300;
    public int CommandSeconds { get; set; } = 30;
    /// <summary>
    /// How long stopping the owned server (at teardown and for each restart) waits for it to quit after it is asked (SIGINT,
    /// or Ctrl+Break on Windows over SSH, Ctrl+C on other Windows hosts): the game can save the world and retire its crossplay lobby. Killed only after that. 0 kills at
    /// once, without the game's shutdown. Default 120, at most 1800.
    /// </summary>
    public int QuitSeconds { get; set; } = 120;
    // The maximum above, which env recover never exceeds whatever a journal says.
    internal const int MaximumQuitSeconds = 1800;
    /// <summary>
    /// This run's severities for the teardown log scan's patterns (<see cref="LogScanner.Names"/>), each with a written
    /// reason, for example <c>"rpc-method-missing": { "severity": "Failure", "reason": "..." }</c>; under a new name, a pattern
    /// of the run's own with a <c>line</c> regex (<see cref="LogClassification.Line"/>).
    /// </summary>
    public Dictionary<string, LogClassification> LogScan { get; set; } = [];

    public static T Read<T>(string path) where T : ServerRunPlan =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
        ?? throw new ArgumentException("Empty plan.");

    /// <summary>
    /// The rules every dedicated-server plan follows: fully qualified sources, port and time bounds, a known executable,
    /// no runner-owned session token or Doorstop variable in the environment, <c>-batchmode -nographics</c> and exactly
    /// one <c>-savedir {world}</c>, and exact values for every pin the plan supplies: plugin MD5s, no <c>worldfiles</c>,
    /// full fixture hashes and <see cref="RuntimePins"/>. A standalone runner observes omitted pins from what it stages;
    /// after that binding, every <paramref name="requiredPlugins"/> entry must be in the staged plugin set.
    /// Each log scan classification names a known pattern
    /// with a reason. <c>-crossplay</c> comes only from <see cref="Crossplay"/> (<see cref="CheckCrossplay"/>).
    /// </summary>
    public void ValidateServerPlan(IEnumerable<string> requiredPlugins, string sessionTokenVariable)
    {
        ArgumentNullException.ThrowIfNull(requiredPlugins);
        _requiredPlugins = requiredPlugins.Select(plugin =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(plugin);
            return plugin;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Runtime.Validate(requireHashes: false); World.Validate(requireHashes: false);
        CheckLogScan();
        if (Port < 1024 || Port > 65535 || StartupSeconds < 1 || StartupSeconds > MaximumStartupSeconds || CommandSeconds < 1 || CommandSeconds > 120 || QuitSeconds < 0 || QuitSeconds > MaximumQuitSeconds)
            throw new ArgumentException("Invalid port or time budget.");
        if (!string.IsNullOrEmpty(Executable) && Executable != GameLaunch.ServerWindowsExecutable && Executable != GameLaunch.ServerLinuxExecutable && Executable != GameLaunch.ServerMacExecutable)
            throw new ArgumentException($"Executable must be omitted, {GameLaunch.ServerWindowsExecutable}, {GameLaunch.ServerLinuxExecutable} or {GameLaunch.ServerMacExecutable} (relative to the copied runtime's root).");
        if (Environment.ContainsKey(sessionTokenVariable)) throw new ArgumentException("Session token is runner-owned.");
        var doorstop = Environment.Keys.FirstOrDefault(key => key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase));
        if (doorstop != null)
            throw new ArgumentException(doorstop + " is refused: BepInEx's Doorstop loader variables are set by the runner's launch (GameLaunch.ForServer); remove them from the plan environment.");
        int savedir = Array.IndexOf(Arguments, "-savedir");
        if (savedir < 0 || savedir + 1 >= Arguments.Length || Arguments[savedir + 1] != "{world}" || Arguments.Count(x => x == "-savedir") != 1 ||
            !Arguments.Contains("-batchmode") || !Arguments.Contains("-nographics"))
            throw new ArgumentException("Dedicated launch requires -batchmode -nographics and exactly one -savedir {world}.");
        CheckCrossplay();
        var errors = new List<string>();
        var parsed = Expectations.ParseLines(Pins.Select(x => x.Key + "=" + x.Value), errors);
        if (errors.Count != 0) throw new ArgumentException("Invalid environment pins: " + string.Join("; ", errors));
        if (parsed.Any(x => !Expectations.IsWorldKey(x.Key) &&
            (x.Value.Length != 32 || !x.Value.All(Uri.IsHexDigit))))
            throw new ArgumentException("All listed plugins require exact MD5 pins.");
        if (Pins.ContainsKey("worldfiles")) throw new ArgumentException("World bytes change after save; pin input SHA256 and persistent worlduid instead.");
        RuntimePins?.Validate("runtime");
    }
    /// <summary>
    /// Before anything is staged or copied: the world fixture is a dedicated server's <c>-savedir</c>, a directory holding
    /// <c>worlds_local/</c> with exactly one complete world in it (<see cref="WorldIdentity.Read"/>), whose own UID is the plan's
    /// <c>worlduid</c> pin when it has one. Returns that identity, or null for the one fixture without a world: a plan pinned
    /// <c>world=any</c> whose fixture has no world (no file in <c>worlds_local/</c>, or none), which the game fills with a new world on
    /// its first boot.
    /// </summary>
    public WorldIdentity? PreflightWorld() => PreflightWorld(null, World.Source);

    internal WorldIdentity? PreflightWorld(GameBuild? target, string source)
    {
        source = Path.GetFullPath(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The world fixture does not exist: " + source);
        string worlds = Path.Combine(source, "worlds_local");
        bool empty = !Directory.Exists(worlds) || !Directory.EnumerateFiles(worlds, "*", SearchOption.AllDirectories).Any();
        if (empty && Pins.TryGetValue("world", out string? world) && world == "any") return null; // a new world, made on first boot
        if (!Directory.Exists(worlds))
            throw new ArgumentException($"The world fixture {source} has no worlds_local/. A dedicated server's world fixture is its -savedir: a directory holding " +
                "worlds_local/<name>/, the world exactly as the game saved it; the server makes a fresh world when it finds none there. " +
                "Move the world folder into worlds_local/, or pin world=any for a fixture that is meant to start without a world.");
        var identity = target == null ? WorldIdentity.Read(worlds) : WorldIdentity.Read(worlds, target);
        if (Pins.TryGetValue("worlduid", out string? uid) && uid != identity.UidText)
            throw new InvalidOperationException($"The plan pins worlduid={uid}, but the fixture {worlds} holds world {identity.Name} with UID {identity.UidText} ({identity.File}): " +
                "the same name, another world. Pin the UID of this exact fixture (WorldIdentity.Read(fixture).UidText), or point world.source at the fixture the UID belongs to.");
        if (Pins.TryGetValue("world", out string? name) && name != "any" && name != identity.Name)
            throw new InvalidOperationException($"The plan pins world={name}, but the fixture holds world {identity.Name} ({identity.File}).");
        return identity;
    }
    private IReadOnlyList<string> _requiredPlugins = [];
    /// <summary>
    /// After a strict runner binds its staged pins, refuses a required plugin that is missing or pinned absent. Validation
    /// retains this list because omitted plugin pins cannot be decided until the standalone profile exists.
    /// </summary>
    internal void RequireStagedPlugins()
    {
        var missing = _requiredPlugins.Where(plugin => !Pins.TryGetValue(plugin, out string? value) || value == "absent").ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"The staged server profile is missing required plugin{(missing.Length == 1 ? "" : "s")} {string.Join(", ", missing)}. Install the mod and its test adapter in the source install, or select them in the campaign dependency lock.");
    }
    public static string ExecutableFor(ServerPlatform platform) => platform switch
    {
        ServerPlatform.Windows => GameLaunch.ServerWindowsExecutable,
        ServerPlatform.MacOS => GameLaunch.ServerMacExecutable,
        _ => GameLaunch.ServerLinuxExecutable,
    };
    /// <summary>The runtime decides the platform (<see cref="GameLaunch.DetectServer"/>); a stated executable must agree with it.</summary>
    public void CheckExecutable(ServerPlatform platform)
    {
        if (!string.IsNullOrEmpty(Executable) && Executable != ExecutableFor(platform))
            throw new ArgumentException($"Plan executable {Executable} does not match the copied {platform} runtime, which contains {ExecutableFor(platform)}.");
    }
    /// <summary>A launch needs a host that can execute the runtime's server: a Windows host or not (then Linux).</summary>
    public static void CheckLaunchHost(ServerPlatform platform, bool windowsHost) =>
        CheckLaunchHost(platform, windowsHost ? ServerPlatform.Windows : ServerPlatform.Linux);
    /// <summary>
    /// A launch needs a host of the runtime's own platform: each dedicated server runs only on its own OS (see
    /// <see cref="GameLaunch.LocalServerPlatform"/> for this machine's).
    /// </summary>
    public static void CheckLaunchHost(ServerPlatform platform, ServerPlatform host)
    {
        if (platform != host)
            throw new PlatformNotSupportedException($"A {platform} dedicated-server runtime must run on a {platform} host, not this {host} one; use validate here, " +
                "or run on a matching host, the Linux server container, or a Linux host with --inventory.");
    }
    /// <summary>The plan's log scan classifications are well formed (the runner checks them for every plan).</summary>
    public void CheckLogScan() => LogScanner.CheckClassifications(LogScan);
    public void CheckOutput(string output)
    {
        foreach (string source in new[] { Runtime.Source, World.Source })
            if (ProtectedPaths.Contains(source, output))
                throw new ArgumentException("Output must be outside both pinned sources.");
    }
    /// <summary>The strict <c>cli_expect</c> command for the plan's complete bound pins.</summary>
    public string ExpectCommand => Expectations.ExpectCommand(Expectations.ParseLines(Pins.Select(x => x.Key + "=" + x.Value), new()), strict: true);
    /// <summary>
    /// The file the arguments' <c>-logFile</c> names, expanded (for example <c>{runtime}/toolkit-unity.log</c>), or null. The game
    /// writes all its own lines there, lobby lines included; BepInEx's log carries them only when BepInEx copies Unity's log.
    /// </summary>
    public string? GameLogFile(string runtime, string world)
    {
        int at = Array.FindIndex(Arguments, argument => argument.Equals("-logFile", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < Arguments.Length ? Expand(Arguments[at + 1], runtime, world) : null;
    }

    public string Expand(string value, string runtime, string world) => value.Replace("{runtime}", runtime).Replace("{world}", world).Replace("{port}", Port.ToString(CultureInfo.InvariantCulture));
    /// <summary>
    /// What an owned dedicated server's startup waits on: ValheimCLI's listening line in this boot's BepInEx log (a
    /// previous boot's lines never count; a plugin-load failure, a type-load or missing-member exception, or ValheimCLI's
    /// "core is not ready" ends startup at once: <see cref="StartupEvents.StartupFailures"/>), then the loaded-world push
    /// on a loopback connection of its own. A world state is not mod readiness: the session observation decides that.
    /// </summary>
    public StartupEvents DedicatedStartupEvents(string runtime) => new()
    {
        CliLog = Path.Combine(runtime, "BepInEx", "LogOutput.log"),
        Failures = Game.StartupEvents.StartupFailures,
        States = () => StateWait.Connect("127.0.0.1", Port),
        ReadyStates = [StateWait.InWorldNoPlayer],
    };
}
