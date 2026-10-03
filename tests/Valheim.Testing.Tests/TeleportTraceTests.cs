using Valheim.Testing.Game;
using Xunit;

public sealed class TeleportTraceTests
{
    private const string Complete = "OK: TELEPORT_TRACE id=7 distant=True requestedMs=0 movedMs=2000 areaReadyMs=3500 floorReadyMs=3600 doneMs=3620 floorAtDone=True final=100.25,42.5,-40";

    [Fact] public void ReadsEveryPhaseAndKeepsTheOriginalReply()
    {
        var trace = TeleportTrace.Parse(Complete, 7);
        Assert.Equal(7, trace.Id);
        Assert.True(trace.Distant);
        Assert.Equal((0, 2000, 3500, 3600, 3620),
            (trace.RequestedMs, trace.MovedMs, trace.AreaReadyMs, trace.FloorReadyMs, trace.DoneMs));
        Assert.Equal((100.25f, 42.5f, -40f), (trace.FinalX, trace.FinalY, trace.FinalZ));
        Assert.True(trace.FloorAtDone);
        Assert.Equal(Complete, trace.Raw);
    }

    [Theory]
    [InlineData("id=8", "identity")]
    [InlineData("id=bad", "identity")]
    [InlineData("movedMs=nan", "movedMs")]
    [InlineData("floorReadyMs=-2", "phase")]
    [InlineData("doneMs=100", "phase")]
    [InlineData("final=NaN,42.5,-40", "final")]
    [InlineData("floorAtDone=False", "phase")]
    public void RefusesWrongOrContradictoryValues(string replacement, string message)
    {
        string key = replacement[..replacement.IndexOf('=')];
        string old = Complete.Split(' ').Single(part => part.StartsWith(key + "=", StringComparison.Ordinal));
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() =>
            TeleportTrace.Parse(Complete.Replace(old, replacement, StringComparison.Ordinal), 7)).Message);
    }

    [Fact] public void RefusesSubstringSpoofingMissingAndDuplicateFields()
    {
        Assert.Throws<InvalidDataException>(() => TeleportTrace.Parse(Complete.Replace("floorAtDone=True", "notfloorAtDone=True"), 7));
        Assert.Throws<InvalidDataException>(() => TeleportTrace.Parse(Complete + " floorAtDone=True", 7));
        Assert.Throws<InvalidDataException>(() => TeleportTrace.Parse(Complete.Replace(" movedMs=2000", ""), 7));
    }
}
