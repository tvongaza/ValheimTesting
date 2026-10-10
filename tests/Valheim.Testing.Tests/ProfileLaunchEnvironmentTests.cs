using System.Diagnostics;
using System.Text;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class ProfileLaunchEnvironmentTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LinuxLaunchDropsInheritedLibraryPathOnlyWhenTheSpecUnsetsIt(bool client, bool profile)
    {
        if (OperatingSystem.IsWindows()) return;
        string source = client ? InteractiveScripts.LinuxStart : HostServerScripts.Start;
        const string begin = "unsets=(); removed=(); sets=(); args=()";
        const string end = "done <<< \"$spec\"";
        int first = source.IndexOf(begin, StringComparison.Ordinal);
        Assert.True(first >= 0);
        int last = source.IndexOf(end, first, StringComparison.Ordinal);
        Assert.True(last >= 0);
        string parser = source[first..(last + end.Length)];
        string Enc(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        string spec = (profile ? "unset " + Enc("LD_LIBRARY_PATH") + "\n" : "") +
            "prepend " + Enc("LD_LIBRARY_PATH=/profile") + "\n";
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("decode() { printf '%s' \"$1\" | base64 -D 2>/dev/null || printf '%s' \"$1\" | base64 -d; printf x; }; " +
            "LD_LIBRARY_PATH=/foreign; export LD_LIBRARY_PATH; spec='" + spec + "'; " + parser +
            "\nenv ${unsets[@]+\"${unsets[@]}\"} ${sets[@]+\"${sets[@]}\"} printenv LD_LIBRARY_PATH");
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(profile ? "/profile" : "/profile:/foreign", output);
    }
}
