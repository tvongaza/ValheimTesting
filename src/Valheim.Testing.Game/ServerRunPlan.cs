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
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Source) || Sha256.Count == 0) throw new ArgumentException("A full source path and fixture hashes are required.");
        foreach (var hash in Sha256)
            if (hash.Value.Length != 64 || !hash.Value.All(Uri.IsHexDigit)) throw new ArgumentException("Use full SHA256 fixture hashes.");
    }
}

/// <summary>
/// A plan for owned dedicated-server runs on pinned fixtures (see <see cref="PinnedServerRun"/>). Derive a mod's plan
/// from it and add the mod's scenario fields; <see cref="Read{T}"/> refuses unknown fields. <c>{runtime}</c>,
/// <c>{world}</c> and <c>{port}</c> in arguments and environment values expand to the copies and the CLI port.
/// </summary>
public class ServerRunPlan
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
    public int Port { get; set; } = 5577;
    public int StartupSeconds { get; set; } = 300;
    public int CommandSeconds { get; set; } = 30;

    public static T Read<T>(string path) where T : ServerRunPlan =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
        ?? throw new ArgumentException("Empty plan.");

    /// <summary>
    /// The rules every pinned dedicated-server plan follows: pinned sources, port and time bounds, a known executable,
    /// no runner-owned session token or Doorstop variable in the environment, <c>-batchmode -nographics</c> and exactly
    /// one <c>-savedir {world}</c>, and strict pins with <c>worlduid</c>, an exact MD5 for each of
    /// <paramref name="requiredPlugins"/> and for every other listed plugin, and no <c>worldfiles</c>.
    /// </summary>
    public void ValidateServerPlan(IEnumerable<string> requiredPlugins, string sessionTokenVariable)
    {
        Runtime.Validate(); World.Validate();
        if (Port < 1024 || Port > 65535 || StartupSeconds < 1 || StartupSeconds > 1800 || CommandSeconds < 1 || CommandSeconds > 120)
            throw new ArgumentException("Invalid port or time budget.");
        if (!string.IsNullOrEmpty(Executable) && Executable != ServerLaunch.WindowsExecutable && Executable != ServerLaunch.LinuxExecutable)
            throw new ArgumentException($"Executable must be omitted, {ServerLaunch.WindowsExecutable} or {ServerLaunch.LinuxExecutable} at the copied runtime's root.");
        if (Environment.ContainsKey(sessionTokenVariable)) throw new ArgumentException("Session token is runner-owned.");
        var doorstop = Environment.Keys.FirstOrDefault(key => key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase));
        if (doorstop != null)
            throw new ArgumentException(doorstop + " is refused: BepInEx's Doorstop loader variables are set by the runner's ServerLaunch; remove them from the plan environment.");
        int savedir = Array.IndexOf(Arguments, "-savedir");
        if (savedir < 0 || savedir + 1 >= Arguments.Length || Arguments[savedir + 1] != "{world}" || Arguments.Count(x => x == "-savedir") != 1 ||
            !Arguments.Contains("-batchmode") || !Arguments.Contains("-nographics"))
            throw new ArgumentException("Dedicated launch requires -batchmode -nographics and exactly one -savedir {world}.");
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
    }
    public static string ExecutableFor(ServerPlatform platform) => platform == ServerPlatform.Windows ? ServerLaunch.WindowsExecutable : ServerLaunch.LinuxExecutable;
    /// <summary>The runtime decides the platform (<see cref="ServerLaunch.Detect"/>); a stated executable must agree with it.</summary>
    public void CheckExecutable(ServerPlatform platform)
    {
        if (!string.IsNullOrEmpty(Executable) && Executable != ExecutableFor(platform))
            throw new ArgumentException($"Plan executable {Executable} does not match the copied {platform} runtime, which contains {ExecutableFor(platform)}.");
    }
    /// <summary>A launch needs a host that can execute the runtime's server; macOS runs neither.</summary>
    public static void CheckLaunchHost(ServerPlatform platform, bool windowsHost)
    {
        if ((platform == ServerPlatform.Windows) != windowsHost)
            throw new PlatformNotSupportedException($"A {platform} dedicated-server runtime must run on a {platform} host; use validate here, or run on a matching host or container.");
    }
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
    public string ExpectCommand => Expectations.ExpectCommand(Expectations.ParseLines(Pins.Select(x => x.Key + "=" + x.Value), new()), strict: true);
    public string Expand(string value, string runtime, string world) => value.Replace("{runtime}", runtime).Replace("{world}", world).Replace("{port}", Port.ToString(CultureInfo.InvariantCulture));
    /// <summary>
    /// What an owned dedicated server's startup waits on: ValheimCLI's listening line in this boot's BepInEx log (a
    /// previous boot's lines never count; a BepInEx plugin-load failure ends startup at once), then the loaded-world push
    /// on a loopback connection of its own. A world state is not mod readiness: the session observation decides that.
    /// </summary>
    public StartupEvents DedicatedStartupEvents(string runtime) => new()
    {
        CliLog = Path.Combine(runtime, "BepInEx", "LogOutput.log"),
        Failures = Game.StartupEvents.BepInExPluginLoadFailures,
        States = () => StateWait.Connect("127.0.0.1", Port),
        ReadyStates = [StateWait.InWorldNoPlayer],
    };
}
