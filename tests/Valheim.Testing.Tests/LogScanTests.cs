using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

// Scripted logs in the formats BepInEx's disk log and Unity's Player.log use. Each known problem is counted once, with
// its first line, and nothing else in its log is counted.
public sealed class LogScanTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("log-scan-").FullName;
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string Boot = "[Message:   BepInEx] BepInEx 5.4.23.2 - valheim_server\n[Info   :   BepInEx] Loading [My Mod 1.0.0]\n";
    private const string Tail = "[Message:   BepInEx] Chainloader startup complete\n[Info   :valheimCLI] Command server listening on 127.0.0.1:5577\n";

    private RunLog Write(string text, string name = "LogOutput.log", bool required = true)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return new RunLog("server BepInEx log", path, required);
    }
    private static LogPatternCount Count(LogFileScan scan, string pattern) => Assert.Single(scan.Counts, count => count.Pattern == pattern);

    // One case per name: the problem's text, and the line (1-based, in the scripted log) where it is first counted.
    public static TheoryData<string, string, int> Problems => new()
    {
        { "harmony-unpatch-all", Boot + "[Warning:  HarmonyX] UnpatchAll has been called - This will remove ALL HARMONY PATCHES.\n", 3 },
        { "harmony-undefined-target", Boot + "[Error  :   BepInEx] Error loading [Broken Mod 1.0.0] : Exception has been thrown by the target of an invocation.\n" +
            "System.ArgumentException: Undefined target method for patch method static System.Void BrokenMod.Patches::Postfix()\n" +
            "  at HarmonyLib.PatchClassProcessor.PatchWithAttributes (System.Reflection.MethodBase& lastOriginal) [0x00000] in <c0ffee>:0\n", 4 },
        { "accesstools-not-found", Boot + "[Warning:  HarmonyX] AccessTools.Method: Could not find method for type Player and name OldUpdate and parameters \n", 3 },
        { "missing-method", Boot + "[Error  : Unity Log] MissingMethodException: Method not found: void ZNet.OldMethod()\nStack trace:\nMyMod.Patches.Postfix () (at <c0ffee>:0)\n", 3 },
        { "missing-field", Boot + "[Error  : Unity Log] MissingFieldException: Field not found: int ZoneSystem.m_oldField\n", 3 },
        { "type-load", Boot + "[Error  : Unity Log] System.TypeLoadException: Could not load type of field 'ZDO:m_extra' (12) due to: Could not resolve type with token 01000042\n", 3 },
        { "nre-remove-objects", Boot + "[Error  : Unity Log] NullReferenceException: Object reference not set to an instance of an object\nStack trace:\n" +
            "ZNetScene.RemoveObjects (System.Collections.Generic.List`1[T] currentNearObjects, System.Collections.Generic.List`1[T] currentDistantObjects) (at <c0ffee>:0)\n" +
            "ZNetScene.Update () (at <c0ffee>:0)\n", 3 },
        { "rpc-method-missing", Boot + "[Warning: Unity Log] 09/29/2026 12:00:00: Failed to find rpc method 1234567890\n", 3 },
        { "missing-script", Boot + "[Warning: Unity Log] The referenced script on this Behaviour (Game Object 'BrokenPiece') is missing!\n", 3 },
        { "shader-unsupported", Boot + "[Warning: Unity Log] WARNING: Shader Unsupported: 'Custom/Piece' - All subshaders removed\n", 3 },
        { LogScanner.UnknownWarning, Boot + "[Warning:  My Mod] Config value out of range; using 5\n", 3 },
        { LogScanner.UnknownError, Boot + "[Error  :  My Mod] Could not open the cache file\nSystem.IO.IOException: Sharing violation\n", 3 },
    };
    [Fact] public void EveryNameHasAScriptedCase() =>
        Assert.Equal(LogScanner.Names.Order(), Problems.Select(row => (string)row[0]).Order());

    [Theory] [MemberData(nameof(Problems))]
    public void EachProblemIsCountedOnceWithItsFirstLineAndNothingElse(string pattern, string problem, int line)
    {
        var scan = LogScanner.Scan(Write(problem + Tail));
        Assert.True(scan.Present); Assert.Null(scan.Problem);
        var count = Count(scan, pattern);
        Assert.Equal(1, count.Count); Assert.Equal(line, count.FirstLine);
        Assert.Equal(problem.Split('\n')[line - 1], count.First);
        Assert.All(scan.Counts.Where(other => other.Pattern != pattern), other => Assert.Equal(0, other.Count));
        var severity = LogScanner.Patterns.FirstOrDefault(known => known.Name == pattern)?.Severity ?? LogSeverity.Warning;
        Assert.Equal(severity, count.Severity); Assert.Equal(severity == LogSeverity.Failure, scan.Failed);
    }

    [Fact] public void CountsAddUpAndKeepTheFirstOccurrence()
    {
        var scan = LogScanner.Scan(Write(Boot +
            "[Warning: Unity Log] Failed to find rpc method 111\n" + Tail +
            "[Warning: Unity Log] Failed to find rpc method 222\n[Warning: Unity Log] Failed to find rpc method 333\n"));
        var count = Count(scan, "rpc-method-missing");
        Assert.Equal(3, count.Count); Assert.Equal(3, count.FirstLine); Assert.EndsWith("111", count.First);
    }

    // A clean boot and a clean Player.log: every name is listed, all zero.
    [Fact] public void ACleanLogHasZeroCounts()
    {
        foreach (var scan in new[]
        {
            LogScanner.Scan(Write(Boot + Tail + "[Info   :CLI Standard Commands] Standard commands ready; owner=3f2a\n")),
            LogScanner.Scan(Write("Initialize engine version: 2022.3.50f1\nLoading world 'Fixture'\n\nDedicated server started\n", "Player.log", required: false)),
        })
        {
            Assert.True(scan.Present); Assert.False(scan.Failed);
            Assert.Equal(LogScanner.Names, scan.Counts.Select(count => count.Pattern));
            Assert.All(scan.Counts, count => { Assert.Equal(0, count.Count); Assert.Null(count.FirstLine); Assert.Null(count.First); });
        }
    }

    // Player.log has no BepInEx levels: its known problems count, its other lines are not guessed to be warnings.
    [Fact] public void PlayerLogProblemsCountWithoutLevels()
    {
        var scan = LogScanner.Scan(Write("Initialize engine version\nERROR: Shader Custom/Piece shader is not supported on this GPU (none of subshaders/fallbacks are suitable)\n\n" +
            "NullReferenceException: Object reference not set to an instance of an object\n  at ZNetScene.RemoveObjects (System.Collections.Generic.List`1[T] a, System.Collections.Generic.List`1[T] b) [0x00000] in <c0ffee>:0 \n\n" +
            "Some other message\n", "Player.log", required: false));
        Assert.Equal(1, Count(scan, "shader-unsupported").Count); Assert.Equal(1, Count(scan, "nre-remove-objects").Count);
        Assert.Equal(0, Count(scan, LogScanner.UnknownWarning).Count); Assert.Equal(0, Count(scan, LogScanner.UnknownError).Count);
        Assert.True(scan.Failed);
    }

    // Negative controls: near misses count as what they are, not as the pattern.
    [Fact] public void AnNreElsewhereIsAnUnknownErrorNotARemoveObjectsError()
    {
        var elsewhere = LogScanner.Scan(Write(Boot + "[Error  : Unity Log] NullReferenceException: Object reference not set to an instance of an object\nStack trace:\nMyMod.Piece.Awake () (at <c0ffee>:0)\n" +
            // The frame belongs to the next record, not to this NRE.
            "[Error  : Unity Log] NullReferenceException: Object reference not set to an instance of an object\n[Info   :   My Mod] ZNetScene.RemoveObjects was patched\n" + Tail));
        Assert.Equal(0, Count(elsewhere, "nre-remove-objects").Count); Assert.Equal(2, Count(elsewhere, LogScanner.UnknownError).Count);
        Assert.False(elsewhere.Failed);
    }
    [Fact] public void OrdinaryLinesMentioningTheWordsAreNotProblems()
    {
        var scan = LogScanner.Scan(Write(Boot +
            "[Info   :   My Mod] Patched ZNetScene.RemoveObjects\n" +
            "[Info   :   My Mod] Registered rpc method RPC_Mark\n" +
            "[Info   :   My Mod] Checked that no referenced script is missing? no: all present\n" + Tail));
        Assert.All(scan.Counts, count => Assert.Equal(0, count.Count));
    }

    [Fact] public void ARunReclassifiesAPatternWithItsReason()
    {
        var log = Write(Boot + "[Warning: Unity Log] Failed to find rpc method 1\n[Warning:  HarmonyX] UnpatchAll has been called - This will remove ALL HARMONY PATCHES.\n" + Tail);
        Assert.True(LogScanner.Scan(log).Failed);
        var scan = LogScanner.Scan(log, new Dictionary<string, LogClassification>
        {
            ["rpc-method-missing"] = new() { Severity = LogSeverity.Failure, Reason = "Both sides run the same mods; a missing handler is a bug." },
            ["harmony-unpatch-all"] = new() { Severity = LogSeverity.Warning, Reason = "The mod under test unpatches its own patches on shutdown only." },
        });
        Assert.Equal(LogSeverity.Failure, Count(scan, "rpc-method-missing").Severity);
        Assert.Equal("Both sides run the same mods; a missing handler is a bug.", Count(scan, "rpc-method-missing").Reason);
        Assert.Equal(LogSeverity.Warning, Count(scan, "harmony-unpatch-all").Severity);
        Assert.Null(Count(scan, "type-load").Reason);
        Assert.True(scan.Failed); // Now because of the RPC warning.
    }
    [Theory]
    [InlineData("not-a-pattern", "Failure", "reason", "not a known pattern")]
    [InlineData("type-load", "Warning", " ", "written reason")]
    [InlineData("type-load", null, "reason", "written reason")]
    public void AClassificationNeedsAKnownNameASeverityAndAReason(string name, string? severity, string reason, string message)
    {
        var classification = new LogClassification { Severity = severity == null ? null : Enum.Parse<LogSeverity>(severity), Reason = reason };
        var error = Assert.Throws<ArgumentException>(() => LogScanner.CheckClassifications(new Dictionary<string, LogClassification> { [name] = classification }));
        Assert.Contains(message, error.Message);
    }

    [Fact] public void AnAbsentLogHasNoCountsAndOnlyARequiredOneFails()
    {
        var optional = LogScanner.Scan(new RunLog("server Unity log", Path.Combine(_directory, "boot-1.game-1.log")));
        Assert.False(optional.Present); Assert.Empty(optional.Counts); Assert.Null(optional.Problem); Assert.False(optional.Failed);
        var notKept = LogScanner.Scan(new RunLog("boot-1 BepInEx log", Path.Combine(_directory, "boot-1.game-0.log"), Required: true));
        Assert.True(notKept.Failed); Assert.Empty(notKept.Counts); Assert.Contains("was not kept", notKept.Problem);
    }
    // DirectServerProcess leaves "<copy>.absent" when the game never created its BepInEx log.
    [Fact] public void ANeverWrittenBepInExLogPointsAtPlayerLogAndSecuritySoftware()
    {
        string copy = Path.Combine(_directory, "boot-1.game-0.log");
        File.WriteAllText(copy + ".absent", "Game did not create this log");
        var scan = LogScanner.Scan(new RunLog("boot-1 BepInEx log", copy, Required: true));
        Assert.True(scan.Failed); Assert.Contains("Player.log", scan.Problem); Assert.Contains("security software", scan.Problem);
    }

    [Fact] public void TheReportRecordsEveryLogAndFailsOnAFailurePattern()
    {
        var report = new ScenarioReport("scan"); report.Step("scenario", () => { });
        var broken = Write(Boot + "[Error  :   BepInEx] Error loading [Broken Mod 1.0.0] : Undefined target method for patch method static System.Void BrokenMod.Patches::Postfix()\n" + Tail);
        var player = Write("Dedicated server started\n", "Player.log", required: false);
        Assert.False(report.ScanLogs([broken, player]));
        Assert.False(report.Passed); Assert.Equal(2, report.Logs.Count);
        var step = Assert.Single(report.Steps, s => s.Name == "scan run logs");
        Assert.False(step.Passed);
        Assert.Contains("server BepInEx log: harmony-undefined-target x1, first at line 3: [Error  :   BepInEx] Error loading [Broken Mod 1.0.0]", step.Error);
        report.Write(_directory);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "result.json")));
        var counts = json.RootElement.GetProperty("Logs")[0].GetProperty("Counts");
        var undefined = counts.EnumerateArray().Single(count => count.GetProperty("Pattern").GetString() == "harmony-undefined-target");
        Assert.Equal(1, undefined.GetProperty("Count").GetInt32()); Assert.Equal("Failure", undefined.GetProperty("Severity").GetString());
    }
    [Fact] public void TheReportPassesACleanRunAndCountsItsWarnings()
    {
        var report = new ScenarioReport("scan"); report.Step("scenario", () => { });
        Assert.True(report.ScanLogs([Write(Boot + "[Warning:  My Mod] Config value out of range; using 5\n" + Tail)]));
        Assert.True(report.Passed);
        Assert.Equal(1, Count(report.Logs[0], LogScanner.UnknownWarning).Count);
    }

    [Fact] public void APlanReadsItsClassificationsAndPatchers()
    {
        string path = Path.Combine(_directory, "plan.json");
        File.WriteAllText(path, """{ "patchers": ["HookGenPatcher"], "logScan": { "rpc-method-missing": { "severity": "failure", "reason": "Same mods on both sides." } } }""");
        var plan = ServerRunPlan.Read<ServerRunPlan>(path);
        Assert.Equal(new[] { "HookGenPatcher" }, plan.Patchers);
        Assert.Equal(LogSeverity.Failure, plan.LogScan["rpc-method-missing"].Severity);
        plan.CheckPatchersAndLogScan();
        plan.LogScan["rpc-method-missing"].Reason = "";
        Assert.Throws<ArgumentException>(plan.CheckPatchersAndLogScan);
        File.WriteAllText(path, """{ "logScan": { "rpc-method-missing": { "severity": "failure", "reason": "x", "extra": 1 } } }""");
        Assert.ThrowsAny<JsonException>(() => ServerRunPlan.Read<ServerRunPlan>(path));
    }
}
