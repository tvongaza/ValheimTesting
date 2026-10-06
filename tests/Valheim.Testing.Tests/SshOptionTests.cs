using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// ssh reads each -o value like a line of its config file (OpenSSH 8.7 and later): spaces split words, quotes group, a backslash
// escapes a quote, a backslash or an unquoted space, and a word starting with '#' is a comment. The expected arguments below are
// written out by hand from those rules; the ssh-to-localhost job checks the same values against a real ssh.
public class SshOptionTests
{
    [Theory]
    [InlineData("IdentityFile=/keys/valheim_tests")]
    [InlineData(@"IdentityFile=C:\Users\tester\.ssh\key")]
    [InlineData(@"IdentityFile=C:\keys\")]
    [InlineData("StrictHostKeyChecking=accept-new")]
    [InlineData("SetEnv=A=B")]
    [InlineData("ProxyJump=tester@jump.example:2200")]
    [InlineData("User=a#b")]
    // ssh takes a command as the rest of the line, quotes included: quoted, the shell would look for one file named "ssh -i ...".
    [InlineData("ProxyCommand=ssh -i /keys/test key -p 2200 -W %h:%p root@jump.example")]
    [InlineData("proxycommand=ssh -W %h:%p \"jump host\"")]
    [InlineData("KnownHostsCommand=/usr/local/bin/hosts %H")]
    [InlineData("LocalCommand=echo connected to %n")]
    public void AValueSshReadsAsGivenIsPassedUnchanged(string option) => Assert.Equal(option, SshGameHost.OptionArgument(SshChecks.CheckOption(option)));

    [Theory]
    [InlineData("User=Some Name", "User=\"Some Name\"")]
    [InlineData("IdentityFile=/keys/test key", "IdentityFile=\"/keys/test key\"")]
    [InlineData("User=  lead and trail  ", "User=\"  lead and trail  \"")]
    // A Windows path needs no escape: a backslash before a letter or a space is kept as it is, quoted or not.
    [InlineData(@"IdentityFile=C:\Users\Some Name\.ssh\key", @"IdentityFile=""C:\Users\Some Name\.ssh\key""")]
    [InlineData(@"User=a""b", @"User=""a\""b""")]
    [InlineData("User=o'brien", "User=\"o'brien\"")]
    [InlineData("User=#x", "User=\"#x\"")]
    // Unquoted, ssh would read \\ as one backslash. Only the first of the pair needs the escape: \s is kept as it is.
    [InlineData(@"IdentityFile=\\server\share\key", @"IdentityFile=""\\\server\share\key""")]
    [InlineData(@"IdentityFile=C:\keys with space\", @"IdentityFile=""C:\keys with space\\""")]
    [InlineData(@"User=a\""b", @"User=""a\\\""b""")]
    [InlineData(@"User=a\'b c", @"User=""a\\'b c""")]
    public void AValueSshWouldSplitOrUnescapeIsQuoted(string option, string argument) => Assert.Equal(argument, SshGameHost.OptionArgument(SshChecks.CheckOption(option)));

    [Fact] public async Task TheHostPassesTheQuotedFormAndNeverTheBareOne()
    {
        var fake = new FakeLauncher().Exits(0, "", FakeLauncher.Report(0));
        var host = new SshGameHost("box", "tester@box.example", HostShell.Bash, 0, ["User=Some Name", @"IdentityFile=C:\Users\Some Name\.ssh\key", "IdentitiesOnly=yes"], null, "ssh", fake);
        await host.RunAsync("true", null, TimeSpan.FromSeconds(30));
        var arguments = fake.Calls[0].Arguments;
        Assert.Equal(new[] { "-o", "User=\"Some Name\"", "-o", @"IdentityFile=""C:\Users\Some Name\.ssh\key""", "-o", "IdentitiesOnly=yes" }, arguments.Skip(8).Take(6));
        // Negative control: the bare form is what ssh refused ("keyword user extra arguments at end of line").
        Assert.DoesNotContain("User=Some Name", arguments);
        Assert.DoesNotContain(@"IdentityFile=C:\Users\Some Name\.ssh\key", arguments);
    }

    [Theory]
    [InlineData("User=", "User has no value")]
    [InlineData("User=a\tb", "User has a control character")]
    [InlineData("IdentityFile=/keys/a\nb", "IdentityFile has a control character")]
    [InlineData("User Some=Name", "An ssh option is Name=value")]
    [InlineData("User Some Name", "An ssh option is Name=value")]
    [InlineData("RemoteCommand=uptime", "RemoteCommand is refused: the host runs its own scripts")]
    public void AnOptionSshCannotCarryIsRefusedByName(string option, string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => new SshGameHost("box", "box", HostShell.Bash, 0, [option], null, "ssh", new FakeLauncher()));
        Assert.Contains(expected, error.Message);
    }

    [Fact] public void AProfilePassesItsOptionsQuotedAndRefusesAnEmptyValue()
    {
        string json = """
            { "hosts": { "far": { "kind": "ssh", "platform": "linux", "shell": "bash", "destination": "tester@far.example",
                                  "sshOptions": ["OPTION"], "lock": "/tmp/lock" } },
              "server": { "host": "far", "install": "/opt/valheim/server", "runtime": "/srv/vt/runs", "cliPort": 5577, "gamePort": 2456 } }
            """;
        var host = Assert.IsType<SshGameHost>(TestEnvironment.Parse(json.Replace("OPTION", "IdentityFile=/keys/test key")).CreateServerHost());
        Assert.Contains("IdentityFile=\"/keys/test key\"", host.SshArguments(forward: false, [], null));
        Assert.Contains("IdentityFile has no value", Assert.Throws<ArgumentException>(() => TestEnvironment.Parse(json.Replace("OPTION", "IdentityFile="))).Message);
    }
}

/// <summary>
/// The same values through a real ssh (<c>ssh -G</c> prints the configuration it read and connects nowhere), in the job that sets
/// VALHEIM_TESTING_SSH_DESTINATION. IdentityAgent takes any text and is printed back as given (nothing here connects to an agent),
/// so it shows what ssh made of each value.
/// </summary>
[Trait("Category", "GameHosts")]
public class SshOptionIntegrationTests
{
    public static TheoryData<string> Values => new()
    {
        "plain", "Some Name", "  lead and trail  ", "a\"b", "o'brien", "#x", @"\\server\share\key", @"C:\Users\Some Name\.ssh\key",
        @"C:\keys with space\", @"a\""b", @"a\'b c", @"C:\keys\",
    };

    private static string Destination => Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!;

    private static Task<ProcessExit> SshG(IReadOnlyList<string> arguments) =>
        SystemProcessLauncher.Instance.RunAsync(new ProcessCall("ssh", arguments, [], null, null, null, GameHostChecks.Generous), CancellationToken.None);

    [SshTheory, MemberData(nameof(Values))] public async Task SshReadsEachValueBackAsGiven(string value)
    {
        var host = new SshGameHost("ssh-g", Destination, HostShell.Bash, 0, ["IdentityAgent=" + value]);
        var exit = await SshG(host.SshArguments(forward: true, ["-G"], null));
        Assert.True(exit.End == ProcessEnd.Exited && exit.ExitCode == 0, $"ssh -G: {exit.End} {exit.ExitCode} {exit.Stderr}");
        string line = exit.Stdout.Split('\n').Select(text => text.TrimEnd('\r')).Single(text => text.StartsWith("identityagent ", StringComparison.Ordinal));
        Assert.Equal(value, line["identityagent ".Length..]);
    }

    // A command comes back as written; its words are the shell's to split, not ssh's.
    [SshTheory]
    [InlineData("ProxyCommand", "ssh -i /keys/test key -W %h:%p root@jump.example")]
    [InlineData("KnownHostsCommand", "/usr/local/bin/hosts \"a b\" %H")]
    public async Task SshReadsACommandBackAsWritten(string name, string command)
    {
        var host = new SshGameHost("ssh-g", Destination, HostShell.Bash, 0, [name + "=" + command]);
        var exit = await SshG(host.SshArguments(forward: true, ["-G"], null));
        Assert.True(exit.End == ProcessEnd.Exited && exit.ExitCode == 0, $"ssh -G: {exit.End} {exit.ExitCode} {exit.Stderr}");
        string key = name.ToLowerInvariant() + " ";
        Assert.Equal(command, exit.Stdout.Split('\n').Select(text => text.TrimEnd('\r')).Single(text => text.StartsWith(key, StringComparison.Ordinal))[key.Length..]);
    }

    [SshFact] public async Task NegativeControlSshKeepsTheQuotesOfAQuotedCommand()
    {
        // What SshGameHost passed before commands were exempt: ssh keeps the quotes, and the shell then runs one word.
        var exit = await SshG(["-G", "-o", "ProxyCommand=\"ssh -W %h:%p jump\"", "--", Destination]);
        Assert.True(exit.End == ProcessEnd.Exited && exit.ExitCode == 0, $"ssh -G: {exit.End} {exit.ExitCode} {exit.Stderr}");
        Assert.Contains("proxycommand \"ssh -W %h:%p jump\"", exit.Stdout);
    }

    [SshTheory]
    [InlineData("User=Some Name")]
    [InlineData("IdentityFile=/keys/test key")]
    public async Task NegativeControlSshRefusesTheBareForm(string option)
    {
        // What SshGameHost passed before values were quoted.
        var exit = await SshG(["-G", "-o", option, "--", Destination]);
        Assert.True(exit.End == ProcessEnd.Exited && exit.ExitCode == 255, $"ssh -G: {exit.End} {exit.ExitCode} {exit.Stdout} {exit.Stderr}");
        Assert.Contains("extra arguments at end of line", exit.Stderr);
    }

    // The job's key sits in a directory with a space in its name, so every ssh check above and in GameHostChecks connects with a
    // quoted IdentityFile. Without it the end-to-end coverage of quoting would silently disappear.
    [Fact] public void TheJobsKeyPathHasASpace()
    {
        if (Environment.GetEnvironmentVariable("VALHEIM_TESTING_REQUIRE_HOSTS") != "1") return;
        var options = (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(options, option => option.StartsWith("IdentityFile=", StringComparison.Ordinal) && option.Contains(' '));
    }
}
