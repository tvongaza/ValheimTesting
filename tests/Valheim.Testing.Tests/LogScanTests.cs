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
        { "missing-prefab-hash", Boot + "[Warning: Unity Log] 09/29/2026 12:00:00: Missing prefab hash: -887680680\n", 3 },
        { "missing-script", Boot + "[Warning: Unity Log] The referenced script on this Behaviour (Game Object 'BrokenPiece') is missing!\n", 3 },
        { "shader-unsupported", Boot + "[Warning: Unity Log] WARNING: Shader Unsupported: 'Custom/Piece' - All subshaders removed\n", 3 },
        { "macos-apple-plugin-missing", Boot + AppleGameKit, 3 },
        { "headless-server-graphics", Boot + "[Error  : Unity Log] AsyncResourceUpload failed.\n", 3 },
        { LogScanner.UnityException, Boot + JotunnNre, 3 },
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
        var severity = LogScanner.Patterns.FirstOrDefault(known => known.Name == pattern)?.Severity
            ?? (pattern is LogScanner.UnityException or LogScanner.UnknownError ? LogSeverity.Failure : LogSeverity.Warning);
        Assert.Equal(severity, count.Severity); Assert.Equal(severity == LogSeverity.Failure, scan.Failed);
    }

    [Theory]
    [InlineData("server BepInEx log", "Error")]
    [InlineData("client BepInEx log", "Fatal")]
    public void UnclassifiedBepInExErrorsFailAnOtherwisePassingRun(string role, string level)
    {
        var log = Write(Boot + $"[{level}  :   BepInEx] A new loader failure\n" + Tail);
        var report = new ScenarioReport("native smoke");
        report.Step("scenario", () => { });
        Assert.False(report.ScanLogs([log with { Role = role }]));
        Assert.False(report.Passed);
        var count = Count(Assert.Single(report.Logs), LogScanner.UnknownError);
        Assert.Equal((LogSeverity.Failure, 1, 3), (count.Severity, count.Count, count.FirstLine));
        Assert.Equal($"[{level}  :   BepInEx] A new loader failure", count.First);
    }

    [Fact] public void AKnownEnvironmentalErrorNeedsItsExactHeaderAndAReason()
    {
        const string known = "[Error  :   BepInEx] Unable to start Unity log writer";
        var classifications = new Dictionary<string, LogClassification>
        {
            [LogScanner.UnknownError] = new() { Expected = [known], Reason = "The headless test host cannot open Unity's log writer." },
        };
        var scan = LogScanner.Scan(Write(Boot + known + "\n" + Tail), classifications);
        Assert.False(scan.Failed);
        var count = Count(scan, LogScanner.UnknownError);
        Assert.Equal((LogSeverity.Failure, 0, 1), (count.Severity, count.Count, count.Expected));
        Assert.Equal(known, count.FirstExpected);

        scan = LogScanner.Scan(Write(Boot + known + "\n[Error  :   BepInEx] Unable to start Unity log writer again\n" + Tail), classifications);
        Assert.True(scan.Failed);
        count = Count(scan, LogScanner.UnknownError);
        Assert.Equal((1, 1, 4), (count.Count, count.Expected, count.FirstLine));
    }

    [Fact] public void TheWholeUnknownErrorCategoryCannotBeDowngraded()
    {
        var classifications = new Dictionary<string, LogClassification>
        {
            [LogScanner.UnknownError] = new() { Severity = LogSeverity.Warning, Reason = "Ignore errors" },
        };
        Assert.Contains("exact expected", Assert.Throws<ArgumentException>(() => LogScanner.CheckClassifications(classifications)).Message);
    }

    [Fact] public void UnknownErrorAllowanceMustNameAFullErrorOrFatalHeader()
    {
        foreach (string expected in new[] { "Unable to start Unity log writer", "[Warning: BepInEx] Unable to start Unity log writer" })
        {
            var classifications = new Dictionary<string, LogClassification>
            {
                [LogScanner.UnknownError] = new() { Expected = [expected], Reason = "Known environment limitation." },
            };
            Assert.Contains("exact expected BepInEx Error or Fatal header", Assert.Throws<ArgumentException>(() => LogScanner.CheckClassifications(classifications)).Message);
        }
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
        Assert.Equal(0, Count(scan, LogScanner.UnityException).Count); // the RemoveObjects NRE is counted once, by its pattern
        Assert.True(scan.Failed);
    }

    // Negative controls: near misses count as what they are, not as the pattern.
    [Fact] public void AnNreElsewhereIsAnUnknownErrorNotARemoveObjectsError()
    {
        var elsewhere = LogScanner.Scan(Write(Boot + "[Error  : Unity Log] NullReferenceException: Object reference not set to an instance of an object\nStack trace:\nMyMod.Piece.Awake () (at <c0ffee>:0)\n" +
            // The frame belongs to the next record, not to this NRE.
            "[Error  : Unity Log] NullReferenceException: Object reference not set to an instance of an object\n[Info   :   My Mod] ZNetScene.RemoveObjects was patched\n" + Tail));
        Assert.Equal(0, Count(elsewhere, "nre-remove-objects").Count); Assert.Equal(2, Count(elsewhere, LogScanner.UnknownError).Count);
        Assert.Equal(0, Count(elsewhere, LogScanner.UnityException).Count); // BepInEx's error records count them already
        Assert.True(elsewhere.Failed); // The unknown error is still a failure, just not attributed to RemoveObjects.
    }
    // HarmonyX skips a legacy instance UnpatchAll() when DisallowLegacyGlobalUnpatchAll is set: nothing was removed.
    [Fact] public void ASkippedLegacyUnpatchAllIsNotAGlobalUnpatch()
    {
        var scan = LogScanner.Scan(Write(Boot + "[Warning:  HarmonyX] Legacy UnpatchAll has been called AND DisallowLegacyGlobalUnpatchAll=true. Skipping execution of UnpatchAll\n" + Tail));
        Assert.Equal(0, Count(scan, "harmony-unpatch-all").Count); Assert.Equal(1, Count(scan, LogScanner.UnknownWarning).Count);
        Assert.False(scan.Failed);
    }
    [Fact] public void AShaderMissingItsPlatformIsCounted()
    {
        var scan = LogScanner.Scan(Write("Desired shader compiler platform 15 is not available in shader blob\n" +
            "ERROR: Shader Unlit/Color shader is not supported on this GPU (none of subshaders/fallbacks are suitable)\n", "Player.log", required: false));
        var count = Count(scan, "shader-unsupported");
        Assert.Equal(2, count.Count); Assert.Equal(1, count.FirstLine); Assert.Equal(LogSeverity.Warning, count.Severity);
    }
    // A Linux dedicated server's boot as the scheduled native checks logged it (#187: build 25527701, 2 Oct 2026): 13
    // graphics errors from its null GPU device, which only warn; the same systems' other errors still fail.
    private const string LinuxServerGraphics =
        "[Error  : Unity Log] AsyncResourceUpload failed.\n" +
        "[Error  : Unity Log] AsyncResourceUpload failed.\n" +
        "[Info   : Unity Log] 10/02/2026 10:00:27: GPU Device: 0000:0000 (Unknown)\n\n" +
        "[Error  : Unity Log] 10/02/2026 10:00:36: Failed to play intro cinematic\n\n" +
        "[Warning: Unity Log] HDR Render Texture not supported, disabling HDR on reflection probe.\n" +
        "[Error  : Unity Log] This custom render path shader needs to have at least 1 passes.\n" +
        "[Error  : Unity Log] Could not find material Hidden/VideoDecode. Make sure the Video shaders are included in your build, in the Built-in Shader Settings section of the Graphics Settings.\n" +
        "[Error  : Unity Log] Could not find video decode shader pass YCbCr_To_RGB1 in shader <not found>\n" +
        "[Error  : Unity Log] Could not find video decode shader pass YCbCrA_To_RGBAFull in shader <not found>\n" +
        "[Error  : Unity Log] Could not find video decode shader pass YCbCrA_To_RGBA in shader <not found>\n" +
        "[Error  : Unity Log] Could not find video decode shader pass Flip_RGBA_To_RGBA in shader <not found>\n" +
        "[Error  : Unity Log] Could not find video decode shader pass Flip_RGBASplit_To_RGBA in shader <not found>\n" +
        "[Error  : Unity Log] This custom render path shader needs to have at least 1 passes.\n" +
        "[Error  : Unity Log] Could not find material Hidden/VideoComposite. Make sure the Video shaders are included in your build, in the Built-in Shader Settings section of the Graphics Settings.\n" +
        "[Error  : Unity Log] Could not find video decode shader pass Default in shader <not found>\n";
    [Fact] public void ADedicatedServersGraphicsErrorsOnlyWarn()
    {
        var scan = LogScanner.Scan(Write(Boot + LinuxServerGraphics + Tail));
        Assert.False(scan.Failed);
        var count = Count(scan, "headless-server-graphics");
        Assert.Equal((LogSeverity.Warning, 13, 3), (count.Severity, count.Count, count.FirstLine));
        Assert.Equal(0, Count(scan, LogScanner.UnknownError).Count);
        Assert.Equal(1, Count(scan, LogScanner.UnknownWarning).Count);
        // The Windows server writes the same messages, without levels, only to its Unity log.
        var windows = LogScanner.Scan(Write(string.Join("\n", LinuxServerGraphics.Split('\n').Select(line => line.Replace("[Error  : Unity Log] ", ""))), "server.unity.log", required: false));
        Assert.Equal(13, Count(windows, "headless-server-graphics").Count);
        Assert.False(windows.Failed);
    }
    [Theory]
    [InlineData("[Error  : Unity Log] Could not find material MyMod/Glow. Make sure the Video shaders are included in your build, in the Built-in Shader Settings section of the Graphics Settings.")]
    [InlineData("[Error  : Unity Log] AsyncResourceUpload failed. Retrying bundle mymod_assets")]
    [InlineData("[Error  : Unity Log] Failed to play intro cinematic")]
    [InlineData("[Error  :   My Mod] AsyncResourceUpload failed.")]
    [InlineData("[Error  : Unity Log] Mod renderer: AsyncResourceUpload failed.")]
    [InlineData("[Error  :   My Mod] Could not find video decode shader pass Default in shader MyMod/Video")]
    public void OtherErrorsFromTheSameSystemsStillFail(string line)
    {
        var scan = LogScanner.Scan(Write(Boot + LinuxServerGraphics + line + "\n" + Tail));
        Assert.True(scan.Failed);
        Assert.Equal(13, Count(scan, "headless-server-graphics").Count);
        Assert.Equal((1, line), (Count(scan, LogScanner.UnknownError).Count, Count(scan, LogScanner.UnknownError).First));
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
    // What BepInEx's log on the Valheim 1.0.16 Windows dedicated server has of a [HarmonyPatch] on a method that does not
    // exist (native, 30 Sep 2026: BepInEx 5.4.23.5, HarmonyX 2.9.0, the FullLifecycle MissingHarmonyTarget control): this one
    // warning. PatchAll's "Undefined target method" exception went to Unity's log only.
    private const string MissingTarget = "[Warning:  HarmonyX] AccessTools.DeclaredMethod: Could not find method for type Player and name MyModControlMethodThatDoesNotExist and parameters \n";
    [Fact] public void AMissingHarmonyTargetFailsTheScanByDefault()
    {
        var scan = LogScanner.Scan(Write(Boot + MissingTarget + Tail));
        Assert.True(scan.Failed);
        var count = Count(scan, "accesstools-not-found");
        Assert.Equal((LogSeverity.Failure, 1, 3), (count.Severity, count.Count, count.FirstLine));
        Assert.Contains("MyModControlMethodThatDoesNotExist", count.First);
    }
    // PatchAll's exception, as the same run's Unity log (-logFile) had it: no level header, and it fails there too.
    [Fact] public void PatchAllsErrorInTheUnityLogFailsTheScan()
    {
        var scan = LogScanner.Scan(Write("Loading world 'LifecycleFixture'\n" +
            "ArgumentException: Undefined target method for patch method static void MyMod.Controls.MissingHarmonyTarget.Plugin+PatchMissingMethod::Postfix()\n" +
            "  at HarmonyLib.PatchClassProcessor.PatchWithAttributes (System.Reflection.MethodBase& lastOriginal) [0x00000] in <c0ffee>:0\n", "toolkit-unity.log", required: false));
        Assert.True(scan.Failed);
        Assert.Equal((1, 2), (Count(scan, "harmony-undefined-target").Count, Count(scan, "harmony-undefined-target").FirstLine));
    }
    // A mod's deliberate lookup of an optional member is named, not the whole pattern: a missing patch target still fails.
    [Fact] public void ExpectedLinesAreCountedApartAndOnlyTheyAreExcused()
    {
        var optional = "[Warning:  HarmonyX] AccessTools.Field: Could not find field for type ZNet and name m_optionalSetting\n";
        var classifications = new Dictionary<string, LogClassification>
        {
            ["accesstools-not-found"] = new() { Expected = ["name m_optionalSetting"], Reason = "My Mod probes a field only newer game versions have." },
        };
        var probeOnly = LogScanner.Scan(Write(Boot + optional + Tail), classifications);
        Assert.False(probeOnly.Failed);
        var count = Count(probeOnly, "accesstools-not-found");
        Assert.Equal((LogSeverity.Failure, 0, 1), (count.Severity, count.Count, count.Expected));
        Assert.Null(count.First); Assert.Equal(optional.TrimEnd('\n'), count.FirstExpected);
        Assert.Equal("My Mod probes a field only newer game versions have.", count.Reason);
        Assert.Equal(0, Count(probeOnly, LogScanner.UnknownWarning).Count); // an expected line is still a known one
        var both = LogScanner.Scan(Write(Boot + optional + MissingTarget + Tail), classifications);
        Assert.True(both.Failed);
        count = Count(both, "accesstools-not-found");
        Assert.Equal((1, 1, 4), (count.Count, count.Expected, count.FirstLine));
        Assert.Contains("MyModControlMethodThatDoesNotExist", count.First);
    }
    [Fact] public void AnUnknownLineCanBeNamedAsExpectedToo()
    {
        var scan = LogScanner.Scan(Write(Boot + "[Warning:  My Mod] QuitLog: quitting\n[Warning:  My Mod] Config value out of range\n" + Tail),
            new Dictionary<string, LogClassification> { [LogScanner.UnknownWarning] = new() { Expected = ["QuitLog: "], Reason = "The quit probe logs this on purpose." } });
        var count = Count(scan, LogScanner.UnknownWarning);
        Assert.Equal((1, 1, 4), (count.Count, count.Expected, count.FirstLine));
        Assert.Equal("[Warning:  My Mod] QuitLog: quitting", count.FirstExpected);
    }
    [Theory]
    [InlineData("not-a-pattern", "Failure", "reason", "not a built-in pattern")]
    [InlineData("type-load", "Warning", " ", "written reason")]
    [InlineData("type-load", null, "reason", "written reason")]
    public void AClassificationNeedsAKnownNameASeverityAndAReason(string name, string? severity, string reason, string message)
    {
        var classification = new LogClassification { Severity = severity == null ? null : Enum.Parse<LogSeverity>(severity), Reason = reason };
        var error = Assert.Throws<ArgumentException>(() => LogScanner.CheckClassifications(new Dictionary<string, LogClassification> { [name] = classification }));
        Assert.Contains(message, error.Message);
    }
    [Fact] public void ExpectedLinesNeedTextAndAReasonButNoSeverity()
    {
        LogScanner.CheckClassifications(new Dictionary<string, LogClassification> { ["accesstools-not-found"] = new() { Expected = ["m_optional"], Reason = "probe" } });
        Assert.Contains("non-empty text", Assert.Throws<ArgumentException>(() => LogScanner.CheckClassifications(
            new Dictionary<string, LogClassification> { ["accesstools-not-found"] = new() { Expected = [" "], Reason = "probe" } })).Message);
        Assert.Contains("written reason", Assert.Throws<ArgumentException>(() => LogScanner.CheckClassifications(
            new Dictionary<string, LogClassification> { ["accesstools-not-found"] = new() { Expected = ["m_optional"] } })).Message);
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

    [Fact] public void APlanReadsItsClassifications()
    {
        string path = Path.Combine(_directory, "plan.json");
        File.WriteAllText(path, """{ "logScan": { "rpc-method-missing": { "severity": "failure", "reason": "Same mods on both sides." } } }""");
        var plan = ServerRunPlan.Read<ServerRunPlan>(path);
        Assert.Equal(LogSeverity.Failure, plan.LogScan["rpc-method-missing"].Severity);
        plan.CheckLogScan();
        plan.LogScan["rpc-method-missing"].Reason = "";
        Assert.Throws<ArgumentException>(plan.CheckLogScan);
        File.WriteAllText(path, """{ "logScan": { "accesstools-not-found": { "expected": ["name m_optional"], "reason": "A probe." } } }""");
        plan = ServerRunPlan.Read<ServerRunPlan>(path);
        Assert.Null(plan.LogScan["accesstools-not-found"].Severity);
        Assert.Equal(new[] { "name m_optional" }, plan.LogScan["accesstools-not-found"].Expected);
        plan.CheckLogScan();
        File.WriteAllText(path, """{ "logScan": { "mymod-fallback": { "line": "fell back to ", "frame": "MyMod\\.", "severity": "failure", "reason": "Never in a pinned runtime." } } }""");
        plan = ServerRunPlan.Read<ServerRunPlan>(path);
        Assert.Equal(("fell back to ", "MyMod\\."), (plan.LogScan["mymod-fallback"].Line, plan.LogScan["mymod-fallback"].Frame));
        plan.CheckLogScan();
        File.WriteAllText(path, """{ "logScan": { "rpc-method-missing": { "severity": "failure", "reason": "x", "extra": 1 } } }""");
        Assert.ThrowsAny<JsonException>(() => ServerRunPlan.Read<ServerRunPlan>(path));
    }
    [Fact] public void APlanAddsItsOwnFailurePatternAndNamesAnotherModsNoise()
    {
        var plan = new Dictionary<string, LogClassification>
        {
            ["mymod-fallback"] = new() { Line = @"^\[Warning *: *My Mod\] fell back to ", Severity = LogSeverity.Failure, Reason = "MyMod must never fall back in a pinned runtime." },
            ["othermod-noise"] = new() { Line = @"^\[Warning *: *Other Mod\] Config file not found", Severity = LogSeverity.Warning, Reason = "Other Mod logs this on every first start." },
        };
        string text = Boot + "[Warning:  My Mod] fell back to the vanilla road table\n[Warning: Other Mod] Config file not found; writing defaults\n" + Tail;
        var scan = LogScanner.Scan(Write(text), plan);
        Assert.True(scan.Failed);
        var fallback = Count(scan, "mymod-fallback");
        Assert.Equal((1, LogSeverity.Failure, 3, "MyMod must never fall back in a pinned runtime."), (fallback.Count, fallback.Severity, fallback.FirstLine!.Value, fallback.Reason));
        Assert.Equal((1, LogSeverity.Warning), (Count(scan, "othermod-noise").Count, Count(scan, "othermod-noise").Severity));
        Assert.Equal(0, Count(scan, LogScanner.UnknownWarning).Count);
        Assert.Equal([.. LogScanner.Names.Take(LogScanner.Patterns.Count), "mymod-fallback", "othermod-noise", LogScanner.UnityException, LogScanner.UnknownWarning, LogScanner.UnknownError],
            scan.Counts.Select(count => count.Pattern));
        // Without the plan's patterns both lines are unknown warnings and nothing fails.
        var plain = LogScanner.Scan(Write(text));
        Assert.False(plain.Failed); Assert.Equal(2, Count(plain, LogScanner.UnknownWarning).Count);
    }
    [Fact] public void NewGameNoiseCanBeAllowedFromAPlanWithoutAToolkitRelease()
    {
        string text = Boot + "[Error  : Unity Log] Some new null-GPU error on a dedicated server.\n" + Tail;
        Assert.True(LogScanner.Scan(Write(text)).Failed); // unknown-error, as #187's lines were before #189
        var plan = new Dictionary<string, LogClassification>
        {
            ["headless-server-graphics-2"] = new() { Line = @"^\[Error\s+:\s*Unity Log\]\s*Some new null-GPU error on a dedicated server\.$", Severity = LogSeverity.Warning, Reason = "Every 1.0.17 dedicated server boot logs it (measured 4 Oct)." },
        };
        var scan = LogScanner.Scan(Write(text), plan);
        Assert.False(scan.Failed); Assert.Equal(1, Count(scan, "headless-server-graphics-2").Count);
    }
    [Fact] public void APlanRegexThatRunsAwayNamesItsPattern()
    {
        var plan = new Dictionary<string, LogClassification> { ["mymod-slow"] = new() { Line = "^(a+)+$", Severity = LogSeverity.Warning, Reason = "r" } };
        var error = Assert.Throws<InvalidOperationException>(() => LogScanner.Scan(Write(Boot + new string('a', 40) + "!\n" + Tail), plan));
        Assert.Equal("Log scan: mymod-slow's regex took over 1 s on line 3 of server BepInEx log; simplify it.", error.Message);
    }
    [Theory]
    [InlineData("mymod-x", null, "Failure", "r", null, "needs a line regex, a severity and a written reason")]
    [InlineData("mymod-x", "fell back", null, "r", null, "needs a line regex, a severity and a written reason")]
    [InlineData("mymod-x", "fell back", "Failure", " ", null, "needs a line regex, a severity and a written reason")]
    [InlineData("mymod-x", "fell (back", "Failure", "r", null, "mymod-x's line regex does not parse")]
    [InlineData("mymod-x", "fell back", "Failure", "r", "MyMod.(", "mymod-x's frame regex does not parse")]
    [InlineData("missing-method", "anything", "Failure", "r", null, "missing-method is built in; its regex cannot be replaced")]
    public void APlansOwnPatternIsRefusedUnlessComplete(string name, string? line, string? severity, string reason, string? frame, string message)
    {
        var plan = new Dictionary<string, LogClassification>
        {
            [name] = new() { Line = line, Frame = frame, Severity = severity == null ? null : Enum.Parse<LogSeverity>(severity), Reason = reason },
        };
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => LogScanner.CheckClassifications(plan)).Message);
        Assert.Throws<ArgumentException>(() => LogScanner.Scan(Write(Boot + Tail), plan));
    }
    // Unity's Player.log of the 30 Sep 2026 Jötunn #494 negative control (Valheim 1.0.16, Windows client), shortened and with
    // the assembly ids replaced: an exception Jötunn threw in a death hook, which BepInEx's log did not have.
    private const string JotunnNre =
        "NullReferenceException: Object reference not set to an instance of an object\n" +
        "  at Jotunn.Managers.CreatureManager+<>c__DisplayClass26_0.<EnableCumulativeLevelEffects>b__0 (Jotunn.Entities.CustomCreature x) [0x0000b] in <c0ffee>:0 \n" +
        "  at System.Linq.Enumerable.Any[TSource] (System.Collections.Generic.IEnumerable`1[T] source, System.Func`2[T,TResult] predicate) [0x0002c] in <c0ffee>:0 \n" +
        "  at Jotunn.Managers.CreatureManager.EnableCumulativeLevelEffects (LevelEffects self, System.Int32 level) [0x00012] in <c0ffee>:0 \n" +
        "  at (wrapper dynamic-method) LevelEffects.DMD<LevelEffects::SetupLevelVisualization>(LevelEffects,int)\n" +
        "  at Ragdoll.Setup (UnityEngine.Vector3 velocity, System.Single hue, System.Single saturation, System.Single value, CharacterDrop characterDrop, System.Int32 level, System.Boolean cheated) [0x00104] in <c0ffee>:0 \n" +
        "  at Character.OnDeath () [0x0030d] in <c0ffee>:0 \n";
    private const string UnityLines =
        "09/30/2026 20:34:02: Placed location StartTemple in zone 0,0  duration 9.5084 ms\n" +
        "09/30/2026 20:34:03: Placed location ShipSetting01 in zone -4,-2  duration 1.9998 ms\n";
    private const string UnityTail = "\n09/30/2026 20:34:03: SpawnPrefab StoneSpawner_Fader SPAWNING BossStone_Fader\n";
    // The macOS game's own plugins, as every kept macOS Unity log on 1.0.16 had them (clients and dedicated servers, 18-30 Sep 2026).
    private const string AppleGameKit =
        "DllNotFoundException: GameKitWrapper assembly:<unknown assembly> type:<unknown type> member:(null)\n" +
        "  at (wrapper managed-to-native) Apple.GameKit.DefaultNSErrorHandler+Interop.DefaultNSErrorHandler_Set(Apple.Core.Runtime.NSExceptionCallback)\n" +
        "  at Apple.GameKit.DefaultNSErrorHandler.Init () [0x00000] in <c0ffee>:0 \n";
    private const string AppleCore =
        "DllNotFoundException: AppleCoreNativeMac assembly:<unknown assembly> type:<unknown type> member:(null)\n" +
        "  at (wrapper managed-to-native) Apple.Core.Availability.AppleCore_GetRuntimeEnvironment()\n" +
        "  at Apple.Core.Availability.Initialize () [0x00000] in <c0ffee>:0 \n";
    private RunLog PlayerLog(string text) => Write(text, "Player.log", required: false) with { Role = "client Player.log" };

    [Fact] public void AnExceptionInPlayerLogFailsWithItsFirstLineAndTheFrameThatThrew()
    {
        var scan = LogScanner.Scan(PlayerLog(UnityLines + JotunnNre + UnityTail));
        Assert.True(scan.Failed);
        var count = Count(scan, LogScanner.UnityException);
        Assert.Equal((LogSeverity.Failure, 1, 3), (count.Severity, count.Count, count.FirstLine));
        Assert.Equal("NullReferenceException: Object reference not set to an instance of an object", count.First);
        Assert.StartsWith("at Jotunn.Managers.CreatureManager+<>c__DisplayClass26_0.<EnableCumulativeLevelEffects>b__0", count.FirstFrame);
        Assert.All(scan.Counts.Where(other => other.Pattern != LogScanner.UnityException), other => Assert.Equal(0, other.Count));

        var report = new ScenarioReport("scan"); report.Step("scenario", () => { });
        Assert.False(report.ScanLogs([PlayerLog(UnityLines + JotunnNre + UnityTail)]));
        var step = Assert.Single(report.Steps, s => s.Name == "scan run logs");
        Assert.Contains("client Player.log: unity-exception x1, first at line 3: NullReferenceException: Object reference not set", step.Error);
        Assert.Contains("[at Jotunn.Managers.CreatureManager+<>c__DisplayClass26_0.<EnableCumulativeLevelEffects>b__0", step.Error);
    }
    // The frame chosen is the first that names the code that threw, past the runtime's own frames.
    [Fact] public void TheUsefulFrameSkipsRuntimeFrames()
    {
        var scan = LogScanner.Scan(PlayerLog("Default audio device was changed, but the audio system failed to initialize it. Attempting to reset sound system.\n" +
            "FieldAccessException: Field `Terminal:commands' is inaccessible from method `MyMod.TestAdapter.Plugin/<>c:<Start>b__3_0 (Terminal/ConsoleCommand)'\n" +
            "  at System.Linq.Enumerable.TryGetFirst[TSource] (System.Collections.Generic.IEnumerable`1[T] source, System.Func`2[T,TResult] predicate, System.Boolean& found) [0x00000] in <c0ffee>:0 \n" +
            "  at MyMod.TestAdapter.Plugin+<Start>d__3.MoveNext () [0x0009b] in <c0ffee>:0 \n" + UnityTail));
        var count = Count(scan, LogScanner.UnityException);
        Assert.Equal((1, 2), (count.Count, count.FirstLine));
        Assert.Equal("at MyMod.TestAdapter.Plugin+<Start>d__3.MoveNext () [0x0009b] in <c0ffee>:0", count.FirstFrame);
    }
    // A known pattern's exception is counted once, by that pattern.
    [Fact] public void AKnownExceptionIsNotAlsoAUnityException()
    {
        var scan = LogScanner.Scan(PlayerLog(UnityLines + "MissingMethodException: Method not found: void ZNet.OldMethod()\n  at MyMod.Patches.Postfix () [0x00000] in <c0ffee>:0 \n" + UnityTail +
            AppleGameKit + "\n" + AppleCore + UnityTail));
        Assert.Equal(1, Count(scan, "missing-method").Count); Assert.Equal(2, Count(scan, "macos-apple-plugin-missing").Count);
        Assert.Equal(0, Count(scan, LogScanner.UnityException).Count);
    }
    // The macOS game's missing Apple plugins only warn; another missing native library (PlayFab's, which crossplay needs) fails.
    [Fact] public void OnlyTheMacClientsOwnPluginsAreExcused()
    {
        var mac = LogScanner.Scan(PlayerLog(AppleGameKit + "\n" + AppleCore + UnityTail));
        Assert.False(mac.Failed);
        Assert.Equal((LogSeverity.Warning, 2), (Count(mac, "macos-apple-plugin-missing").Severity, Count(mac, "macos-apple-plugin-missing").Count));
        var party = LogScanner.Scan(PlayerLog("DllNotFoundException: libParty.so assembly:<unknown assembly> type:<unknown type> member:(null)\n" +
            "  at (wrapper managed-to-native) PartyCSharpSDK.Interop.PFPInterop.PartyInitialize(byte[],PartyCSharpSDK.Interop.PARTY_HANDLE&)\n" +
            "  at PlayFab.Party.PlayFabMultiplayerManager.InitializeImpl () [0x000c4] in <c0ffee>:0 \n"));
        Assert.True(party.Failed);
        Assert.Equal(0, Count(party, "macos-apple-plugin-missing").Count);
        Assert.Equal("at PlayFab.Party.PlayFabMultiplayerManager.InitializeImpl () [0x000c4] in <c0ffee>:0", Count(party, LogScanner.UnityException).FirstFrame);
    }
    // A negative control that throws on purpose names that exception by a frame; every other exception still fails.
    [Fact] public void AnExpectedExceptionIsNamedByItsFrameAndOthersStillFail()
    {
        var classifications = new Dictionary<string, LogClassification>
        {
            [LogScanner.UnityException] = new() { Expected = ["Jotunn.Managers.CreatureManager.EnableCumulativeLevelEffects"], Reason = "The negative control runs Jotunn without the #494 fix." },
        };
        var controlOnly = LogScanner.Scan(PlayerLog(UnityLines + JotunnNre + UnityTail), classifications);
        Assert.False(controlOnly.Failed);
        var count = Count(controlOnly, LogScanner.UnityException);
        Assert.Equal((0, 1), (count.Count, count.Expected));
        Assert.Equal("NullReferenceException: Object reference not set to an instance of an object", count.FirstExpected);
        Assert.Equal("The negative control runs Jotunn without the #494 fix.", count.Reason);

        var another = "NullReferenceException: Object reference not set to an instance of an object\n  at MyMod.Marker.Update () [0x00010] in <c0ffee>:0 \n";
        var both = LogScanner.Scan(PlayerLog(UnityLines + JotunnNre + UnityTail + another), classifications);
        Assert.True(both.Failed);
        count = Count(both, LogScanner.UnityException);
        Assert.Equal((1, 1, 12), (count.Count, count.Expected, count.FirstLine));
        Assert.Equal("at MyMod.Marker.Update () [0x00010] in <c0ffee>:0", count.FirstFrame);
        LogScanner.CheckClassifications(classifications);
    }
    // Player.log and a server's output mix BepInEx's console lines with Unity's: Unity's exception after an Info line counts.
    [Fact] public void AnExceptionAfterBepInExConsoleLinesCounts()
    {
        var scan = LogScanner.Scan(PlayerLog("[Message:   BepInEx] Preloader finished\n" +
            "DirectoryNotFoundException: Could not find a part of the path '/home/steam/server/BepInEx/scripts'.\n" +
            "  at System.IO.Directory.GetFiles (System.String path, System.String searchPattern, System.IO.SearchOption searchOption) [0x00008] in <c0ffee>:0 \n"));
        var count = Count(scan, LogScanner.UnityException);
        Assert.Equal((1, 2), (count.Count, count.FirstLine));
        Assert.Equal(0, Count(scan, LogScanner.UnknownError).Count);
        Assert.StartsWith("at System.IO.Directory.GetFiles", count.FirstFrame); // only runtime frames: the first one
    }
    [Fact] public void AUnityMessageEndsAnEarlierBepInExErrorRecordWithoutABlankLine()
    {
        var scan = LogScanner.Scan(PlayerLog("[Error  : My Mod] Failed to load an optional config\n" +
            "09/30/2026 20:34:02: Continuing startup\n" + JotunnNre));
        Assert.Equal(1, Count(scan, LogScanner.UnknownError).Count);
        Assert.Equal(1, Count(scan, LogScanner.UnityException).Count);
        Assert.True(scan.Failed);
    }
    [Fact] public void OneExceptionCannotBorrowTheNextExceptionsFrame()
    {
        var scan = LogScanner.Scan(PlayerLog(
            "DllNotFoundException: libParty.so assembly:<unknown assembly> type:<unknown type> member:(null)\n" +
            "  at PlayFab.Party.PlayFabMultiplayerManager.InitializeImpl () [0x000c4] in <c0ffee>:0 \n" +
            AppleCore));
        Assert.Equal(1, Count(scan, LogScanner.UnityException).Count);
        Assert.Equal(1, Count(scan, "macos-apple-plugin-missing").Count);
        Assert.True(scan.Failed);
    }
    // Routine Unity lines and words that only look like exceptions are not counted.
    [Fact] public void RoutineUnityLinesAreNotExceptions()
    {
        var scan = LogScanner.Scan(PlayerLog(UnityLines +
            "Unloading 93 unused Assets to reduce memory usage. Loaded Objects now: 293256.\n" +
            "09/30/2026 20:34:02: Exception handling test skipped: no exception\n" +
            "No exceptions were thrown during the load\n" +
            "  ExceptionHandlerInstalled: true\n" + UnityTail));
        Assert.False(scan.Failed);
        Assert.All(scan.Counts, count => Assert.Equal(0, count.Count));
    }
}
