using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

// The native campaign's plan fields and rules: five more scenarios on the owned dedicated server, each proving a few of the
// toolkit's native acceptance items, and the controls that must make their checks fail. Each field is refused in a plan
// whose scenario does not read it, so a plan never looks like it tests something it does not.
public sealed partial class LifecyclePlan
{
    /// <summary>The dry-site rounds with a zone cycle, global keys, a dungeon's rooms and a logout (<see cref="LifecycleWorldScenario"/>).</summary>
    public const string WorldScenario = "lifecycle-world";
    /// <summary>A client without MyMod near its objects (<see cref="VanillaClientScenario"/>).</summary>
    public const string VanillaClientScenario = "vanilla-client";
    /// <summary>MyMod's synced config entry on a client that has MyMod (<see cref="SyncedConfigScenario"/>).</summary>
    public const string SyncedConfigScenario = "synced-config";
    /// <summary>A mismatched MyMod build refused, then a matching client joins (<see cref="RefusedJoinScenario"/>).</summary>
    public const string RefusedJoinScenario = "refused-join";
    /// <summary>The dry-site lifecycle on a crossplay (PlayFab) server, joined through its lobby.</summary>
    public const string CrossplayScenario = "crossplay";
    public static readonly string[] Scenarios = [LifecycleScenario, ServerScenario, WorldScenario, VanillaClientScenario, SyncedConfigScenario, RefusedJoinScenario, CrossplayScenario];
    /// <summary>The adapter's fixture commands (the global-key change) run only when the server starts with this set to 1.</summary>
    public const string FixturesVariable = "MYMOD_TEST_FIXTURES";
    private static readonly Regex Word = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyName = new("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>lifecycle-world: declared dry ground far enough away that the client unloads the dry site's zones.</summary>
    public Site? Away { get; set; }
    /// <summary>lifecycle-world: a global key the fixture world does not have yet, set on the server (a boss key: <c>defeated_eikthyr</c>).</summary>
    public string? GlobalKey { get; set; }
    /// <summary>lifecycle-world: a vanilla dungeon location's ground position near the arrival point.</summary>
    public DungeonSite? Dungeon { get; set; }
    /// <summary>lifecycle-world: where the client's disposable local character is saved.</summary>
    public LogoutSettings? Logout { get; set; }
    /// <summary>synced-config: the word the server's admin changes the greeting to; it must differ from the server's current one.</summary>
    public string? NewGreeting { get; set; }
    /// <summary>refused-join: the client with a mismatched MyMod build, run before <see cref="Client"/>.</summary>
    public ClientRunPlan? RefusedClient { get; set; }
    /// <summary>refused-join: the status the refused client must end with, by the game's name; default <c>ErrorVersion</c> (3).</summary>
    public string? ExpectedRefusal { get; set; }
    /// <summary>
    /// A control run: the control plugin installed for this run (see <see cref="ControlPlugins"/>), whose check must fail
    /// for its named reason. The run passes only then. A control plugin in the pins without this is refused.
    /// </summary>
    public string? ExpectFailure { get; set; }

    [JsonIgnore] public bool MarksSites => Scenario is LifecycleScenario or ServerScenario or WorldScenario or VanillaClientScenario or CrossplayScenario;
    [JsonIgnore] public bool IsCampaign => Scenario is WorldScenario or VanillaClientScenario or SyncedConfigScenario or RefusedJoinScenario or CrossplayScenario;
    [JsonIgnore] public ControlPlugin? Control => ControlPlugins.Named(ExpectFailure);
    [JsonIgnore] public GameConnectionStatus RefusalStatus => ExpectedRefusal == null ? GameConnectionStatus.ErrorVersion : ConnectionStatusReading.ParseStatus(ExpectedRefusal);

    private void ValidateCampaign()
    {
        OnlyForScenario("away, globalKey, dungeon and logout", Away != null || GlobalKey != null || Dungeon != null || Logout != null, WorldScenario);
        OnlyForScenario("newGreeting", NewGreeting != null, SyncedConfigScenario);
        OnlyForScenario("refusedClient and expectedRefusal", RefusedClient != null || ExpectedRefusal != null, RefusedJoinScenario);
        OnlyForScenario("crossplay", Crossplay, CrossplayScenario);
        OnlyForScenario("patchReload", PatchReload != null, ServerScenario);
        PatchReload?.Validate(this);
        CheckControls();
        if (!IsCampaign) return;
        if (Review.Enabled) throw new ArgumentException($"review is for the {LifecycleScenario} scenario; remove it from this plan.");
        if (!MarksSites && (float.IsFinite(DrySite.Ground) || float.IsFinite(WetSite.Ground) || float.IsFinite(Arrival.Ground)))
            throw new ArgumentException($"The {Scenario} scenario marks nothing: remove drySite, wetSite and arrival.");
        var client = Client ?? throw new ArgumentException($"The {Scenario} scenario looks from a client: add the client section.");
        if (client.HostWorld != null) throw new ArgumentException("A hosting client runs with the host mode and a hosted plan, not on the owned server.");
        switch (Scenario)
        {
            case WorldScenario:
                RequireEnvironmentFlag(FixturesVariable, "let the test adapter's globalkey command set the fixture world's global keys");
                SameBuildsAs(client, "client");
                Arrival.Validate("arrival point", requireGround: true);
                CheckAway();
                if (GlobalKey == null || !KeyName.IsMatch(GlobalKey))
                    throw new ArgumentException("Set globalKey to one lower-case key the fixture world does not have yet, for example defeated_eikthyr.");
                (Dungeon ?? throw new ArgumentException("Add dungeon: the ground position of a vanilla dungeon location near the arrival point.")).Validate(Arrival);
                (Logout ?? throw new ArgumentException("Add logout with the client character's characters_local folder.")).Validate();
                break;
            case VanillaClientScenario:
                client.Validate(ModPlugin); // The claim is what a client without MyMod sees; it still needs the adapter.
                RequirePin(client, AdapterPlugin, Pins[AdapterPlugin], "client", "the vanilla client reads its census through the server's adapter build");
                break;
            case SyncedConfigScenario:
                SameBuildsAs(client, "client");
                if (NewGreeting == null || !Word.IsMatch(NewGreeting)) throw new ArgumentException("Set newGreeting to one word (letters, digits, - or _), the value the server's admin changes the greeting to.");
                break;
            case RefusedJoinScenario:
                CheckRefusedJoin(client);
                break;
            case CrossplayScenario:
                if (!Crossplay || !client.Crossplay) throw new ArgumentException("The crossplay scenario needs \"crossplay\": true in the plan and in its client section.");
                if (Arguments.Any(argument => argument.Equals("-password", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("A crossplay fixture server runs private (-public 0) without -password: the crossplay join cannot send one.");
                client.Validate(ModPlugin);
                break;
        }
    }

    // The client runs the server's MyMod and adapter builds, pinned by the same MD5s.
    private void SameBuildsAs(ClientRunPlan client, string what)
    {
        client.Validate();
        RequirePin(client, ModPlugin, Pins[ModPlugin], what, "the check needs the server's MyMod on the client");
        RequirePin(client, AdapterPlugin, Pins[AdapterPlugin], what, "the client's observations come from the adapter");
    }

    private static void RequirePin(ClientRunPlan client, string plugin, string md5, string what, string why)
    {
        if (!client.Pins.TryGetValue(plugin, out var pinned) || !string.Equals(pinned, md5, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Pin {plugin} in the {what} section with the server's MD5 ({md5}): {why}.");
    }

    private void CheckAway()
    {
        var away = Away ?? throw new ArgumentException("Add away: declared dry ground far from the dry site, where the player goes so the client unloads the site.");
        away.Validate("away point", requireGround: true);
        if (away.Ground < WaterLevel + Clearance) throw new ArgumentException("The away point must be dry ground; a swimming player is not supported.");
        // At the game's default simulation distance (near 2, far 2) nothing of a zone unloads closer. A client with a larger
        // distance needs more, which the run reads from the client and checks before moving anyone.
        int needed = ZoneCycle.RingsToLeave(new SimulationRange(2, 2, Classic: true));
        var at = ZoneId.Of(away.X, away.Z);
        if (MarkerZones(DrySite).Any(zone => zone.Rings(at) < needed))
            throw new ArgumentException($"Put the away point at least {needed} zones ({needed * (int)ZoneId.Size} m in x or z) from the dry site's zones, or the client keeps them loaded.");
    }

    private void CheckRefusedJoin(ClientRunPlan client)
    {
        var refused = RefusedClient ?? throw new ArgumentException("Add refusedClient: the client with a mismatched MyMod build.");
        SameBuildsAs(client, "client");
        refused.Validate();
        if (refused.HostWorld != null || refused.Crossplay || client.Crossplay) throw new ArgumentException("Both clients of the refused-join scenario join the server by address.");
        if (!refused.Pins.TryGetValue(ModPlugin, out var mismatched) || mismatched == "absent")
            throw new ArgumentException($"The refused client runs another MyMod build: pin {ModPlugin} there by that build's MD5.");
        if (string.Equals(mismatched, Pins[ModPlugin], StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The refused client pins the server's own MyMod build; build it with another net version (-p:MyModNetVersion=2) and pin that build.");
        if (refused.Owned && client.Owned && string.Equals(Path.GetFullPath(refused.Install), Path.GetFullPath(client.Install), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The two clients need their own installs: one install holds one MyMod build.");
        if ((!refused.Owned || !client.Owned) && refused.Port == client.Port)
            throw new ArgumentException("An attached client runs while the other is used: give the two clients different ValheimCLI ports.");
        GameConnectionStatus expected;
        try { expected = RefusalStatus; }
        catch (InvalidOperationException error) { throw new ArgumentException("expectedRefusal: " + error.Message, error); }
        if (!ConnectionStatusReading.IsRefusal(expected)) throw new ArgumentException("expectedRefusal names one of the game's Error statuses, for example ErrorVersion.");
    }

    private void CheckControls()
    {
        static bool Installed(Dictionary<string, string>? pins, string guid) => pins != null && pins.TryGetValue(guid, out var value) && value != "absent";
        var installed = ControlPlugins.All.Where(c => Installed(Pins, c.Guid) || Installed(Client?.Pins, c.Guid) || Installed(RefusedClient?.Pins, c.Guid)).ToList();
        if (ExpectFailure == null)
        {
            if (installed.Count != 0)
                throw new ArgumentException($"The control plugin {installed[0].Guid} is pinned: a control makes its check fail on purpose. Name it in expectFailure (\"{installed[0].Name}\") for a control run, or remove it.");
            return;
        }
        var control = Control ?? throw new ArgumentException($"expectFailure names no control; use one of {string.Join(", ", ControlPlugins.All.Select(c => c.Name))}.");
        if (Scenario != control.Scenario) throw new ArgumentException($"The {control.Name} control belongs to the {control.Scenario} scenario, not {Scenario}.");
        var side = control.OnServer ? Pins : Client?.Pins;
        if (side == null || !side.TryGetValue(control.Guid, out var md5) || md5.Length != 32 || !md5.All(Uri.IsHexDigit))
            throw new ArgumentException($"A {control.Name} run installs that control on the {(control.OnServer ? "server" : "client")}: pin {control.Guid} there by its MD5.");
        if (installed.Count != 1) throw new ArgumentException("A control run installs exactly the control it names; remove the others.");
        if (!control.OnServer && Installed(Pins, control.Guid)) throw new ArgumentException($"The {control.Name} control belongs on the client only.");
        // The control writes the pattern its check requires: the teardown scan must not fail the run on that very line.
        if (control.Name == ControlPlugins.ServerOnlyPrefab && LogScan.TryGetValue("missing-prefab-hash", out var missing) && missing.Severity == LogSeverity.Failure)
            throw new ArgumentException("A server-only-prefab run expects missing-prefab-hash in the client's log: do not classify it as a Failure there.");
        // Its lines fail the scan by default (BepInEx's lookup warning; PatchAll's error in Unity's log), which its own check
        // requires; the teardown scan must count them apart, by name, while every other such line still fails.
        if (control.Name == ControlPlugins.MissingHarmonyTarget)
            foreach (var (pattern, text) in new[] { ("accesstools-not-found", ControlPlugins.MissingMethodName), ("harmony-undefined-target", ControlPlugins.MissingPatchClass) })
            {
                if (!LogScan.TryGetValue(pattern, out var named) || !named.Expected.Contains(text))
                    throw new ArgumentException("A missing-harmony-target run names the control's lines as expected, so the teardown scan counts them apart instead of failing on them again: " +
                        $"\"logScan\": {{ \"accesstools-not-found\": {{ \"expected\": [\"{ControlPlugins.MissingMethodName}\"], \"reason\": \"...\" }}, " +
                        $"\"harmony-undefined-target\": {{ \"expected\": [\"{ControlPlugins.MissingPatchClass}\"], \"reason\": \"...\" }} }}.");
                if (named.Severity == LogSeverity.Warning)
                    throw new ArgumentException($"A missing-harmony-target run keeps {pattern} a failure: name the control's line as expected instead of making every such line a warning.");
            }
    }

    /// <summary>The zones round a site that its marker can stand in.</summary>
    public static IReadOnlyList<ZoneId> MarkerZones(Site site) => ZoneId.Around(site.X, site.Z, DrySiteScenario.MarkerRadius);
}

/// <summary>A vanilla dungeon location's ground position and how far round it the server looks for its generator.</summary>
public sealed class DungeonSite
{
    public float X { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; } = 64;
    public void Validate(Site arrival)
    {
        if (!float.IsFinite(X) || !float.IsFinite(Z) || MathF.Abs(X) > 10000 || MathF.Abs(Z) > 10000) throw new ArgumentException("The dungeon needs finite coordinates inside the world.");
        if (!(Radius > 0 && Radius <= 256)) throw new ArgumentException("dungeon.radius is more than 0 and at most 256 m.");
        // The server generates a zone, and so the dungeon's rooms, once a player is near: two zones from where the player stands.
        if (ZoneId.Of(X, Z).Rings(ZoneId.Of(arrival.X, arrival.Z)) > 2)
            throw new ArgumentException("Choose a dungeon within two zones (128 m in x or z) of the arrival point, so the server generates it while the player stands there.");
    }
}

/// <summary>The client character's save folder for the logout check, and how long the save may take to appear.</summary>
public sealed class LogoutSettings
{
    /// <summary>The client's <c>characters_local</c> folder, as this runner reads it; never a cloud <c>characters</c> folder.</summary>
    public string CharactersDirectory { get; set; } = "";
    public int WriteSeconds { get; set; } = 30;
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(CharactersDirectory) || !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(CharactersDirectory)), "characters_local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("logout.charactersDirectory is the full path of the client's characters_local folder (local characters only, never cloud ones).");
        if (WriteSeconds is < 1 or > 600) throw new ArgumentException("logout.writeSeconds is 1 to 600.");
    }
}
