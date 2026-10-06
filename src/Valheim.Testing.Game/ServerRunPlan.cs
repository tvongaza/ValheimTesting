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
    /// <summary>Without <paramref name="requireHashes"/> (an explicitly unpinned plan) the hashes may be left out; listed ones are still full SHA256.</summary>
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
/// A plan for owned dedicated-server runs on pinned fixtures (see <see cref="PinnedServerRun"/>). Derive a mod's plan
/// from it and add the mod's scenario fields; <see cref="Read{T}"/> refuses unknown fields. <c>{runtime}</c>,
/// <c>{world}</c> and <c>{port}</c> in arguments and environment values expand to the copies and the CLI port.
/// </summary>
public partial class ServerRunPlan
{
    public string Scenario { get; set; } = "";
    public PinnedDirectory Runtime { get; set; } = new();
    public PinnedDirectory World { get; set; } = new();
    /// <summary>Optional: documents the plan's platform and must match what the copied runtime contains.</summary>
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = [];
    /// <summary>Strict ValheimCLI expectations: <c>worlduid</c> and an exact MD5 for every listed plugin.</summary>
    public Dictionary<string, string> Pins { get; set; } = [];
    /// <summary>
    /// The runtime's game build, loader and patchers by SHA256 (<see cref="InstallPins.Of"/> computes them), checked
    /// on the copy before launch: what ValheimCLI cannot report in game. Required unless <see cref="Pinning"/> is <c>none</c>.
    /// </summary>
    public InstallPins? RuntimePins { get; set; }
    /// <summary>
    /// <c>strict</c>, the default when omitted, or <c>none</c>: an explicit opt-out for trying the toolkit before keeping
    /// pins. An unpinned plan lists no <see cref="Pins"/> or <see cref="RuntimePins"/> and may leave out the fixture
    /// hashes (copies are then recorded as found); the runner warns at start and marks every report and evidence file
    /// "environment not pinned" (<see cref="EnvironmentPinning"/>).
    /// </summary>
    public string Pinning { get; set; } = EnvironmentPinning.Strict;
    /// <summary>Whether <see cref="Pinning"/> is <c>strict</c>; refuses any value but <c>strict</c> or <c>none</c>.</summary>
    [JsonIgnore] public bool Pinned => EnvironmentPinning.IsStrict(Pinning, "The plan's");
    public int Port { get; set; } = 5577;
    public int StartupSeconds { get; set; } = 300;
    public int CommandSeconds { get; set; } = 30;
    /// <summary>
    /// How long stopping the owned server (at teardown and for each restart) waits for it to quit after it is asked (SIGINT,
    /// or Ctrl+Break on Windows over SSH, Ctrl+C on other Windows hosts): the game can save the world and retire its crossplay lobby. Killed only after that. 0 kills at
    /// once, without the game's shutdown. Default 120, at most 1800.
    /// </summary>
    public int QuitSeconds { get; set; } = 120;
    // Removed (#295): patcher names beside the patchers hash described one folder twice. A plan that still names them is
    // refused with what to do instead of the generic unknown-field error.
    [JsonInclude, JsonPropertyName("patchers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? RemovedPatchers { get => null; set => throw new ArgumentException($"The plan's patchers was removed (ValheimTesting #295): {PatchersInstead}. Delete patchers from the plan."); }
    internal const string PatchersInstead = "the patchers pin in runtimePins and installPins hashes everything BepInEx/patchers holds, so a patcher a removed mod left behind is refused by the pin, which names what the folder holds; an explicitly unpinned run checks no patchers, like the rest of its install";
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
    /// The rules every pinned dedicated-server plan follows: pinned sources, port and time bounds, a known executable,
    /// no runner-owned session token or Doorstop variable in the environment, <c>-batchmode -nographics</c> and exactly
    /// one <c>-savedir {world}</c>, and strict pins with <c>worlduid</c>, an exact MD5 for each of
    /// <paramref name="requiredPlugins"/> and for every other listed plugin, no <c>worldfiles</c>, and
    /// <see cref="RuntimePins"/>. Each log scan classification names a known pattern
    /// with a reason. <c>-crossplay</c> comes only from <see cref="Crossplay"/> (<see cref="CheckCrossplay"/>). An explicitly unpinned plan (<see cref="Pinning"/> <c>none</c>) follows the same rules without the
    /// pins, which it must leave out, and may leave out the fixture hashes.
    /// </summary>
    public void ValidateServerPlan(IEnumerable<string> requiredPlugins, string sessionTokenVariable)
    {
        bool pinned = Pinned;
        Runtime.Validate(pinned); World.Validate(pinned);
        CheckLogScan();
        if (Port < 1024 || Port > 65535 || StartupSeconds < 1 || StartupSeconds > 1800 || CommandSeconds < 1 || CommandSeconds > 120 || QuitSeconds < 0 || QuitSeconds > 1800)
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
        if (!pinned)
        {
            if (Pins.Count != 0 || RuntimePins != null)
                throw new ArgumentException("A plan with pinning \"none\" lists no pins and no runtimePins: nothing would check them. Remove them, or remove \"pinning\" to keep strict pins.");
            return;
        }
        var errors = new List<string>();
        var parsed = Expectations.ParseLines(Pins.Select(x => x.Key + "=" + x.Value), errors);
        if (errors.Count != 0) throw new ArgumentException("Invalid environment pins: " + string.Join("; ", errors));
        if (!Pins.ContainsKey("worlduid")) throw new ArgumentException("Pin worlduid.");
        foreach (string plugin in requiredPlugins)
            if (!Pins.TryGetValue(plugin, out var value) || value.Length != 32 || !value.All(Uri.IsHexDigit))
                throw new ArgumentException("Pin a full MD5 for " + plugin + ".");
        if (parsed.Any(x => !Expectations.IsWorldKey(x.Key) && (x.Value == "any" || x.Value == "absent")))
            throw new ArgumentException("All listed plugins require exact MD5 pins.");
        if (Pins.ContainsKey("worldfiles")) throw new ArgumentException("World bytes change after save; pin input SHA256 and persistent worlduid instead.");
        if (RuntimePins == null)
            throw new ArgumentException("Pin the runtime's game build, loader and patchers in runtimePins (InstallPins.Of computes them), or opt out explicitly with \"pinning\": \"none\".");
        RuntimePins.Validate("runtime");
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
    /// <summary>
    /// Pinned: refuses a runtime whose game build, loader or patchers are not <see cref="RuntimePins"/>. Unpinned:
    /// checks nothing. Either way returns what the runtime holds, for the report.
    /// </summary>
    public InstallPins CheckRuntimePins(string runtime) => !Pinned ? InstallPins.Of(runtime) :
        (RuntimePins ?? throw new ArgumentException("Pin the runtime's game build, loader and patchers in runtimePins, or opt out explicitly with \"pinning\": \"none\"."))
            .Check(runtime, "runtime");
    public void CheckOutput(string output)
    {
        output = Path.GetFullPath(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string source in new[] { Runtime.Source, World.Source })
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            if (output.Equals(full, comparison) || output.StartsWith(full + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Output must be outside both pinned sources.");
        }
    }
    /// <summary>The strict <c>cli_expect</c> command, or <see cref="EnvironmentPinning.None"/> for an unpinned plan (see <see cref="GameActor.VerifyEnvironment"/>).</summary>
    public string ExpectCommand => !Pinned ? EnvironmentPinning.None :
        Expectations.ExpectCommand(Expectations.ParseLines(Pins.Select(x => x.Key + "=" + x.Value), new()), strict: true);
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
