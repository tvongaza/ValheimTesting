using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The synced-config observation against scripted replies shaped like the adapter's ConfigEntryCommand: a server and a
// client each report the live entry; the client takes the server's value after some reads, or never.
public class SyncedConfigTests
{
    private const string Path = "mymod.testing/config", Guid = "example.mymod", Section = "Server Settings", Key = "Max Carts";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(5);

    private static object Entry(string value, bool server, bool installed = true, bool found = true, string key = Key) => new
    {
        source = "bepinex-config", complete = true, guid = Guid, section = Section, key, server, installed, found,
        type = found ? "System.Int32" : null, value = found ? value : null, defaultValue = found ? "3" : null,
    };

    private static ScriptedTransport Side(Func<object> reply, List<IReadOnlyList<string>>? asked = null) =>
        new ScriptedTransport().Extension("mymod.testing", "config", args => { asked?.Add(args); return reply(); });

    [Fact] public async Task TheClientTakesTheServersValueWithinTheWait()
    {
        var asked = new List<IReadOnlyList<string>>();
        int reads = 0;
        using var server = Side(() => Entry("5", server: true)).Actor("server");
        using var client = Side(() => Entry(++reads < 3 ? "3" : "5", server: false), asked).Actor("client");
        var serverValue = SyncedConfig.Read(server, Path, Guid, Section, Key);
        Assert.Equal(("5", "System.Int32", "3", true), (serverValue.Value, serverValue.Type, serverValue.DefaultValue, serverValue.Server));
        var clientValue = await SyncedConfig.WaitForValue(client, Path, Guid, Section, Key, serverValue.Value!, TimeSpan.FromSeconds(5), Interval);
        SyncedConfig.RequireSame(serverValue, clientValue);
        Assert.Equal(3, reads);
        // Sections and keys with spaces travel as single percent-encoded tokens.
        Assert.All(asked, args => Assert.Equal(new[] { "example.mymod", "Server%20Settings", "Max%20Carts" }, args));
    }

    [Fact] public async Task AValueThatNeverArrivesTimesOutWithTheLastValueRead()
    {
        // Negative control: the client keeps its own value, as a mod that does not sync this entry would.
        using var client = Side(() => Entry("3", server: false)).Actor("client");
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            SyncedConfig.WaitForValue(client, Path, Guid, Section, Key, "5", TimeSpan.FromMilliseconds(150), Interval));
        Assert.Contains("to read 5", error.Message); Assert.Contains("[Server Settings] Max Carts = 3", error.Message);
        Assert.Throws<InvalidOperationException>(() => SyncedConfig.RequireSame(SyncedConfig.Parse(Json(Entry("5", true))), SyncedConfig.Parse(Json(Entry("3", false)))));
    }

    [Theory]
    [InlineData(false, true, "is not loaded in client")]
    [InlineData(true, false, "has no config entry [Server Settings] Max Carts in client")]
    public async Task AMissingPluginOrEntryFailsAtOnce(bool installed, bool found, string reason)
    {
        using var client = Side(() => Entry("", server: false, installed, found && installed)).Actor("client");
        var error = await Assert.ThrowsAsync<WaitFailedException>(() =>
            SyncedConfig.WaitForValue(client, Path, Guid, Section, Key, "5", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
        Assert.Contains(reason, error.Reason);
    }

    [Fact] public void AReplyAboutAnotherEntryOrAnIncompleteOneIsRefused()
    {
        using var other = Side(() => Entry("5", true, key: "Min Carts")).Actor();
        Assert.Contains("the reply is about", Assert.Throws<InvalidOperationException>(() => SyncedConfig.Read(other, Path, Guid, Section, Key)).Message);
        using var incomplete = Side(() => new { source = "bepinex-config", complete = false }).Actor();
        Assert.Throws<InvalidOperationException>(() => SyncedConfig.Read(incomplete, Path, Guid, Section, Key));
        Assert.Throws<InvalidOperationException>(() => SyncedConfig.Parse(Json(new { source = "bepinex-config", complete = true, guid = Guid, section = Section, key = Key, server = true, installed = true, found = true })));
        Assert.Throws<ArgumentException>(() => SyncedConfig.Token(""));
        Assert.Equal("a%25b%20c", SyncedConfig.Token("a%b c"));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
