using System.Diagnostics;
using System.Xml.Linq;

internal sealed record ValidationInvocation(
    string[] Mode,
    bool Split,
    bool SkipCiShell,
    bool OnlyCiShell,
    bool CiToolkitTests,
    bool CiMainTests,
    bool CiBuildOnly,
    bool TestProjectTest,
    bool TestProjectTarget,
    int CiTimed)
{
    public const string Usage = "usage: dotnet run scripts/validate.cs [--skip-ci-shell|--only-ci-shell|--ci-toolkit-tests|--ci-main-tests|--ci-timed 1|2|3|--ci-build-only|--test-project ROOT PROJECT RESULTS [FILTER|--target TARGET [ARGUMENTS]]] [--split]; --split may appear anywhere";
    public const int TimedShardCount = 3;

    public bool TestProject => TestProjectTest || TestProjectTarget;

    public static ValidationInvocation Parse(string[] arguments)
    {
        int splitCount = arguments.Count(argument => argument == "--split");
        bool split = splitCount == 1;
        string[] mode = arguments.Where(argument => argument != "--split").ToArray();
        bool skipCiShell = mode.SequenceEqual(["--skip-ci-shell"]);
        bool onlyCiShell = mode.SequenceEqual(["--only-ci-shell"]);
        bool ciToolkitTests = mode.SequenceEqual(["--ci-toolkit-tests"]);
        bool ciMainTests = mode.SequenceEqual(["--ci-main-tests"]);
        bool ciBuildOnly = mode.SequenceEqual(["--ci-build-only"]);
        bool testProjectTest = mode is ["--test-project", _, _, _] ||
            mode is ["--test-project", _, _, _, var filter] && filter != "--target";
        bool testProjectTarget = mode is ["--test-project", _, _, _, "--target", var target, ..] && !string.IsNullOrWhiteSpace(target);
        bool testProject = testProjectTest || testProjectTarget;
        int ciTimed = mode is ["--ci-timed", var shardNumber] && int.TryParse(shardNumber, out int shardIndex) &&
            shardIndex >= 1 && shardIndex <= TimedShardCount ? shardIndex : 0;
        if ((mode.Length != 0 && !skipCiShell && !onlyCiShell && !ciToolkitTests && !ciMainTests && !ciBuildOnly && !testProject && ciTimed == 0) ||
            splitCount > 1 || split && testProjectTarget)
            throw new ArgumentException(Usage);
        // A one-project run names its repository and paths absolutely: a file-based app's working directory is the SDK's choice (#766).
        if (testProject && (mode.Skip(1).Take(3).Any(path => !Path.IsPathFullyQualified(path)) || !File.Exists(Path.Combine(mode[1], "cli-dependency.json"))))
            throw new ArgumentException(Usage + "; --test-project needs three absolute paths, and ROOT holds cli-dependency.json");
        return new(mode, split, skipCiShell, onlyCiShell, ciToolkitTests, ciMainTests, ciBuildOnly, testProjectTest, testProjectTarget, ciTimed);
    }
}

internal sealed record CiTimedMethod(string Name, double Seconds);

internal sealed record CiSplitFilter(string[] ExcludedTraits)
{
    public string Expression => string.Join('&', ExcludedTraits.Select(value => "Os!=" + value));

    public bool Includes(string? osTrait) => osTrait == null || !ExcludedTraits.Contains(osTrait, StringComparer.Ordinal);

    public static CiSplitFilter For(string operatingSystem) => operatingSystem switch
    {
        "Windows" => new(["neutral", "unix", "macos"]),
        "macOS" => new(["neutral", "windows"]),
        "Linux" => new(["windows", "macos"]),
        _ => throw new PlatformNotSupportedException("--split knows Windows, macOS and Linux only."),
    };
}

internal sealed record CiToolkitFilter(string[] ExcludedMethods)
{
    public string Expression => "Category!=CiShell" + string.Concat(ExcludedMethods.Select(name => "&FullyQualifiedName!=" + name));

    public bool Includes(string method, bool ciShell) => !ciShell && !ExcludedMethods.Contains(method, StringComparer.Ordinal);
}

internal static class CiToolkitShards
{
    public static CiToolkitFilter Filter(ValidationInvocation invocation, IEnumerable<CiTimedMethod> timedMethods)
    {
        if (!invocation.CiToolkitTests && !invocation.CiMainTests)
            throw new ArgumentException("A toolkit filter needs --ci-toolkit-tests or --ci-main-tests.", nameof(invocation));
        return new((invocation.CiToolkitTests ? Enumerable.Empty<string>() : timedMethods.Select(method => method.Name)).ToArray());
    }

    public static (string Name, int Shard)[] Assign(IEnumerable<CiTimedMethod> methods, int shardCount) => methods
        .GroupBy(method => method.Name[..method.Name.LastIndexOf('.')])
        .SelectMany(group =>
        {
            double[] loads = new double[shardCount];
            return group.OrderByDescending(method => method.Seconds).ThenBy(method => method.Name, StringComparer.Ordinal)
                .Select(method =>
                {
                    int shard = Array.IndexOf(loads, loads.Min());
                    loads[shard] += method.Seconds;
                    return (method.Name, Shard: shard + 1);
                });
        }).ToArray();
}

internal static class ValidationLimitReports
{
    public static string SoftLimit(string command, TimeSpan elapsed, TimeSpan softLimit, string testResults)
    {
        string slowest = "the slowest tests are unknown: this run wrote no TRX";
        string? trx = Directory.Exists(testResults) ? Directory.GetFiles(testResults, "*.trx", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (trx != null)
        {
            XDocument document;
            try { document = XDocument.Load(trx); }
            catch (System.Xml.XmlException) { document = new XDocument(new XElement("TestRun")); slowest = "the slowest tests are unknown: the TRX is unreadable"; }
            var tests = document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult")
                .Select(element => (Name: (string?)element.Attribute("testName") ?? "?",
                    Duration: TimeSpan.TryParse((string?)element.Attribute("duration"), out var duration) ? duration : TimeSpan.Zero))
                .OrderByDescending(test => test.Duration).Take(5).ToArray();
            if (tests.Length > 0) slowest = "slowest tests: " + string.Join("; ", tests.Select(test => $"{test.Name} {test.Duration.TotalSeconds:F1}s"));
        }
        return $"SOFT LIMIT: {command} took {Elapsed(elapsed)}, over the {Elapsed(softLimit)} soft limit; split, refactor or speed it up; {slowest}";
    }

    public static IEnumerable<string> IncompleteTests(string testResults, string relativeTo)
    {
        if (!Directory.Exists(testResults)) yield break;
        foreach (string sequence in Directory.GetFiles(testResults, "Sequence_*.xml", SearchOption.AllDirectories))
        {
            XElement? root = null;
            try { root = XDocument.Load(sequence).Root; }
            catch (System.Xml.XmlException)
            {
                // Iterator methods cannot yield inside a catch; the null below reports this sequence and continues.
            }
            if (root == null)
            {
                yield return $"unreadable blame sequence: {Path.GetRelativePath(relativeTo, sequence)}";
                continue;
            }
            foreach (var test in root.Elements("Test").Where(test => (string?)test.Attribute("Completed") != "True"))
                yield return $"started and not completed: {(string?)test.Attribute("DisplayName")} ({Path.GetRelativePath(relativeTo, sequence)})";
        }
    }

    public static string PreparationLimitFailure(string command, TimeSpan elapsed, TimeSpan cap) =>
        $"PREPARATION LIMIT: {command} is still running after {Elapsed(elapsed)} (preparation cap {Elapsed(cap)}); " +
        "no test names: preparation ended before the test command started";

    public static string HardLimitFailure(string command, TimeSpan elapsed, TimeSpan hardLimit, string? noTestNamesReason) =>
        $"FAILED: over the hard limit of {Elapsed(hardLimit)} after {Elapsed(elapsed)}: {command}" +
        (noTestNamesReason == null ? "" : $"; no test names: {noTestNamesReason}");

    public static string Elapsed(TimeSpan elapsed) => $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s";
}

internal static class ValidationLimitHandling
{
    public static bool ShouldWaitForCommandExit(int stoppedTestHosts) => stoppedTestHosts != 0;
}

// Internal script policy only: plain builds and Capture are deliberately outside the first preparation fix.
internal enum ValidationCommandPhase { Ordinary, Preparation, Test }

internal sealed record ValidationOwnedProcess(Process Handle, long StartUtcTicks, int Depth, bool IsTestHost)
{
    public Task? StopRequest { get; set; }
}
