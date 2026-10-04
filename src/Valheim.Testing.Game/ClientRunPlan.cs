using System.Text.Json;
using System.Text.Json.Serialization;
using valheimCLI;

namespace Valheim.Testing.Game;

/// <summary>
/// The game client a system test joins to its server, as a plan section. <c>owned</c>: the runner launches it from
/// <see cref="Install"/> and stops only that process (<see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/>);
/// it must run in the desktop session where Steam is running and signed in, with a display. <c>attach</c>: an operator
/// launched and signed in the client; the runner only connects to its ValheimCLI port and never touches the process.
/// The join password stays in the client's own process environment, named by <see cref="PasswordVariable"/>.
/// </summary>
public sealed class ClientRunPlan
{
    public string Mode { get; set; } = "";
    /// <summary>Owned only: the client install to launch, with BepInEx and ValheimCLI.</summary>
    public string Install { get; set; } = "";
    /// <summary>Owned only: extra game arguments; <see cref="ClientLaunch"/> adds <c>-console</c>.</summary>
    public string[] LaunchArguments { get; set; } = [];
    /// <summary>
    /// Owned only: the slice a macOS client (<c>Valheim.app</c>) runs as, <c>x64</c> or <c>arm64</c>. Left out, it is <c>x64</c>:
    /// under Rosetta on Apple Silicon, BepInExPack_Valheim's own loader and core work as installed, so a plan means the same
    /// process on every Mac. <c>arm64</c> is the native path: the install needs a Doorstop library with an arm64 slice and a
    /// BepInEx core built on MonoMod 25 or later, and the launch refuses one without them rather than fall back to Rosetta.
    /// Windows and Linux clients are x64 only, so <c>arm64</c> is refused for them. <see cref="Validate"/> runs the launch's slice
    /// and core check on a <c>Valheim.app</c> install on this machine, so a runner refuses such a plan before it starts anything.
    /// Recorded in <c>client-process.json</c>.
    /// </summary>
    public string Architecture { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>The client's ValheimCLI port (its <c>[Server] Port</c> setting); it must differ from the server's.</summary>
    public int Port { get; set; }
    /// <summary>Every plugin the client loads by exact MD5, or <c>absent</c>. No world key: the runner adds the server's.</summary>
    public Dictionary<string, string> Pins { get; set; } = [];
    /// <summary>The server address the client joins, host:port. Left out for a <see cref="Crossplay"/> or hosting (<see cref="HostWorld"/>) client.</summary>
    public string Join { get; set; } = "";
    /// <summary>
    /// Joins the server's crossplay (PlayFab) lobby instead of its address (<see cref="SessionControl.JoinCrossplay"/>),
    /// for a server plan with <see cref="ServerRunPlan.Crossplay"/>. Leave out <see cref="Join"/> and
    /// <see cref="PasswordVariable"/>: the crossplay join command would carry a password as text, so the fixture server runs
    /// private without one.
    /// </summary>
    public bool Crossplay { get; set; }
    /// <summary>
    /// The client hosts this fixture world from its menu (a listen server) instead of joining a server
    /// (<see cref="HostRounds"/>). Leave out <see cref="Join"/>, <see cref="PasswordVariable"/> and <see cref="Crossplay"/>.
    /// </summary>
    public HostWorldPlan? HostWorld { get; set; }
    /// <summary>An existing, disposable local character (never a cloud character).</summary>
    public string Character { get; set; } = "";
    // Removed (#298): direct start and prepared-character start saved 0.07 s over the menu start. A plan that still names
    // one is refused with what to do instead of the generic unknown-field error.
    [JsonInclude, JsonPropertyName("directStart"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? RemovedDirectStart { get => null; set => throw Removed("directStart"); }
    [JsonInclude, JsonPropertyName("directStartWorldUid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? RemovedDirectStartWorldUid { get => null; set => throw Removed("directStartWorldUid"); }
    [JsonInclude, JsonPropertyName("startAtCharacterSave"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? RemovedStartAtCharacterSave { get => null; set => throw Removed("startAtCharacterSave"); }
    [JsonInclude, JsonPropertyName("characterStart"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? RemovedCharacterStart { get => null; set => throw Removed("characterStart"); }
    private static ArgumentException Removed(string field) => new(
        $"The client plan's {field} was removed (ValheimTesting #298): an owned client launches to its menu and joins (or hosts), " +
        "and the first arrival teleports. Delete directStart, directStartWorldUid, startAtCharacterSave and characterStart from the plan.");
    /// <summary>
    /// Use ValheimCLI's bounded, game-side teleport readiness and support waits for arrival instead of repeated
    /// remote observations. Requires the current Standard and World Tools packs. The ordinary game teleport timing
    /// remains in force unless <see cref="FastTestTeleports"/> is also selected.
    /// </summary>
    public bool EventDrivenArrival { get; set; }
    /// <summary>
    /// Owned, strictly pinned test clients only: allow ValheimCLI to complete a distant teleport once the game's
    /// area and floor checks pass after its initial movement. The test launch sets its opt-in environment marker;
    /// the runner switches the mode on for each arrival hop and off once it lands. This does not preload terrain or
    /// bypass support checks. Only a client running its own local fixture world may use it. A joined client's area
    /// check covers only objects the server has already sent: a native cold hop finished while its site marker had
    /// not arrived, so joined clients are refused before launch.
    /// </summary>
    public bool FastTestTeleports { get; set; }
    /// <summary>The environment variable, in the client's process, that holds the join password.</summary>
    public string? PasswordVariable { get; set; }
    public int StartSeconds { get; set; } = 300;
    /// <summary>
    /// Owned only: how long after the launch BepInEx may take to write its first line to this launch's
    /// <c>BepInEx/LogOutput.log</c>, 5 to 1800 seconds (default 60; never more than <see cref="StartSeconds"/>). Doorstop starts
    /// BepInEx before the game's first frame, so a game still running without that line runs without BepInEx, and startup
    /// ends then instead of at the start deadline.
    /// </summary>
    public int BepInExSeconds { get; set; } = 60;
    public int JoinSeconds { get; set; } = 180;
    public int ArrivalSeconds { get; set; } = 120;
    /// <summary>Owned only: every entry the install's <c>BepInEx/patchers</c> holds, by name; the launch refuses any other.</summary>
    public string[] Patchers { get; set; } = [];
    /// <summary>
    /// Owned only: the install's game build, BepInEx core and patchers by SHA256 (<see cref="Valheim.Testing.Game.InstallPins.Of"/>),
    /// checked before launch. Required for an owned client unless <see cref="Pinning"/> is <c>none</c>. An attached
    /// client's install is its operator's and is not read, so its build is not pinned (the game refuses a join only across
    /// network versions).
    /// </summary>
    public InstallPins? InstallPins { get; set; }
    /// <summary>
    /// <c>strict</c>, the default when omitted, or <c>none</c>: the explicit opt-out. An unpinned client lists no
    /// <see cref="Pins"/> or <see cref="InstallPins"/>, its actor runs without <c>cli_expect</c> (warned at start) and its
    /// command record starts with the "environment not pinned" marker.
    /// </summary>
    public string Pinning { get; set; } = EnvironmentPinning.Strict;
    /// <summary>
    /// The ValheimCLI extension commands (<c>owner/command</c>, such as <c>valheim.world/terrain</c>) the run uses beyond what
    /// its runner requires itself (<see cref="HostRounds"/> adds <see cref="CliCapabilities.HostedRounds"/>). Checked
    /// against <see cref="CliManifest"/> before an owned launch, and live once the client answers, owned or attached
    /// (<see cref="CliCapabilities.Require(GameActor, string[])"/>).
    /// </summary>
    public string[] Capabilities { get; set; } = [];
    /// <summary>
    /// Owned only: the full path of the <see cref="CliCapabilityManifest"/> of the ValheimCLI core and packs staged in
    /// <see cref="Install"/>. Set, the preflight refuses before launch an install whose ValheimCLI files are not exactly that
    /// set by SHA256, or a set without a capability the run uses; a manifest that is missing or malformed is refused, never
    /// skipped. Left out, no static capability check runs: the live check after the client answers is the only one, and the
    /// report says so (<see cref="CliPreflight"/>). An attached client's files are its operator's, so it takes no manifest.
    /// </summary>
    public string? CliManifest { get; set; }
    /// <summary>
    /// Which capability checks apply, for the report's <c>cliPreflight</c>: <c>static and live</c> for an owned client with a
    /// <see cref="CliManifest"/>, otherwise <c>live only</c> with the reason.
    /// </summary>
    [JsonIgnore] public string CliPreflight => !Owned ? "live only: an attached client's files are its operator's"
        : CliManifest == null ? "live only: the plan names no cliManifest" : "static (cliManifest) and live";
    /// <summary>Whether <see cref="Pinning"/> is <c>strict</c>; refuses any value but <c>strict</c> or <c>none</c>.</summary>
    [JsonIgnore] public bool Pinned => EnvironmentPinning.IsStrict(Pinning, "The client's");

    public bool Owned => Mode == "owned";
    /// <summary><see cref="Architecture"/> as the launch takes it; refuses any value but <c>x64</c>, <c>arm64</c> or none.</summary>
    [JsonIgnore] public ClientArchitecture LaunchArchitecture => Architecture switch
    {
        "" or "x64" => ClientArchitecture.X64,
        "arm64" => ClientArchitecture.Arm64,
        _ => throw new ArgumentException($"Client architecture \"{Architecture}\" is neither x64 nor arm64; leave it out for x64."),
    };

    /// <summary>A full path on a Windows host (drive or UNC) or a POSIX host (rooted), whichever machine this runs on.</summary>
    internal static bool IsFullPathOnAnyHost(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) &&
        (System.Text.RegularExpressions.Regex.IsMatch(path, @"^([A-Za-z]:[\\/]|\\\\)") || path.StartsWith('/'));

    /// <summary>
    /// The rules every client section follows, plus <paramref name="absentPlugins"/>, which must be pinned <c>absent</c>
    /// when the claim is what a client without them sees (a server-only mod). An owned client pins its install
    /// (<see cref="InstallPins"/>). An explicitly unpinned client (<see cref="Pinning"/> <c>none</c>) must leave out every
    /// pin, and <paramref name="absentPlugins"/> are then not checked.
    /// </summary>
    public void Validate(params string[] absentPlugins)
    {
        bool pinned = Pinned;
        if (Mode is not ("owned" or "attach")) throw new ArgumentException("Client mode is owned or attach.");
        CheckTestTeleportOptions();
        // The install is a path on the client's machine, which with an environment profile is not this one (a Windows
        // client driven from macOS): a full path in either style is accepted here; launching checks it where it runs.
        if (Owned && !(Path.IsPathFullyQualified(Install) || IsFullPathOnAnyHost(Install))) throw new ArgumentException("An owned client needs the full path of its install.");
        var architecture = LaunchArchitecture;
        if (!Owned && (Install.Length != 0 || LaunchArguments.Length != 0 || Patchers.Length != 0 || InstallPins != null || Architecture.Length != 0))
            throw new ArgumentException("An attached client is launched by its operator; leave out install, installPins, launch arguments, patchers and architecture.");
        var platform = Owned ? InstallPlatform() : null;
        if (architecture == ClientArchitecture.Arm64 && platform is { } other && other != ClientPlatform.MacOS)
            throw new ArgumentException($"Architecture arm64 is for a macOS client (Valheim.app); this {other} client is x64 only. Leave architecture out.");
        // The launch's own slice and core check on an install on this machine, so validate and run refuse it before a server starts.
        if (platform == ClientPlatform.MacOS)
            try { ClientLaunch.RequireMacArchitecture(Path.GetFullPath(Install), architecture); }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                throw new ArgumentException($"The client install cannot launch as {ClientLaunch.PlanName(architecture)}: {error.Message}", error);
            }
        BepInExLoader.CheckPatcherNames(Patchers);
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1024 or > 65535) throw new ArgumentException("Give the client's ValheimCLI host and port.");
        if (Owned && Host is not ("127.0.0.1" or "localhost")) throw new ArgumentException("An owned client runs on this machine; its ValheimCLI host is 127.0.0.1.");
        if (HostWorld != null && (Join.Length != 0 || PasswordVariable != null || Crossplay))
            throw new ArgumentException("A hosting client (hostWorld) joins no server: leave out join, passwordVariable and crossplay; hostWorld.crossplay hosts a crossplay world.");
        if (HostWorld != null && Host is not ("127.0.0.1" or "localhost"))
            throw new ArgumentException("A hosting client runs on this machine, where the runner places the fixture world in its save directory; its ValheimCLI host is 127.0.0.1.");
        if (Crossplay && (Join.Length != 0 || PasswordVariable != null))
            throw new ArgumentException("A crossplay client joins the server's PlayFab lobby, not an address, and the crossplay join command would carry a password as text: leave out join and passwordVariable, and run the crossplay fixture server private without a password.");
        foreach (string? token in new[] { HostWorld == null && !Crossplay ? Join : null, Character, PasswordVariable })
            if (token != null && (token.Length == 0 || token.Any(char.IsWhiteSpace))) throw new ArgumentException("Join address, character and password variable must be single tokens.");
        if (StartSeconds is < 10 or > 1800 || JoinSeconds is < 10 or > 900 || ArrivalSeconds is < 10 or > 600 || BepInExSeconds is < 5 or > 1800) throw new ArgumentException("Client timeouts are out of range.");
        HostWorld?.Validate(pinned);
        if (Capabilities == null || Capabilities.Any(path => path == null || path.Split('/') is not [{ Length: > 0 }, { Length: > 0 }] || path.Any(char.IsWhiteSpace)) || Capabilities.Distinct(StringComparer.Ordinal).Count() != Capabilities.Length)
            throw new ArgumentException("Name each of the client's capabilities once, as owner/command.");
        if (CliManifest != null && !Owned)
            throw new ArgumentException("An attached client's files are its operator's: leave out cliManifest. Its capabilities are checked live once it answers.");
        if (CliManifest != null && !Path.IsPathFullyQualified(CliManifest))
            throw new ArgumentException("Give the full path of the client's cliManifest.");
        if (!pinned)
        {
            if (Pins.Count != 0 || InstallPins != null)
                throw new ArgumentException("A client with pinning \"none\" lists no pins and no installPins: nothing would check them. Remove them, or remove \"pinning\" to keep strict pins.");
            return;
        }
        if (Pins.ContainsKey("worlduid") || Pins.ContainsKey("world")) throw new ArgumentException("Leave the world out of the client pins; the runner pins the server's world.");
        if (!Pins.TryGetValue("valheimCLI.valheimCLI", out var cli) || cli.Length != 32 || !cli.All(Uri.IsHexDigit)) throw new ArgumentException("Pin the client's exact ValheimCLI MD5.");
        foreach (string plugin in absentPlugins)
            if (!Pins.TryGetValue(plugin, out var value) || value != "absent") throw new ArgumentException($"The client must pin {plugin}=absent: the check is what a client without it sees.");
        foreach (var pin in Pins)
            if (pin.Value != "absent" && (pin.Value.Length != 32 || !pin.Value.All(Uri.IsHexDigit))) throw new ArgumentException($"Client plugin {pin.Key} needs an exact MD5 or absent.");
        _ = MenuExpectations; // Parses the pins before anything launches.
        if (Owned)
            (InstallPins ?? throw new ArgumentException("Pin the owned client's game build, BepInEx core and patchers in installPins (InstallPins.Of computes them), or opt out explicitly with \"pinning\": \"none\"."))
                .Validate("client install");
    }

    // The owned install's platform where the plan shows it: the install on this machine, or a Windows-style path to another
    // machine. A POSIX path that is not here stays unknown until the launch, which refuses arm64 for a Linux client too.
    private ClientPlatform? InstallPlatform()
    {
        if (Directory.Exists(Install))
            try { return ClientLaunch.Detect(Install); }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { return null; }
        return System.Text.RegularExpressions.Regex.IsMatch(Install, @"^([A-Za-z]:[\\/]|\\\\)") ? ClientPlatform.Windows : null;
    }

    /// <summary>Strict pins at the menu: plugins only. <see cref="EnvironmentPinning.None"/> for an unpinned client.</summary>
    public string MenuExpectations => !Pinned ? EnvironmentPinning.None : Expect(Pins.Select(p => p.Key + "=" + p.Value));
    /// <summary>Strict pins once joined: plugins and the server's world. <see cref="EnvironmentPinning.None"/> for an unpinned client.</summary>
    public string WorldExpectations(string worldUid) => !Pinned ? EnvironmentPinning.None : Expect(Pins.Select(p => p.Key + "=" + p.Value).Append("worlduid=" + worldUid));
    /// <summary>
    /// The static preflight of the run, after <see cref="Validate"/> and before anything is copied or started; every fact it
    /// needs is on disk here. A hosting client's fixture must still be its pinned files and must hold the world
    /// <see cref="HostWorldPlan.WorldUid"/> names (<see cref="HostWorldPlan.Preflight"/>). An owned client's install must pass
    /// what its launch checks first: its patchers, <see cref="InstallPins"/> and BepInEx loader (a Doorstop proxy and
    /// configuration from different versions included). Then: every pinned plugin build is installed exactly once in
    /// <c>BepInEx/plugins</c> or <c>BepInEx/scripts</c> (a hash typed by hand or a wrong staged file matches none), a pinned
    /// plugin in <c>scripts</c> has ScriptEngine pinned and set to <c>LoadOnStart</c>, and ValheimCLI's standing expectations
    /// file, when its config sets one, parses one pin per line, names a world when strict, and agrees with the plan's pins and
    /// hosted world. An unpinned client skips the plugin checks, which need pins. With a <see cref="CliManifest"/>, last, the
    /// install's ValheimCLI files must be exactly its set and provide its own requested pack capabilities
    /// (<see cref="CheckCliManifest"/>). A mod adapter's extensions are confirmed after the client loads.
    /// An attached client's install is its operator's and is not read. <see cref="ClientSession.Launch(ClientRunPlan, string, CancellationToken)"/>
    /// runs the install part itself; <see cref="HostRounds"/> runs all of it before it places the fixture.
    /// </summary>
    public void Preflight() => Preflight([]);

    internal void CheckTestTeleportOptions()
    {
        if (FastTestTeleports && (!EventDrivenArrival || !Owned || !Pinned))
            throw new ArgumentException("fastTestTeleports requires eventDrivenArrival and an owned, strictly pinned test client.");
        if (FastTestTeleports && HostWorld?.Local != true)
            throw new ArgumentException("fastTestTeleports requires hostWorld.local: a joined client's cold destination can arrive after fast teleport completion. Use ordinary teleport timing for joined clients.");
    }

    /// <summary>Everything the launch and arrival code needs from the pinned ValheimCLI set.</summary>
    internal IEnumerable<string> RequiredCliCapabilities => Capabilities
        .Concat(EventDrivenArrival ? ["valheim.world/player-support-wait", CliCapabilities.TeleportSignals] : []);

    /// <summary>
    /// <see cref="Preflight()"/>, with <paramref name="capabilities"/> (<c>owner/command</c>) that the runner itself uses
    /// added to <see cref="Capabilities"/> for the manifest check (<see cref="HostRounds"/> passes <see cref="CliCapabilities.HostedRounds"/>).
    /// </summary>
    public void Preflight(IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        CheckTestTeleportOptions();
        var identity = HostWorld?.Preflight();
        if (Owned) CheckOwnedInstall(identity?.Name, capabilities);
    }

    /// <summary>The owned launch's checks on this machine's install, in order, returning the launch they allow.</summary>
    internal System.Diagnostics.ProcessStartInfo CheckOwnedInstall(string? hostWorldName = null, IEnumerable<string>? capabilities = null)
    {
        BepInExLoader.RequirePatchers(Install, Patchers, "client install");
        CheckInstallPins();
        var start = ClientSession.StartInfo(this, ClientLaunch.CurrentHost); // The install's loader and slices.
        var located = OwnedClientPreflight.Check(Install, Pins, Pinned, HostWorld, hostWorldName);
        var manifestCheck = CheckCliManifest(capabilities);
        if (manifestCheck != null)
            OwnedClientPreflight.RequireManifestScriptsLoad(Install, Pins, located, manifestCheck.Files);
        return start;
    }

    /// <summary>
    /// The static capability check alone: an owned client's install against its <see cref="CliManifest"/>
    /// (<see cref="CliCapabilityManifest.Check"/>), requiring only the ValheimCLI-owned entries in
    /// <see cref="Capabilities"/> and <paramref name="capabilities"/>. A mod's own extensions are
    /// checked from the live game after its plugin loads; the CLI pack manifest cannot declare them.
    /// Null, with nothing read, for a plan without a manifest or an attached client, whose capabilities only the live check
    /// sees. Refuses a missing manifest (<see cref="FileNotFoundException"/>), a malformed one (<see cref="InvalidDataException"/>)
    /// and an install that is not its set or lacks a capability (<see cref="InvalidOperationException"/>).
    /// </summary>
    public CliManifestCheck? CheckCliManifest(IEnumerable<string>? capabilities = null)
    {
        if (!Owned || CliManifest == null) return null;
        var manifest = CliCapabilityManifest.Read(CliManifest);
        return manifest.Check(Install, RequiredCliCapabilities.Concat(capabilities ?? []).Where(CliCapabilities.IsPackCapability));
    }

    /// <summary>Owned and pinned: refuses an install whose game build, BepInEx core or patchers are not <see cref="InstallPins"/>.</summary>
    public void CheckInstallPins()
    {
        if (!Owned || !Pinned) return;
        (InstallPins ?? throw new ArgumentException("Pin the owned client's game build, BepInEx core and patchers in installPins, or opt out explicitly with \"pinning\": \"none\"."))
            .Check(Install, "client install");
    }
    private static string Expect(IEnumerable<string> lines)
    {
        var errors = new List<string>();
        var parsed = Expectations.ParseLines(lines, errors);
        if (errors.Count != 0) throw new ArgumentException("Invalid client pins: " + string.Join("; ", errors));
        return Expectations.ExpectCommand(parsed, strict: true);
    }
}

/// <summary>A file a plan depends on, pinned by path and SHA256.</summary>
public sealed class PinnedFile
{
    public string Source { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public void Validate(string what)
    {
        if (!Path.IsPathFullyQualified(Source) || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new ArgumentException($"Pin the {what} by full path and SHA256.");
    }
    /// <summary>The file's path after checking that its hash still matches.</summary>
    public string Verified()
    {
        if (!string.Equals(WorldFixture.Hash(Source), Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{Source} changed after it was pinned.");
        return Source;
    }
}
