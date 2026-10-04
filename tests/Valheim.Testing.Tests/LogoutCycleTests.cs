using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// LogoutCycle against a scripted client whose leave saves the character as the game does (the new file renamed over the
// old, the previous one kept as .fch.old) and whose join reads the custom data back from that save. Negative controls: a
// logout that writes nothing (a mod broke the save) and a key the save leaves out (a mod keeps it only in memory). No game.
public sealed class LogoutCycleTests : IDisposable
{
    private const string WorldUid = "4242";
    private readonly string _root = Directory.CreateTempSubdirectory("logout-cycle-").FullName;
    private string Characters => Path.Combine(_root, "characters_local");
    private string ProfileFile => Path.Combine(Characters, "tester.fch");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Client
    {
        public bool Joined = true, Saves = true, WriteLate;
        public string FileSource = "Local", Character = "Tester";
        public readonly HashSet<string> KeptInMemoryOnly = [];
        public Dictionary<string, string> Live = new() { ["mymod.home"] = "12,40,-8", ["mymod.level"] = "3", ["other.mod"] = "x" };
        private Dictionary<string, string> _saved = new();
        private int _writes;
        public Task? LateWrite;
        /// <summary>What the character's custom data is when it spawns, from what the save holds.</summary>
        public Func<Dictionary<string, string>, Dictionary<string, string>> OnLoad = saved => new(saved);

        public ScriptedTransport Transport(string profile)
        {
            return new ScriptedTransport()
                .ClientAccess(() => Joined)
                .Extension("valheim.session", "join", _ => { Joined = true; Live = OnLoad(_saved); return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
                .Extension("valheim.session", "leave", _ =>
                {
                    // The game saves inside the logout, before the menu: the leave's reply comes after the write.
                    if (Saves)
                    {
                        _saved = Live.Where(e => !KeptInMemoryOnly.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value);
                        if (WriteLate) LateWrite = Task.Delay(1000).ContinueWith(_ => Save(profile));
                        else Save(profile);
                    }
                    Joined = false;
                    return new { source = "session-leave", complete = true, action = "leave" };
                }, readOnly: false)
                .Extension("valheim.session", "state", _ => new
                {
                    source = "session-state", complete = true, phase = Joined ? "world-present" : "menu", worldUid = Joined ? WorldUid : null, worldPresent = Joined,
                    worldReady = Joined, server = false, dedicated = false, localPlayer = Joined, playerReady = Joined, saving = false, loadError = false,
                    connectionStatus = Joined ? "Connected" : "None",
                })
                .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"))
                .Extension("mymod.testing", "custom-data", args => !Joined ? new { source = "local-player-custom-data", complete = false } : (object)new
                {
                    source = "local-player-custom-data", complete = true, prefix = args.Count == 1 ? args[0] : null, character = Character, profileFile = Character.ToLowerInvariant(),
                    fileSource = FileSource, profilePath = profile,
                    entries = Live.Where(e => args.Count == 0 || e.Key.StartsWith(args[0], StringComparison.Ordinal)).OrderBy(e => e.Key, StringComparer.Ordinal)
                        .Select(e => new { key = e.Key, value = e.Value }).ToArray(),
                });
        }

        // As the game does: a .new file, the previous file moved to .old, the new one renamed over it.
        private void Save(string profile)
        {
            File.WriteAllText(profile + ".new", "profile save " + ++_writes + " " + string.Join(";", _saved.Select(e => e.Key + "=" + e.Value)));
            File.Move(profile, profile + ".old", overwrite: true);
            File.Move(profile + ".new", profile);
        }
    }

    public LogoutCycleTests()
    {
        Directory.CreateDirectory(Characters);
        File.WriteAllText(ProfileFile, "profile as the last session left it");
    }

    private static ClientRunPlan Plan() => new()
    {
        Mode = "attach", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
        Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), ["my.mod"] = "absent" },
    };

    private LogoutCycle Cycle(TimeSpan? writeTimeout = null, string? characters = null, params string[] keys) => new()
    {
        Capability = "mymod.testing/custom-data", Keys = keys.Length == 0 ? ["mymod.home", "mymod.level"] : keys, KeyPrefix = "mymod.",
        CharactersDirectory = characters ?? Characters, WriteTimeout = writeTimeout ?? TimeSpan.FromSeconds(5), RereadInterval = TimeSpan.FromMilliseconds(100),
    };

    private (Client Fake, ScriptedTransport Transport, GameActor Actor) Joined()
    {
        var fake = new Client();
        var transport = fake.Transport(ProfileFile);
        return (fake, transport, transport.Actor("client", "cli_expect worlduid=" + WorldUid));
    }

    [Fact] public void TheLogoutRewritesTheProfileAndTheCustomDataComesBack()
    {
        var (_, transport, client) = Joined();
        string before = ProfileFileState.Read(ProfileFile).Sha256!;

        var result = Cycle().Run(client, Plan(), WorldUid);

        Assert.Equal(before, result.FileBefore.Sha256);
        Assert.NotEqual(before, result.FileAfter.Sha256);
        Assert.True(result.OldMatchesBefore); // One save replaced the file the check hashed.
        Assert.Equal("3", result.After.Values["mymod.level"]);
        Assert.DoesNotContain("other.mod", result.After.Values.Keys); // Only the prefix was read.
        Assert.Equal(1, transport.Count("cli_extension valheim.session/leave"));
        Assert.Equal(1, transport.Count("cli_extension valheim.session/join"));
        Assert.Equal(1, transport.Count("cli_set_player_safety true")); // Joined again protected.
        var leave = transport.Commands.ToList().IndexOf("cli_extension valheim.session/leave");
        Assert.Contains(transport.Commands.Skip(leave), c => c.StartsWith("cli_expect", StringComparison.Ordinal) && !c.Contains("worlduid", StringComparison.Ordinal)); // Menu pins after the leave.
    }

    // Negative control: a mod broke the save at logout (another mod's UnpatchAll, a blocked save). The hash never changes.
    [Fact] public void AProfileWhoseHashDoesNotChangeFailsTheLogoutCheck()
    {
        var (fake, transport, client) = Joined();
        fake.Saves = false;
        var error = Assert.Throws<WaitTimeoutException>(() => Cycle(writeTimeout: TimeSpan.FromMilliseconds(500)).Run(client, Plan(), WorldUid));
        Assert.Contains("the character file tester.fch to be rewritten by the logout", error.Message);
        Assert.Contains("did not save the character on logout", error.Message);
        Assert.Equal(0, transport.Count("cli_extension valheim.session/join")); // Nothing joins after a failed logout check.
    }

    // Negative control: the mod keeps a value only in memory, so the save leaves it out and the next login lacks it.
    [Fact] public void CustomDataThatIsNotSavedFailsTheReobservation()
    {
        var (fake, _, client) = Joined();
        fake.KeptInMemoryOnly.Add("mymod.level");
        var error = Assert.Throws<InvalidOperationException>(() => Cycle().Run(client, Plan(), WorldUid));
        Assert.Contains("1 of 2 custom data key(s) did not come back", error.Message);
        Assert.Contains("mymod.level is gone (was \"3\")", error.Message);
    }

    [Fact] public void AChangedValueIsReportedWithBothValues()
    {
        var (fake, _, client) = Joined();
        // A mod that resets its value when the character loads.
        fake.OnLoad = saved => new(saved) { ["mymod.level"] = "0" };
        var error = Assert.Throws<InvalidOperationException>(() => Cycle().Run(client, Plan(), WorldUid));
        Assert.Contains("mymod.level is \"0\", was \"3\"", error.Message);
    }

    [Fact] public async Task AWriteTheRunnerSeesLateIsWaitedFor()
    {
        var (fake, _, client) = Joined();
        fake.WriteLate = true;
        var result = Cycle().Run(client, Plan(), WorldUid);
        Assert.True(result.WriteSeen >= TimeSpan.FromMilliseconds(300), result.WriteSeen.ToString());
        Assert.NotEqual(result.FileBefore.Sha256, result.FileAfter.Sha256);
        await fake.LateWrite!;
    }

    // On Windows a watcher callback already in flight runs after the watcher is disposed. When the wait's semaphore was
    // disposed with it, that late Release threw ObjectDisposedException on a thread-pool thread and ended the test host.
    [Fact] public void AFileEventAfterTheWaitEndsIsHarmless()
    {
        var signal = new ChangeSignal(Characters);
        signal.Dispose();
        var late = new FileSystemEventArgs(WatcherChangeTypes.Changed, Characters, "tester.fch");
        signal.Wake(null, late);
        signal.Wake(null, late); // A second one while the first wake is still pending.
    }

    [Fact] public void ACloudCharacterIsRefusedBeforeTheLeave()
    {
        var (fake, transport, client) = Joined();
        fake.FileSource = "Cloud";
        Assert.Contains("never a cloud one", Assert.Throws<InvalidOperationException>(() => Cycle().Run(client, Plan(), WorldUid)).Message);
        Assert.Equal(0, transport.Count("cli_extension valheim.session/leave"));
    }

    [Fact] public void AnotherCharacterOrAnUnsetKeyIsRefusedBeforeTheLeave()
    {
        var (fake, transport, client) = Joined();
        fake.Character = "Someone";
        Assert.Contains("not the plan's Tester", Assert.Throws<InvalidOperationException>(() => Cycle().Run(client, Plan(), WorldUid)).Message);
        fake.Character = "Tester";
        Assert.Contains("mymod.missing are not set before the logout", Assert.Throws<InvalidOperationException>(() => Cycle(keys: ["mymod.home", "mymod.missing"]).Run(client, Plan(), WorldUid)).Message);
        Assert.Equal(0, transport.Count("cli_extension valheim.session/leave"));
    }

    [Fact] public void OnlyTheLocalCharacterFolderIsAccepted()
    {
        var (_, transport, client) = Joined();
        Assert.Contains("characters_local", Assert.Throws<ArgumentException>(() => Cycle(characters: Path.Combine(_root, "characters")).Run(client, Plan(), WorldUid)).Message);
        Assert.Throws<ArgumentException>(() => Cycle(characters: "characters_local").Run(client, Plan(), WorldUid)); // Not a full path.
        Assert.Throws<ArgumentException>(() => Cycle(keys: ["other.mod"]).Run(client, Plan(), WorldUid)); // Outside the prefix: never read.
        Assert.Equal(0, transport.Count("cli_extension valheim.session/leave"));
    }

    [Fact] public void InARoundEachPartIsAStepAndTheReadingsAreEvidence()
    {
        var output = Path.Combine(_root, "out"); Directory.CreateDirectory(output);
        var (_, _, client) = Joined();
        var server = new ScriptedTransport().Actor("server", "cli_expect worlduid=" + WorldUid);
        var report = new ScenarioReport("logout");
        Cycle().Run(new ClientRound("first", 0, true, server, client, report, output, WorldUid), Plan(), WorldUid);
        Assert.Equal(new[]
        {
            "first: the custom data is set and the profile file hashed before the logout", "first: the client leaves to its menu and the profile file is rewritten",
            "first: join again with the same character, protected", "first: the custom data came back",
        }, report.Steps.Select(s => s.Name));
        var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "first-logout.json"))).RootElement;
        Assert.True(evidence.GetProperty("oldMatchesBefore").GetBoolean());
        Assert.NotEqual(evidence.GetProperty("fileBefore").GetProperty("Sha256").GetString(), evidence.GetProperty("fileAfter").GetProperty("Sha256").GetString());
    }
}
