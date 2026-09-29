using Valheim.Testing.Game;
using Xunit;

public sealed class ClientPlanTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "client-plan-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() => File.Delete(_path);

    [Fact] public void ASurfacePlanRoundTripsAndTheSupportPointIsOptional()
    {
        new SurfacePlan { ExpectedFrom = "declared fixture", Samples = [new(10, 20, 31.5f)], Support = new(10, 20, 31.5f) }.Write(_path);
        var plan = SurfacePlan.Read(_path);
        Assert.Equal(31.5f, Assert.Single(plan.Samples).Height); Assert.Equal(new HeightExpectation(10, 20, 31.5f), plan.Support);
        new SurfacePlan { ExpectedFrom = "declared fixture", Samples = [new(10, 20, 31.5f)] }.Write(_path);
        Assert.Null(SurfacePlan.Read(_path).Support);
        Assert.DoesNotContain("support", File.ReadAllText(_path));
    }
    [Fact] public void PlansAreReadStrictly()
    {
        File.WriteAllText(_path, """{"expectedFrom":"x","layer":"loaded-ground","tolerance":0.05,"samples":[{"x":1,"z":2,"height":3}]}""");
        Assert.ThrowsAny<Exception>(() => SurfacePlan.Read(_path));
        Assert.Throws<ArgumentException>(() => new SurfacePlan { ExpectedFrom = "x", Samples = [new(1.5f, 2, 3)] }.Write(_path));
    }
    [Fact] public void APaintPlanRoundTrips()
    {
        new PaintPlan { ExpectedFrom = "declared paint", Samples = [new(4, 8, 1, 0, 0, .3f)] }.Write(_path);
        Assert.Equal(.3f, Assert.Single(PaintPlan.Read(_path).Samples).A);
    }
}
