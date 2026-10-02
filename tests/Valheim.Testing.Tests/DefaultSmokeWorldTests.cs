using Valheim.Testing.Game;
using Xunit;

public sealed class DefaultSmokeWorldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-default-smoke-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void PreparedWorldIsPinnedAndDoesNotReplaceAnExistingRun()
    {
        string first = Path.Combine(_root, "first"), second = Path.Combine(_root, "second");
        var one = DefaultSmokeWorld.Prepare(first);
        var two = DefaultSmokeWorld.Prepare(second);
        Assert.Equal(DefaultSmokeWorld.Name, one.Name);
        Assert.Equal(DefaultSmokeWorld.Uid, one.UidText);
        Assert.Equal(DefaultSmokeWorld.SeedName, one.SeedName);
        Assert.Equal(one, two with { File = one.File });
        var hashes = WorldFixture.Manifest(first);
        Assert.Equal(5, hashes.Count);
        Assert.Equal(hashes.OrderBy(item => item.Key), WorldFixture.Manifest(second).OrderBy(item => item.Key));
        Assert.Throws<IOException>(() => DefaultSmokeWorld.Prepare(first));
        WorldFixture.Verify(first, hashes);
    }

    [Fact]
    public void WrongUidAndAlteredFixtureAreRefusedBeforeAClientLaunch()
    {
        string fixture = Path.Combine(_root, "world");
        DefaultSmokeWorld.Prepare(fixture);
        Assert.Contains("another world", Assert.Throws<InvalidOperationException>(() =>
            FixtureLayout.Discover(fixture, "12345")).Message);
        var hashes = WorldFixture.Manifest(fixture);
        string data = Path.Combine(fixture, DefaultSmokeWorld.Name, "_main.1.db2");
        using (var stream = new FileStream(data, FileMode.Append)) stream.WriteByte(1);
        Assert.Throws<InvalidOperationException>(() => WorldFixture.Verify(fixture, hashes));
    }
}
