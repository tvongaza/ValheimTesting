using System.Reflection;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// One definition of macOS process identity and stop (#665), checked in the scripts as they are composed.
public sealed class MacProcessScriptTests
{
    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // Every script the toolkit sends to a host, as its static string fields hold them.
    private static IEnumerable<(string Name, string Script)> Scripts() =>
        typeof(MacProcess).Assembly.GetTypes().Where(type => !type.ContainsGenericParameters)
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(field => field.FieldType == typeof(string) && (field.IsInitOnly || field.IsLiteral))
            .Select(field => (field.DeclaringType!.Name + "." + field.Name, (string)field.GetValue(null)!))
            .Where(item => item.Item2 != null);

    [Fact]
    public void NoScriptReadsAMacStartIdentityButThroughMacProcess()
    {
        int own = Count(MacProcess.Functions, "lstart");
        Assert.Equal(1, own);
        var readers = Scripts().Where(item => item.Script.Contains("lstart", StringComparison.Ordinal)).ToList();
        foreach (var (name, script) in readers)
            Assert.True(Count(script, "lstart") == Count(script, MacProcess.Functions) * own, $"{name} reads lstart outside MacProcess.Functions");
        // Each macOS path holds it: the probe, the server's start, stop and wait, the client's start, launcher, stop and wait.
        foreach (string script in new[] { HostProcessProbe.Mac, HostServerScripts.MacStart, MacProcess.Stop, MacProcess.Wait,
            InteractiveScripts.MacStart, InteractiveScripts.MacLauncher, InteractiveScripts.MacStop })
            Assert.Contains(MacProcess.Functions, script);
    }

    [Fact]
    public void EveryMacStopIsTheOneStop()
    {
        Assert.Equal(1, Count(MacProcess.Functions, "vt_mac_stop() {"));
        // The client's stop calls it, then retires its GUI agent only after the verdict, and only for a game proven gone.
        Assert.Contains(MacProcess.Functions, InteractiveScripts.MacStop);
        string after = InteractiveScripts.MacStop[(InteractiveScripts.MacStop.LastIndexOf("}\n", StringComparison.Ordinal) + 2)..];
        Assert.Equal("vt_mac_stop\ncase \"$vt_stopped\" in gone|quit|stopped) vt_mac_agent_retire ;; esac", after.Trim());
        // No other script sends a Mac game SIGKILL.
        foreach (var (name, script) in Scripts().Where(item => item.Script.Contains("kill -KILL \"$game\"", StringComparison.Ordinal)))
            Assert.True(script == InteractiveScripts.LinuxStop || script.Contains(MacProcess.Functions, StringComparison.Ordinal), name + " kills a game outside the one stop");
    }

    [Fact]
    public void RecoveryGivesAProcessTheQuitBudgetItsOwnStopWouldHave()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), RunRecovery.QuitBudget(new Dictionary<string, string> { ["quitSeconds"] = "300", ["bootDirectory"] = "/b" }));
        Assert.Equal(ClientSession.DefaultQuitTimeout, RunRecovery.QuitBudget(new Dictionary<string, string> { ["quitSeconds"] = "-1" }));
        Assert.Equal(ClientSession.DefaultQuitTimeout, RunRecovery.QuitBudget(new Dictionary<string, string> { ["launchDirectory"] = "/l" }));
        // A journal is a file on the host: no line makes a recovery wait longer than a plan may.
        Assert.Equal(TimeSpan.FromSeconds(ServerRunPlan.MaximumQuitSeconds), RunRecovery.QuitBudget(new Dictionary<string, string> { ["quitSeconds"] = "99999999" }));
    }
}
