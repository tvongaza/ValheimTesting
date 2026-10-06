using System.Text.Json;
using System.Globalization;
using MyMod.IntegrationTests;
using MyMod.SystemTests;
using Valheim.Testing.NativeAcceptance;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;
using Valheim.Testing.GameSessions.Fakes;

namespace Valheim.Testing.NativeAcceptance.Tests;

/// <summary>
/// The hosted scenario on a session whose host is scripted (<see cref="FakeGameSession.Hosted"/>): one client that hosts the
/// fixture world (placed in a temporary data directory), is its server and its client, runs MyMod's feature, and logs MyMod's
/// greeting handler as a host would. Nothing here starts Valheim.
/// </summary>
public sealed class HostedScenarioTests : IDisposable
{
    private const string WorldUid = "4242", Name = "HostFixture";
    private readonly string _root = Directory.CreateTempSubdirectory("mymod-hosted-").FullName;
    private string Fixture => Path.Combine(_root, "fixture");
    private string Save => Path.Combine(_root, "client-data");
    private string Output => Path.Combine(_root, "out");
    private string HostLog => Path.Combine(_root, "host-LogOutput.log");
    private readonly List<(float X, float Z)> _markers = [], _saved = [];
    private bool _hosting, _handlerLogs = true, _loseMarkers;
    private string _greeting = "hello";
    private int _readings, _saveNumber = 5, _greetings;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    public HostedScenarioTests()
    {
        Directory.CreateDirectory(Fixture); Directory.CreateDirectory(Output); Directory.CreateDirectory(Path.Combine(Save, "worlds_local"));
        File.WriteAllBytes(Path.Combine(Fixture, Name + ".fwl"), Metadata(Name, long.Parse(WorldUid, CultureInfo.InvariantCulture)));
        File.WriteAllText(Path.Combine(Fixture, Name + ".db"), "fixture world");
        File.WriteAllText(HostLog, "[Info   :   BepInEx] Chainloader startup complete\n");
    }

    // The fixture's world metadata as the game writes it (a length-prefixed package: version, name, seed name, seed, UID, ...),
    // which the run's preflight reads to check that the fixture holds the planned world UID.
    private static byte[] Metadata(string name, long uid)
    {
        using var package = new MemoryStream();
        using (var writer = new BinaryWriter(package, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(41); writer.Write(name); writer.Write("AbCdEf1234"); writer.Write(1234); writer.Write(uid); writer.Write(2);
        }
        return [.. BitConverter.GetBytes((int)package.Length), .. package.ToArray()];
    }

    private HostedPlan Plan() => new()
    {
        Scenario = HostedPlan.HostedScenarioName, DrySite = CampaignWorld.Dry, WetSite = CampaignWorld.Wet, NewGreeting = "goodbye",
        Client = new()
        {
            Mode = "attach", Port = 5556, Character = "Tester", JoinSeconds = 2,
            Pins = new() { ["valheimCLI.valheimCLI"] = CampaignWorld.Md5Cli, [AcceptancePlan.ModPlugin] = CampaignWorld.Md5Mod, [AcceptancePlan.AdapterPlugin] = CampaignWorld.Md5Adapter },
            HostWorld = new() { World = new() { Source = Fixture, Sha256 = new(WorldFixture.Manifest(Fixture)) }, WorldUid = WorldUid, SaveDirectory = Save },
        },
    };

    // The host: at its menu until it starts the world, then the world (one loading reading, then ready with its player).
    private ScriptedTransport Host()
    {
        bool acknowledged = false;
        bool devcommands = false;
        return new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .On("cli_access", _ => ScriptedTransport.Ok("ACCESS " + JsonSerializer.Serialize(new { schemaVersion = 1, complete = true,
                devcommands, cheatsAcknowledged = acknowledged, allowOnServerClients = true, server = _hosting, dedicated = false,
                joinedClient = _hosting && !_hosting, localPlayer = _hosting, profileAvailable = _hosting })))
            .On("cli_acknowledge_local_cheats", _ => { acknowledged = true; return ScriptedTransport.Ok("OK: localCharacterCheated=True"); })
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester, Local)"))
            .OnPrefix("cli_start_host_world ", command =>
            {
                _hosting = true; _readings = 0;
                return ScriptedTransport.Ok($"OK: Starting hosted world '{command.Split(' ')[1]}' using character 'Tester' (tester, Local); open=true, public=False, crossplay=False, backend=Steamworks, passwordSet=False");
            })
            .Extension("valheim.session", "state", _ =>
            {
                bool present = _hosting && ++_readings > 1;
                return new
                {
                    source = "session-state", complete = true, phase = present ? "world-present" : _hosting ? "loading" : "menu", worldUid = present ? WorldUid : null,
                    worldPresent = present, worldReady = present, server = present, dedicated = false, localPlayer = present, playerReady = present,
                    saving = false, loadError = false, connectionStatus = present ? "Connected" : "None",
                };
            })
            .Extension("valheim.session", "save", _ =>
            {
                _saved.Clear(); _saved.AddRange(_markers);
                return new { source = "session-save", complete = true, worldUid = WorldUid, saved = true, before = _saveNumber, after = ++_saveNumber, milliseconds = 10 };
            }, readOnly: false)
            .Extension("valheim.session", "leave", _ =>
            {
                _hosting = false;
                _markers.Clear(); if (!_loseMarkers) _markers.AddRange(_saved); // Hosting again loads what was saved.
                return new { source = "session-leave", complete = true, action = "leave" };
            }, readOnly: false)
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"))
            .Extension("mymod.testing", "harmony", _ => TestWorld.ModCensus())
            .Extension("mymod.testing", "config", args => new
            {
                source = "bepinex-config", complete = true, guid = Uri.UnescapeDataString(args[0]), section = "Server", key = "Greeting", server = true,
                installed = true, found = true, type = "System.String", value = _greeting, defaultValue = "hello",
            })
            .OnPrefix("mymod_mark ", command =>
            {
                var w = command.Split(' ');
                float x = float.Parse(w[1], CultureInfo.InvariantCulture), z = float.Parse(w[2], CultureInfo.InvariantCulture);
                if (x < 200) { _markers.Add((x, z)); return ScriptedTransport.Ok($"OK: marked {x} {z} ground=42.5"); }
                return ScriptedTransport.Ok($"REFUSED: {x} {z} ground=22.0 below 31.5");
            })
            .OnPrefix("mymod_greeting ", command =>
            {
                _greetings++;
                _greeting = command.Split(' ')[1];
                // A broadcast runs its handler on the host itself, which logs it as the host.
                if (_handlerLogs) File.AppendAllText(HostLog, $"[Info   :MyMod (ValheimTesting example)] Greeting \"{_greeting}\" received from 77 (host)\n");
                return ScriptedTransport.Ok("OK: greeting " + _greeting);
            })
            .OnPrefix("cli_zdos_at ", command =>
            {
                var w = command.Split(' ');
                float x = float.Parse(w[1], CultureInfo.InvariantCulture), z = float.Parse(w[2], CultureInfo.InvariantCulture);
                var lines = _markers.Where(m => MathF.Abs(m.X - x) <= 8 && MathF.Abs(m.Z - z) <= 8).Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"ZDO {DrySiteScenario.Marker} id=1:2 pos={m.X:F3},42.250,{m.Z:F3} rot=0.00,0.00,0.00 quat=0,0,0,1 scale=- persistent=True owner=0")).ToList();
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"OK: ZDOS_AT {x:F1},{z:F1} r=8.0 zones=1 objects={lines.Count}"));
                return ScriptedTransport.Ok([.. lines]);
            });
    }

    private async Task<ScenarioReport> Run(HostedPlan plan, string? hostLog)
    {
        var report = new ScenarioReport("mymod-hosted-test");
        var host = Host();
        // The scripted host keeps its data in the temporary folder, as a real client keeps it in its user's own directory.
        using var data = new FakeClientDataDirectory(Save);
        // The run journals its copies of the fixture here, never in this machine's own ValheimTesting folder, which
        // valheim-test env status reads and a killed test would leave a run in.
        using var machine = new FakeDataRoot(Path.Combine(_root, "valheim-testing"));
        // As the runner's host mode builds it: MyMod's declaration, so its Harmony patches are checked on the host.
        await using var session = FakeGameSession.Hosted(report, Output, plan.Client, (client, _, output) => ClientSession.Attach(client, output, host),
            hostLog: hostLog, mod: AcceptancePlan.Mod);
        try
        {
            await session.StartAsync();
            await HostedScenario.Run(session, plan);
        }
        catch (Exception) { Assert.False(report.Passed); }
        await session.DisposeAsync(); // The teardown's steps, before the report is read.
        return report;
    }
    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();

    [Fact] public async Task TheModsFeatureRunsOnAHostAndItsBroadcastHandlerFiresThere()
    {
        var report = await Run(Plan(), HostLog);
        Assert.True(report.Passed, string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error)));
        Assert.Equal(new[]
        {
            "preflight the fixture world, before it is copied", "preflight the native client's hosted-world save directory",
            "place the disposable fixture world in the client's local worlds", "hosting client host at its menu, plugins pinned",
            "hosting client host's ValheimCLI offers the hosted-world session commands", "host: the mod's Harmony patches are applied",
            "host the fixture world with the disposable character, protected",
            "no marker at either site before the mod acts", "the mod marks the dry site", "the mod refuses the wet site",
            "host: one marker at the dry site, none at the wet site",
            "the mod's greeting broadcast runs its handler on the host, which is server and client at once",
            "confirmed world save", "restart the hosted world, protected", "host: the marker is still at the dry site after the restart, none at the wet site",
            "the host leaves its world to its menu", "detach from the operator's hosting client host",
            "move the hosted world from the client's local worlds into the evidence",
        }, report.Steps.Select(s => s.Name));
        Assert.Contains("(host)", report.Provenance["hostBroadcastLine"]);
        Assert.Equal(2, _greetings); // Changed once for the check and set back once.
        Assert.Equal("hello", _greeting);
        Assert.Equal(Name, report.Provenance["hostWorld"]);
        Assert.True(File.Exists(Path.Combine(report.Provenance["hostWorldEvidence"], Name + ".fwl")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Save, "worlds_local")));
    }

    [Fact] public async Task ABroadcastWhoseHandlerNeverRunsOnTheHostFailsAndTheGreetingIsSetBack()
    {
        _handlerLogs = false;
        var report = await Run(Plan(), HostLog);
        Assert.Equal(new[] { "the mod's greeting broadcast runs its handler on the host, which is server and client at once" }, Failed(report));
        Assert.Equal("hello", _greeting);
        Assert.True(report.Steps.Single(s => s.Name == "detach from the operator's hosting client host").Passed);
        // A failed run leaves the operator's client as it is: no leave, and its world stays in place, named.
        Assert.DoesNotContain(report.Steps, s => s.Name == "the host leaves its world to its menu");
        Assert.Contains(Name, report.Provenance["hostWorldLeftInPlace"]);
    }

    [Fact] public async Task AMarkerTheHostDoesNotKeepFailsAfterTheRestart()
    {
        _loseMarkers = true;
        var report = await Run(Plan(), hostLog: null); // An attached host: its log is its operator's, so the broadcast is not observed.
        Assert.Equal(new[] { "host: the marker is still at the dry site after the restart, none at the wet site" }, Failed(report));
        Assert.StartsWith("not observed", report.Provenance["hostBroadcast"]);
        Assert.Equal(0, _greetings);
    }
    // validate-host runs the run's own preflight: a plan whose fixture holds another world UID is refused there, naming the
    // UID the fixture holds, and nothing is copied into the client's worlds.
    [Fact] public void ValidateHostRunsTheRunsPreflightAndRefusesAnotherWorldUid()
    {
        string Validate(string worldUid, string output)
        {
            var plan = Plan();
            var pins = new System.Text.Json.Nodes.JsonObject();
            foreach (var pin in plan.Client.Pins) pins[pin.Key] = pin.Value;
            var sha256 = new System.Text.Json.Nodes.JsonObject();
            foreach (var file in plan.Client.HostWorld!.World.Sha256) sha256[file.Key] = file.Value;
            System.Text.Json.Nodes.JsonObject Point(Site site) => new() { ["x"] = site.X, ["z"] = site.Z, ["ground"] = site.Ground };
            var json = new System.Text.Json.Nodes.JsonObject
            {
                ["scenario"] = plan.Scenario, ["newGreeting"] = plan.NewGreeting, ["drySite"] = Point(plan.DrySite), ["wetSite"] = Point(plan.WetSite),
                ["client"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["mode"] = plan.Client.Mode, ["port"] = plan.Client.Port, ["character"] = plan.Client.Character, ["pins"] = pins,
                    ["hostWorld"] = new System.Text.Json.Nodes.JsonObject { ["world"] = new System.Text.Json.Nodes.JsonObject { ["source"] = Fixture, ["sha256"] = sha256 }, ["worldUid"] = worldUid },
                },
            };
            string path = Path.Combine(_root, "hosted-" + worldUid + ".json");
            File.WriteAllText(path, json.ToJsonString());
            Assert.Equal(worldUid == WorldUid ? 0 : 1, PinnedServerRun.MainAsync([PinnedServerRun.ValidateHostMode, path, output], HostedScenario.RunnerOptions()).GetAwaiter().GetResult());
            return File.ReadAllText(Path.Combine(output, "result.json"));
        }
        Assert.Contains("preflight the fixture world, before it is copied", Validate(WorldUid, Path.Combine(_root, "validate-right")));
        string wrong = Validate("4243", Path.Combine(_root, "validate-wrong"));
        Assert.Contains("hostWorld.worldUid is 4243", wrong); Assert.Contains("with UID 4242", wrong);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Save, "worlds_local")));
    }
}
