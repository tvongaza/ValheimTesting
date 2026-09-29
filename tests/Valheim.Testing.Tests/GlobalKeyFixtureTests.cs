using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The global-key fixture action against a scripted server and client shaped like the adapter's GlobalKeyCommands: the
// server applies each change and the client takes the server's whole list when the scripted "broadcast" arrives.
public class GlobalKeyFixtureTests
{
    private const string List = "mymod.testing/globalkeys", Change = "mymod.testing/globalkey";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5), Interval = TimeSpan.FromMilliseconds(5);

    private sealed class World
    {
        public readonly SortedSet<string> ServerKeys = new(StringComparer.Ordinal) { "nomap", "preset combat_default" };
        public SortedSet<string> ClientKeys = new(StringComparer.Ordinal) { "nomap", "preset combat_default" };
        /// <summary>Client reads left before the server's list arrives; null never delivers it.</summary>
        public int? ReadsUntilBroadcast = 1;
        public bool ServerIgnoresChanges;
        public readonly ScriptedTransport Server, Client;

        public World()
        {
            Server = new ScriptedTransport()
                .Extension("mymod.testing", "globalkeys", _ => new { source = "global-keys", complete = true, server = true, keys = ServerKeys.ToArray() })
                .Extension("mymod.testing", "globalkey", args =>
                {
                    string name = args[1];
                    if (!ServerIgnoresChanges)
                    {
                        ServerKeys.RemoveWhere(line => line == name || line.StartsWith(name + " ", StringComparison.Ordinal));
                        if (args[0] == "set") ServerKeys.Add(args.Count == 3 ? name + " " + args[2].ToLowerInvariant() : name);
                    }
                    return new { source = "global-key-change", complete = true, action = args[0], name, value = args.Count == 3 ? args[2] : null, keys = ServerKeys.ToArray() };
                }, readOnly: false);
            Client = new ScriptedTransport().Extension("mymod.testing", "globalkeys", _ =>
            {
                if (ReadsUntilBroadcast is int left)
                {
                    if (left <= 0) ClientKeys = new(ServerKeys, StringComparer.Ordinal);
                    else ReadsUntilBroadcast = left - 1;
                }
                return new { source = "global-keys", complete = true, server = false, keys = ClientKeys.ToArray() };
            });
        }
    }

    [Fact] public async Task SetsAndRemovesEachKeyOnceThenWaitsForTheClientToListTheServersSet()
    {
        var world = new World { ReadsUntilBroadcast = 2 };
        using var server = world.Server.Actor("server");
        using var client = world.Client.Actor("client");
        var keys = await GlobalKeyFixture.Apply(server, client, Change, List, ["defeated_eikthyr", "PlayerDamage 50"], ["nomap"], Timeout, Interval);
        Assert.Equal(new[] { "defeated_eikthyr", "playerdamage 50", "preset combat_default" }, keys);
        Assert.Equal(keys, GlobalKeyFixture.Read(client, List));
        Assert.Equal(3, world.Server.Count("cli_extension mymod.testing/globalkey")); // Each change once, never repeated while waiting.
        Assert.True(world.Client.Count("cli_extension mymod.testing/globalkeys") >= 3);
    }

    [Fact] public async Task AClientWhoseKeysDifferTimesOutNamingTheDifference()
    {
        // Negative control: the client keeps a key it added locally and never gets the server's list.
        var world = new World { ReadsUntilBroadcast = null };
        world.ClientKeys.Add("defeated_bonemass");
        using var server = world.Server.Actor("server");
        using var client = world.Client.Actor("client");
        GlobalKeyFixture.Set(server, Change, "defeated_eikthyr");
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            GlobalKeyFixture.WaitForClient(server, client, List, TimeSpan.FromMilliseconds(200), Interval));
        Assert.Contains("client lacks [defeated_eikthyr]; client has extra [defeated_bonemass]", error.Message);
    }

    [Fact] public async Task TheWaitWakesOnTheChangeEventBeforeTheInterval()
    {
        var world = new World { ReadsUntilBroadcast = 1 };
        using var server = world.Server.Actor("server");
        using var client = world.Client.Actor("client");
        int events = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await GlobalKeyFixture.WaitForClient(server, client, List, Timeout, TimeSpan.FromMinutes(1), (_, _) => { events++; return Task.CompletedTask; });
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30)); Assert.Equal(1, events);
    }

    [Fact] public void AChangeTheServerDidNotKeepFails()
    {
        var world = new World { ServerIgnoresChanges = true };
        using var server = world.Server.Actor("server");
        var error = Assert.Throws<InvalidOperationException>(() => GlobalKeyFixture.Set(server, Change, "defeated_eikthyr"));
        Assert.Contains("did not keep global key \"defeated_eikthyr\"", error.Message);
        Assert.Contains("still has global key \"nomap\"", Assert.Throws<InvalidOperationException>(() => GlobalKeyFixture.Remove(server, Change, "NoMap")).Message);
    }

    [Fact] public void ChangesNeedTheMutatingCommandAndOneWordNamesAndValues()
    {
        var world = new World();
        using var server = world.Server.Actor("server");
        Assert.Contains("read-only", Assert.Throws<InvalidOperationException>(() => GlobalKeyFixture.Set(server, List, "defeated_eikthyr")).Message);
        Assert.Throws<ArgumentException>(() => GlobalKeyFixture.Set(server, Change, "defeated eikthyr"));
        Assert.Throws<ArgumentException>(() => GlobalKeyFixture.Set(server, Change, "playerdamage", "5 0"));
        Assert.Equal(0, world.Server.Count("cli_extension mymod.testing/globalkey"));
    }

    [Fact] public void ComparisonIsExactAndOrdinal()
    {
        Assert.True(GlobalKeyFixture.Compare(["a", "b"], ["b", "a"]).Same);
        var difference = GlobalKeyFixture.Compare(["playerdamage 50"], ["playerdamage 25", "Extra"]);
        Assert.Equal(new[] { "playerdamage 50" }, difference.MissingOnClient);
        Assert.Equal(new[] { "Extra", "playerdamage 25" }, difference.ExtraOnClient);
    }
}
