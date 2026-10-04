using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

// A refused command completes on the transport; acceptance is the default step check (#269).
public class GameReplyTests
{
    // The game's (1.0.16) and ValheimCLI's refusals, each on a transport-successful reply.
    public static TheoryData<string> Refusals => new()
    {
        "That command is a cheat, use devcommands to enable it",
        "Error executing command",
        "ERROR: code=no_local_player A loaded local character is required",
        "Unknown command my_mod_command",
    };

    [Theory] [MemberData(nameof(Refusals))]
    public void ADefaultStepFailsOnARefusalAndAnExpectedRefusalIsReturned(string refusal)
    {
        var transport = new ScriptedTransport().On("my_mod_command", _ => ScriptedTransport.Ok("my_mod_command: working", refusal));
        using var actor = transport.Actor();
        var error = Assert.Throws<InvalidOperationException>(() => actor.Execute("my_mod_command"));
        Assert.Equal("my_mod_command was refused: my_mod_command: working | " + refusal, error.Message);
        var reply = actor.Execute("my_mod_command", requireAccepted: false);
        Assert.True(reply.Ok); Assert.False(reply.Accepted); Assert.Equal(refusal, reply.Refusal);
    }

    [Fact] public void AnAcceptedReplyIsReadByItsLines()
    {
        var transport = new ScriptedTransport().On("cli_fly on", _ => ScriptedTransport.Ok("toggling", "OK: fly=True debugFly=True", "OK: fly=True again"));
        using var actor = transport.Actor();
        var reply = actor.Execute("cli_fly on");
        Assert.True(reply.Accepted);
        Assert.Equal("OK: fly=True debugFly=True", reply.Line("OK: fly="));
        Assert.Equal(new[] { "OK: fly=True debugFly=True", "OK: fly=True again" }, reply.Lines("OK: fly="));
        Assert.Equal("OK: fly=True debugFly=True", reply.RequireLine("OK: fly=True "));
        Assert.Equal("Fly off was not confirmed. Reply: toggling | OK: fly=True debugFly=True | OK: fly=True again",
            Assert.Throws<InvalidOperationException>(() => reply.RequireLine("OK: fly=False ", "Fly off was not confirmed")).Message);
        Assert.Null(reply.Line("ok: fly=")); // ordinal
    }

    [Fact] public void ATransportFailureKeepsItsCodeAndMessage()
    {
        var transport = new ScriptedTransport().On("slow", _ => new CommandResult { Ok = false, ErrorCode = "command_failed", Message = "ERROR: code=command_timeout ...", Output = [] });
        using var actor = transport.Actor();
        Assert.Equal("command_failed: ERROR: code=command_timeout ...", Assert.Throws<InvalidOperationException>(() => actor.Execute("slow")).Message);
        Assert.False(actor.Execute("slow", requireAccepted: false).Accepted);
    }

    [Fact] public void ModDataThatOnlyMentionsAnErrorIsNotARefusal()
    {
        foreach (string line in new[] { "OK: marked 3 errors", "REFUSED: outside the site", "[Info] no ERROR: here", "VALUE Unknown" })
            Assert.False(GameReply.IsRefusalLine(line), line);
    }
}
