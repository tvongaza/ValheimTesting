using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

public sealed class ObserveNativeReplyTests
{
    // Data from the Observe pack on a disposable Mac dedicated server, 2026-10-08.
    // The server had no joined player, so zone 0,0 was not loaded. No world identity is in this fixture.
    [Fact]
    public void ZoneReaderAcceptsTheRealServerReply()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Observe", "zones-server.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        ZoneReading reading = ZoneReading.Parse(document.RootElement, [new ZoneId(0, 0)]);

        Assert.Equal(new ZoneId(15625, 15625), reading.Reference);
        Assert.False(reading.Zones[0].AreaReady);
        Assert.True(reading.Zones[0].Unloaded);
        Assert.Equal(0, reading.Zones[0].WithoutInstance);
    }
}
