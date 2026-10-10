using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

public sealed class DetachedSessionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-detached-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ServerOnlyDetachRequiresExplicitAbsoluteInputsBeforeLaunch()
    {
        string server = Path.Combine(_root, "server"), mod = Path.Combine(_root, "mod.dll"), output = Path.Combine(_root, "run");
        string[] valid = ["--server", server, "--mod", mod, "--server-only", "--output", output];
        Assert.True(DetachedSession.TryServerArgs(valid, out string selected, out _));
        Assert.Equal(output, selected);
        Assert.False(DetachedSession.TryServerArgs(["--server", server, "--mod", "relative.dll", "--server-only", "--output", output], out _, out string relative));
        Assert.Contains("--mod", relative);
        Assert.False(DetachedSession.TryServerArgs([.. valid, "--hold"], out _, out string held));
        Assert.Contains("detach owns the hold", held);
        Assert.False(DetachedSession.TryServerArgs([.. valid, "--bake-fixture", Path.Combine(_root, "bake")], out _, out _));
        Assert.False(DetachedSession.TryServerArgs(valid.Where(item => item != "--server-only").ToArray(), out _, out _));
        Directory.CreateDirectory(output);
        Assert.False(DetachedSession.TryServerArgs(valid, out _, out string existing));
        Assert.Contains("new absolute directory", existing);
    }

    [Fact]
    public void ReadinessIsWrittenAtomicallyOnlyInsideTheRunDataRoot()
    {
        string ready = Path.Combine(_root, "probe.ready.json");
        DetachedSession.SignalHeld("run-test", Path.Combine(_root, "evidence"), 321, "12345", ready, _root);
        using var json = JsonDocument.Parse(File.ReadAllText(ready));
        Assert.Equal("run-test", json.RootElement.GetProperty("Run").GetString());
        Assert.Equal(321, json.RootElement.GetProperty("Pid").GetInt32());
        Assert.False(File.Exists(ready + ".new"));
        Assert.Throws<InvalidOperationException>(() => DetachedSession.SignalHeld("run-test", _root, 321, "12345",
            Path.Combine(_root, "other", "probe.ready.json"), _root));
        Assert.Throws<IOException>(() => DetachedSession.SignalHeld("run-test", _root, 321, "12345", ready, _root));
    }

    [Fact]
    public void RunIdentityIsRecordedBeforeTheHoldAndCannotBeOverwritten()
    {
        string ready = Path.Combine(_root, "probe.ready.json");
        DetachedSession.SignalRunAt("run-early", ready, _root);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "probe.run.json")));
        Assert.Equal("run-early", json.RootElement.GetProperty("Run").GetString());
        Assert.True(json.RootElement.GetProperty("Pid").GetInt32() > 0);
        Assert.False(File.Exists(Path.Combine(_root, "probe.run.json.new")));
        Assert.Throws<IOException>(() => DetachedSession.SignalRunAt("run-other", ready, _root));
        Assert.Throws<InvalidOperationException>(() => DetachedSession.SignalRunAt("run-elsewhere",
            Path.Combine(_root, "other", "probe.ready.json"), _root));
    }

    [Fact]
    public void ModAndAdjacentDependencyAreCopiedWithoutEditingTheirSource()
    {
        string source = Path.Combine(_root, "build"), target = Path.Combine(_root, "inputs");
        Directory.CreateDirectory(source);
        string mod = Path.Combine(source, "MyMod.dll"), companion = Path.Combine(source, "Dependency.dll");
        File.WriteAllText(mod, "pinned mod");
        File.WriteAllText(companion, "adjacent dependency");
        string[] staged = DetachedSession.StageMods(["--mod", mod, "--mod", mod], target);
        Assert.Equal(staged[1], staged[3]);
        Assert.Equal("pinned mod", File.ReadAllText(staged[1]));
        Assert.Equal("adjacent dependency", File.ReadAllText(Path.Combine(Path.GetDirectoryName(staged[1])!, "Dependency.dll")));
        Assert.Equal("pinned mod", File.ReadAllText(mod));
        File.WriteAllText(mod, "new build");
        Assert.Equal("pinned mod", File.ReadAllText(staged[1]));
    }

    [Fact]
    public void LaunchdJobRunsOnceAndEscapesArguments()
    {
        var launch = new DetachedSession.Launch("abcd", "tv.valheimtesting.detached.abcd", _root,
            "ready", "out", "err", "tool", "inputs", "job.plist", "gui/501");
        string xml = DetachedSession.Plist(launch, ["/usr/bin/env", "MOD=alpha&beta", "/path with space/dotnet"]);
        var document = XDocument.Parse(xml);
        var entries = document.Root!.Element("dict")!.Elements().ToArray();
        Assert.Contains(entries, element => element.Name.LocalName == "false");
        Assert.Contains(entries, element => element.Name.LocalName == "true");
        Assert.Contains("MOD=alpha&beta", entries.Single(element => element.Name.LocalName == "array").Value);
        Assert.DoesNotContain("KeepAlive</key>\n    <true", xml);
    }

    [Fact]
    public void WindowsTaskUsesOneDesktopTokenAndQuotesTheRunnerArguments()
    {
        var launch = new DetachedSession.Launch("abcd", "ValheimTesting-detached-abcd", _root,
            "ready", "out", "err", "tool", "inputs", @"C:\run's folder\launcher.ps1", "windows/task");
        string runner = DetachedSession.WindowsLauncher(launch, ["VALHEIM_TEST_DETACH_READY=C:\\ready's file"],
            [@"C:\Program Files\dotnet\dotnet.exe", @"C:\run's folder\test.dll", "--hold"]);
        Assert.Contains("$env:VALHEIM_TEST_DETACH_READY = 'C:\\ready''s file'", runner);
        Assert.Contains("& 'C:\\Program Files\\dotnet\\dotnet.exe' 'C:\\run''s folder\\test.dll' '--hold'", runner);
        Assert.Contains("1>> 'out' 2>> 'err'", runner);
        string registration = DetachedSession.WindowsRegistration(launch);
        Assert.Contains("$logonType = 3", registration);
        Assert.Contains("$definition, 2, $identity.Name", registration); // TASK_CREATE; 1 would never register it.
        Assert.Contains("$definition.Settings.ExecutionTimeLimit = 'PT0S'", registration);
        Assert.Contains("C:\\run''s folder\\launcher.ps1", registration);
        Assert.DoesNotContain("S4U", registration);
    }

    [Fact]
    public void WindowsTaskScriptsParseBeforeAnyTaskIsRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launch = new DetachedSession.Launch("abcd", "ValheimTesting-detached-abcd", _root,
            "ready", "out", "err", "tool", "inputs", Path.Combine(_root, "launcher.ps1"), "windows/task");
        string[] scripts = [DetachedSession.WindowsRegistration(launch),
            DetachedSession.WindowsLauncher(launch, ["VALHEIM_TEST_DETACH_READY=ready"], ["dotnet.exe", "runner.dll"])];
        foreach (string script in scripts)
        {
            string file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(file, script);
            var start = new ProcessStartInfo("powershell.exe") { RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$tokens=$null;$errors=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('" +
                file.Replace("'", "''", StringComparison.Ordinal) + "',[ref]$tokens,[ref]$errors);if($errors.Count){$errors|Out-String|Write-Error;exit 1}");
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell script parser did not finish.");
            }
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
    }
}
