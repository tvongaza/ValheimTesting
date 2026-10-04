using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public class StrictExpectationsTests
{
    [Theory]
    [InlineData("cli_expect worlduid=7 plugin=01234567")]
    [InlineData("cli_expect --strict worlduid=7 plugin=01234567")]
    public void StrictIsAlwaysSentAndAppliedBeforeEachCommand(string input)
    {
        var transport = new Fake(); using var actor = new GameActor("test", transport);
        actor.VerifyEnvironment(input); actor.Execute("read"); actor.Execute("change");
        Assert.Equal(new[] { "cli_expect --strict worlduid=7 plugin=01234567", "cli_expect --strict worlduid=7 plugin=01234567", "read", "cli_expect --strict worlduid=7 plugin=01234567", "change" }, transport.Commands);
    }
    [Theory]
    [InlineData("cli_expect --strict")]
    [InlineData("cli_expect --strict --optional worlduid=7")]
    [InlineData("cli_expect worlduid=7 worlduid=8")]
    [InlineData("cli_expect plugin=not-a-hash")]
    [InlineData("cli_expect worlduid=7\nchange")]
    public void InvalidOrEmptyPinsNeverReachTransport(string input)
    {
        var transport = new Fake(); using var actor = new GameActor("test", transport);
        Assert.Throws<ArgumentException>(() => actor.VerifyEnvironment(input)); Assert.Empty(transport.Commands);
        Assert.Throws<InvalidOperationException>(() => actor.Execute("change"));
    }
    [Fact] public void ExtraPluginDetectedAfterVerificationStopsActionEvenWhenExpectingFailure()
    {
        var transport = new Fake(); using var actor = new GameActor("test", transport);
        actor.VerifyEnvironment("cli_expect worlduid=7"); transport.Drift = true;
        Assert.Throws<InvalidOperationException>(() => actor.Execute("change", requireAccepted: false));
        Assert.DoesNotContain("change", transport.Commands);
        transport.Drift = false;
        Assert.Throws<InvalidOperationException>(() => actor.Execute("change")); // explicit repin required after failure
        actor.VerifyEnvironment("cli_expect worlduid=7"); actor.Execute("change");
        Assert.Single(transport.Commands.Where(x => x == "change"));
    }
    [Fact] public void ExpectedCommandRefusalStillGetsStrictPreflight()
    {
        var transport = new Fake { RefuseCommand = true }; using var actor = new GameActor("test", transport);
        actor.VerifyEnvironment("cli_expect worlduid=7"); Assert.False(actor.Execute("removed-command", false).Ok);
        Assert.Equal("cli_expect --strict worlduid=7",transport.Commands[^2]);
    }
    [Fact] public async Task ReloadWaitOnlyRetriesExplicitPinsAndNeverAcceptsUnprovenEnvironment()
    {
        var transport = new Fake { Drift = true }; using var actor = new GameActor("test",transport);
        await Assert.ThrowsAsync<WaitTimeoutException>(() => actor.WaitForEnvironment("cli_expect worlduid=7",TimeSpan.FromMilliseconds(20)));
        Assert.All(transport.Commands, x => Assert.Equal("cli_expect --strict worlduid=7",x));
        Assert.Throws<InvalidOperationException>(() => actor.Execute("change"));
    }
    [Fact] public async Task ReloadWaitRechecksPinsWhenTheReloadEventArrivesNotBefore()
    {
        var transport = new Fake { Drift = true }; using var actor = new GameActor("test", transport);
        int events = 0;
        await actor.WaitForEnvironment("cli_expect worlduid=7", TimeSpan.FromSeconds(60), (left, token) => { events++; transport.Drift = false; return Task.CompletedTask; });
        Assert.Equal(1, events); Assert.Equal(2, transport.Commands.Count);
        actor.Execute("change");
    }
    [Fact] public async Task AFailedReloadEventEndsTheWaitWithoutAnAction()
    {
        var transport = new Fake { Drift = true }; using var actor = new GameActor("test", transport);
        await Assert.ThrowsAsync<WaitFailedException>(() => actor.WaitForEnvironment("cli_expect worlduid=7", TimeSpan.FromSeconds(60),
            (left, token) => throw new WaitFailedException("probe load line", "a line matched failure /Error/", TimeSpan.Zero, "[Error  : BepInEx] probe")));
        Assert.All(transport.Commands, x => Assert.Equal("cli_expect --strict worlduid=7", x));
    }
    [Fact] public async Task ReloadEventExpiryIsTheWaitsExpiry()
    {
        var transport = new Fake { Drift = true }; using var actor = new GameActor("test", transport);
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() => actor.WaitForEnvironment("cli_expect worlduid=7", TimeSpan.FromMilliseconds(200),
            (left, token) => Task.Delay(Timeout.InfiniteTimeSpan, token)));
        // The expired event ends the wait: no second pin check with next to no time left, and the mismatch stays the last thing seen.
        Assert.Contains("strict environment", error.Target); Assert.Single(transport.Commands); Assert.NotEqual("pins not checked", error.LastSeen);
    }
    [Fact] public void ReloadPinsChangeOnlyExplicitPluginAndRetainStrictWorldAndOtherPins()
    {
        string pins = StrictExpectations.WithPlugin("cli_expect --strict worlduid=7 core=01234567 probe=absent", "probe", "89abcdef");
        Assert.Equal("cli_expect --strict worlduid=7 core=01234567 probe=89abcdef",pins);
        Assert.Throws<ArgumentException>(() => StrictExpectations.WithPlugin(pins,"worlduid","8"));
        Assert.Throws<ArgumentException>(() => StrictExpectations.WithPlugin(pins,"probe","any"));
    }
    [Fact] public void BadOwnedSessionPinsFailBeforeLaunch()
    {
        int launches=0;
        Assert.Throws<ArgumentException>(() => new OwnedServerSession(_=>{ launches++; throw new Exception(); },()=>new Fake(),Path.GetTempPath(),"cli_expect --strict","test/session",TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1)));
        Assert.Equal(0,launches);
    }
    private sealed class Fake : IGameTransport
    {
        public List<string> Commands = [];
        public bool Drift, RefuseCommand;
        public CommandResult Execute(string command,TimeSpan timeout)
        {
            Commands.Add(command);
            if(command.StartsWith("cli_expect ")) return new(){Ok=!Drift,Output=Drift?["MISMATCH extra.plugin: loaded but not listed (strict)"]:["OK: EXPECT"]};
            return new(){Ok=!RefuseCommand,ErrorCode=RefuseCommand?"expected_refusal":"",Output=[]};
        }
        public void Dispose(){}
    }
}
