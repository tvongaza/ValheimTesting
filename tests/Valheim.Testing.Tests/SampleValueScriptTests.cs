using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Runs tools/dev-loop/sample-value.sh (macOS/Linux) and its twin sample-value.ps1 (Windows PowerShell) against an inert
/// valheim-cli whose n-th cli_call prints REPLY_n and exits EXIT_n; no game. Each case runs on the OS of its script and
/// is reported as skipped elsewhere.
/// </summary>
public sealed class SampleValueScriptTests : IDisposable
{
    private const string Header = "sample,utc,value,error";
    private static readonly Regex Utc = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{3})?Z$");

    private readonly DevLoopScripts _scripts = new();

    public void Dispose() => _scripts.Dispose();

    private static string Script(bool powerShell) => powerShell ? "sample-value.ps1" : "sample-value.sh";

    /// <summary>A fake valheim-cli: counts its calls in COUNTER, appends its arguments to CALLS, prints REPLY_n and exits EXIT_n.</summary>
    private string FakeCli(bool powerShell)
    {
        if (powerShell)
            return _scripts.WindowsTool("valheim-cli",
                "$n = 1\r\n" +
                "if (Test-Path -LiteralPath $env:COUNTER) { $n = [int](Get-Content -LiteralPath $env:COUNTER) + 1 }\r\n" +
                "Set-Content -LiteralPath $env:COUNTER -Value $n\r\n" +
                "Add-Content -LiteralPath $env:CALLS -Value ($args -join ' ')\r\n" +
                "Write-Output ([Environment]::GetEnvironmentVariable(\"REPLY_$n\"))\r\n" +
                "exit [int][Environment]::GetEnvironmentVariable(\"EXIT_$n\")\r\n");
        _scripts.Folder("bin");
        return _scripts.Tool("bin/valheim-cli",
            "#!/bin/sh\n" +
            "n=$(( $(cat \"$COUNTER\" 2>/dev/null || echo 0) + 1 ))\n" +
            "echo \"$n\" > \"$COUNTER\"\n" +
            "echo \"$*\" >> \"$CALLS\"\n" +
            "eval \"reply=\\${REPLY_$n:-}; code=\\${EXIT_$n:-0}\"\n" +
            "printf '%s\\n' \"$reply\"\n" +
            "exit \"$code\"\n");
    }

    private Task<ScriptRun> Run(bool powerShell, string[] args, Dictionary<string, string?> replies)
    {
        var environment = new Dictionary<string, string?>
        {
            ["VALHEIM_CLI"] = FakeCli(powerShell),
            ["VALHEIM_CLI_PORT"] = "5602",
            ["COUNTER"] = _scripts.PathOf("count.txt"),
            ["CALLS"] = _scripts.PathOf("calls.txt"),
        };
        foreach ((string name, string? value) in replies) environment[name] = value;
        return _scripts.Run(Script(powerShell), args, environment);
    }

    private string[] Calls() =>
        File.Exists(_scripts.PathOf("calls.txt")) ? DevLoopScripts.Lines(File.ReadAllText(_scripts.PathOf("calls.txt"))) : Array.Empty<string>();

    /// <summary>A row's sample number and value/error fields, after checking its timestamp.</summary>
    private static (string Sample, string Fields) Row(string line)
    {
        string[] parts = line.Split(',', 3);
        Assert.True(parts.Length == 3, "not a CSV row: " + line);
        Assert.Matches(Utc, parts[1]);
        return (parts[0], parts[2]);
    }

    private async Task LaterFailureIsRecordedAndSamplingGoesOn(bool powerShell)
    {
        ScriptRun run = await Run(powerShell, new[] { "MyMod.Queue.Peek", "3", "0", "0,0,0", "3,100,4" }, new()
        {
            // A string value keeps its quotes and commas; the field quotes them again.
            ["REPLY_1"] = "VALUE \"a,b\"\nOK: CALL MyMod.Queue.Peek kind=method type=string",
            ["REPLY_2"] = "ERROR: code=null_target message=MyMod.Queue.instance is null\n  detail line",
            ["EXIT_2"] = "1",
            // A collection has no VALUE line: its item count is the value.
            ["REPLY_3"] = "  item one\nOK: CALL MyMod.Queue.Peek kind=method type=List items=4 shown=4",
        });

        run.AssertExit(0);
        string[] lines = DevLoopScripts.Lines(run.Stdout);
        Assert.Equal(4, lines.Length);
        Assert.Equal(Header, lines[0]);
        Assert.Equal(("1", "\"\"\"a,b\"\"\",\"\""), Row(lines[1]));
        Assert.Equal(("2", "\"\",\"ERROR: code=null_target message=MyMod.Queue.instance is null\""), Row(lines[2]));
        Assert.Equal(("3", "\"4\",\"\""), Row(lines[3]));
        Assert.Equal(Enumerable.Repeat("--port 5602 cli_call MyMod.Queue.Peek 0,0,0 3,100,4", 3), Calls());
    }

    private async Task FailedFirstSampleStops(bool powerShell)
    {
        ScriptRun run = await Run(powerShell, new[] { "No.Such.Member", "5", "0" }, new()
        {
            ["REPLY_1"] = "ERROR: code=no_member message=No.Such.Member not found",
            ["EXIT_1"] = "1",
            ["REPLY_2"] = "VALUE 1\nOK: CALL No.Such.Member kind=field type=int",
        });

        run.AssertExit(1);
        Assert.Equal(new[] { Header }, DevLoopScripts.Lines(run.Stdout));
        Assert.Contains("first sample failed (valheim-cli exit 1)", run.Stderr);
        Assert.Contains("ERROR: code=no_member", run.Stderr);
        Assert.Single(Calls());
    }

    private async Task RefusesBadArgumentsBeforeCalling(bool powerShell)
    {
        foreach (string[] args in new[] { new[] { "EnvMan.IsDay", "10" }, new[] { "EnvMan.IsDay", "0", "1" }, new[] { "EnvMan.IsDay", "2", "soon" } })
            (await Run(powerShell, args, new())).AssertExit(4);
        Assert.Empty(Calls());
    }

    [UnixFact] public Task BashLaterFailureIsRecordedAndSamplingGoesOn() => LaterFailureIsRecordedAndSamplingGoesOn(false);
    [UnixFact] public Task BashFailedFirstSampleStops() => FailedFirstSampleStops(false);
    [UnixFact] public Task BashRefusesBadArgumentsBeforeCalling() => RefusesBadArgumentsBeforeCalling(false);

    [WindowsFact] public Task PowerShellLaterFailureIsRecordedAndSamplingGoesOn() => LaterFailureIsRecordedAndSamplingGoesOn(true);
    [WindowsFact] public Task PowerShellFailedFirstSampleStops() => FailedFirstSampleStops(true);
    [WindowsFact] public Task PowerShellRefusesBadArgumentsBeforeCalling() => RefusesBadArgumentsBeforeCalling(true);
}
