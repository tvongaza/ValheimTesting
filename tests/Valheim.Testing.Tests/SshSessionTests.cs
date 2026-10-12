using Valheim.Testing.Game;
using Xunit;

// #690: one predicate decides "this runner is inside an SSH session" for the Windows quit signal and for the ssh client choice.
public class SshSessionTests
{
    private static bool Active(string? connection, string? client) =>
        SshSession.IsActive(name => name switch { "SSH_CONNECTION" => connection, "SSH_CLIENT" => client, _ => throw new InvalidOperationException(name) });

    [Fact] public void SshConnectionAloneIsASession() => Assert.True(Active("10.0.0.2 51000 10.0.0.1 22", null));
    [Fact] public void SshClientAloneIsASession() => Assert.True(Active(null, "10.0.0.2 51000 22"));
    [Fact] public void BothIsASession() => Assert.True(Active("10.0.0.2 51000 10.0.0.1 22", "10.0.0.2 51000 22"));
    [Fact] public void NeitherIsNotASession() => Assert.False(Active(null, null));

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", "\t")]
    [InlineData(null, "")]
    public void BlankValuesAreNotASession(string? connection, string? client) => Assert.False(Active(connection, client));

    [Fact] public void ABlankVariableDoesNotHideTheOtherOne() => Assert.True(Active("  ", "10.0.0.2 51000 22"));
}
