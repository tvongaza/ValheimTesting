using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The hot-reload steps against a scripted extension listing that changes as a reloader would change it.
public class ExtensionReloadTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private static (ScriptedTransport Transport, Func<int> Reads) Listings(params (string Id, string Version, string Instance, bool Closing)[][] listings)
    {
        int reads = 0;
        var transport = new ScriptedTransport().On("cli_extensions", _ =>
        {
            var current = listings[Math.Min(reads++, listings.Length - 1)];
            return ScriptedTransport.Ok("EXTENSIONS " + JsonSerializer.Serialize(new
            {
                apiVersion = 1,
                extensions = current.Select(e => new { id = e.Id, version = e.Version, instance = e.Instance, closing = e.Closing, commands = Array.Empty<object>() }).ToArray(),
            }));
        });
        return (transport, () => reads);
    }

    [Fact] public async Task TheReplacementIsANewInstanceAtTheNewVersion()
    {
        var (transport, reads) = Listings(
            [("example.probe", "0.1.0", "a1", false)],
            [("example.probe", "0.1.0", "a1", true)],          // A draining: not live.
            [("example.probe", "0.2.0", "a1", false)],          // Same instance: not a replacement, whatever it says.
            [("other.ext", "1.0", "o1", false), ("example.probe", "0.2.0", "b1", false)]);
        using var actor = transport.Actor();
        Assert.Equal(new ExtensionInstance("example.probe", "0.1.0", "a1"), ExtensionReload.Find(actor, "example.probe"));
        var replaced = await ExtensionReload.WaitForReplacement(actor, "example.probe", "0.2.0", "a1", TimeSpan.FromSeconds(10), Tick);
        Assert.Equal(new ExtensionInstance("example.probe", "0.2.0", "b1"), replaced);
        Assert.Equal(4, reads());
    }

    [Fact] public async Task ANeverReplacedExtensionTimesOutNamingWhatWasLastSeen()
    {
        var (transport, _) = Listings([("example.probe", "0.1.0", "a1", false)]);
        using var actor = transport.Actor();
        var error = await Assert.ThrowsAsync<WaitTimeoutException>(() => ExtensionReload.WaitForReplacement(actor, "example.probe", "0.2.0", "a1", TimeSpan.FromMilliseconds(50), Tick));
        Assert.Equal("example.probe 0.1.0 instance a1", error.LastSeen);
    }

    [Fact] public async Task RemovalWaitsUntilNoLiveRegistrationRemains()
    {
        var (transport, reads) = Listings(
            [("example.probe", "0.2.0", "b1", false)],
            [("example.probe", "0.2.0", "b1", true)]);
        using var actor = transport.Actor();
        await ExtensionReload.WaitForRemoval(actor, "example.probe", TimeSpan.FromSeconds(10), Tick);
        Assert.Equal(2, reads());
    }

    [Fact] public void TwoLiveRegistrationsOfOneIdAreAnError()
    {
        var (transport, _) = Listings([("example.probe", "0.1.0", "a1", false), ("example.probe", "0.2.0", "b1", false)]);
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => ExtensionReload.Find(actor, "example.probe"));
    }

    [Fact] public void InstallReplacesTheDeployedFileWholeAndLeavesNothingBeside()
    {
        string directory = Directory.CreateTempSubdirectory("extension-reload-").FullName;
        try
        {
            string artifact = Path.Combine(directory, "artifact.dll"), deployed = Path.Combine(directory, "scripts", "Probe.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(deployed)!);
            File.WriteAllText(deployed, "A");
            File.WriteAllText(artifact, "B");
            ExtensionReload.Install(artifact, deployed);
            Assert.Equal("B", File.ReadAllText(deployed));
            Assert.Equal(deployed, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(deployed)!)));
            Assert.Equal("B", File.ReadAllText(artifact));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
