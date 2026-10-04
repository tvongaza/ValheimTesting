using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Xunit;

public class TestAccessTests
{
    private sealed class AccessTransport : IGameTransport
    {
        public bool Dev, Ack, Allow = true, Dedicated = true, Server = true, Player, Joined, Profile = true;
        public bool RefuseAck, IgnoreAck, Malformed;
        public List<string> Commands = [];
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            Commands.Add(command);
            if (command.StartsWith("cli_expect ")) return ScriptedTransport.Ok("OK: EXPECT");
            if (command == "devcommands") { Dev = !Dev; return ScriptedTransport.Ok("Dev commands: " + Dev); }
            if (command is "confirmcheats" or "cli_acknowledge_local_cheats")
            {
                if (RefuseAck) return ScriptedTransport.Ok("ERROR: code=no_local_player A loaded local character is required");
                if (!IgnoreAck) Ack = true;
                return ScriptedTransport.Ok("OK");
            }
            if (command != "cli_access") throw new InvalidOperationException(command);
            return ScriptedTransport.Ok("ACCESS " + (Malformed ? "{}" : JsonSerializer.Serialize(new
            {
                schemaVersion = 1, complete = true, devcommands = Dev, cheatsAcknowledged = Ack,
                allowOnServerClients = Allow, server = Server, dedicated = Dedicated, joinedClient = Joined,
                localPlayer = Player, profileAvailable = Profile,
            })));
        }
        public GameActor Actor() { var actor = new GameActor("test-actor", this); actor.VerifyEnvironment("cli_expect --strict worlduid=7"); return actor; }
        public void Dispose() { }
    }

    [Fact] public void ServerEnablesBothGatesOnceThenVerifiesWithoutRetoggling()
    {
        var transport = new AccessTransport(); using var actor = transport.Actor();
        Assert.True(TestAccess.Ensure(actor, TestActorRole.DedicatedServer).CheatsAcknowledged);
        TestAccess.Ensure(actor, TestActorRole.DedicatedServer);
        Assert.Equal(new[] { "devcommands", "confirmcheats" }, transport.Commands.Where(c => c is "devcommands" or "confirmcheats"));
    }
    [Fact] public void JoinedClientAcknowledgesLocallyNotOnTheServer()
    {
        var transport = new AccessTransport { Dedicated = false, Server = false, Player = true, Joined = true };
        using var actor = transport.Actor();
        TestAccess.Ensure(actor, TestActorRole.ClientInWorld, clientMutations: true);
        Assert.Contains("cli_acknowledge_local_cheats", transport.Commands);
        Assert.DoesNotContain("confirmcheats", transport.Commands);
    }
    [Fact] public void MenuDoesNotTryToMarkAnUnloadedCharacter()
    {
        var transport = new AccessTransport { Dedicated = false, Server = false, Profile = false };
        using var actor = transport.Actor();
        Assert.False(TestAccess.Ensure(actor, TestActorRole.ClientMenu).CheatsAcknowledged);
        Assert.DoesNotContain("cli_acknowledge_local_cheats", transport.Commands);
    }
    [Fact] public void WrongRoleAndMissingMutationPermissionRefuseBeforeEffects()
    {
        var transport = new AccessTransport { Dedicated = false, Server = false, Player = true, Joined = true, Allow = false };
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TestAccess.Ensure(actor, TestActorRole.DedicatedServer));
        Assert.Throws<InvalidOperationException>(() => TestAccess.Ensure(actor, TestActorRole.ClientInWorld, true));
        Assert.DoesNotContain("devcommands", transport.Commands);
        TestAccess.Ensure(actor, TestActorRole.ClientInWorld, false); // No blanket mutation requirement for read-only checks.
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void TransportSuccessWithoutAcknowledgementIsNotReadiness(bool refusal)
    {
        var transport = new AccessTransport { Dev = true, RefuseAck = refusal, IgnoreAck = !refusal };
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => TestAccess.Ensure(actor, TestActorRole.DedicatedServer));
        Assert.Single(transport.Commands.Where(c => c == "confirmcheats"));
    }
    [Fact] public void MissingObservationDoesNotTriggerBlindMutations()
    {
        var transport = new AccessTransport { Malformed = true }; using var actor = transport.Actor();
        Assert.Throws<KeyNotFoundException>(() => TestAccess.Ensure(actor, TestActorRole.DedicatedServer));
        Assert.DoesNotContain("devcommands", transport.Commands);
    }
}
