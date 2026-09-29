using System;
using MyMod;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

// The game doubles use process-wide singletons, just as the game does.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class DrySiteTests
{
    [Theory]
    [InlineData(29f, false)]   // Under water.
    [InlineData(31.5f, true)]  // Exactly the promised minimum clearance.
    [InlineData(40f, true)]   // Safely above it.
    public void RequiresGroundAtLeastClearanceAboveWater(float ground, bool expected)
    {
        using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(ground));
        Assert.Equal(expected, DrySiteRule.CanPlace(new Vector3(0, 100, 0), 30f, 1.5f));
    }

    [Fact]
    public void SamplesHorizontalXZInsteadOfObjectHeight()
    {
        // Height = 40 + x/4 - z/2. At (8, 10) the ground is 37, not y=100.
        using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(40, .25f, -.5f));
        Assert.False(DrySiteRule.CanPlace(new Vector3(8, 100, 10), 36f, 1.5f));
        Assert.True(DrySiteRule.CanPlace(new Vector3(8, 100, 10), 35f, 1.5f));
    }

    [Fact]
    public void TemporaryFixtureRestoresThePreviousWorldEvenOnFailure()
    {
        using var outer = new ValheimWorldScope().WithTerrain(new PlaneTerrain(40));
        var previous = WorldGenerator.instance;
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var inner = new ValheimWorldScope().WithTerrain(new PlaneTerrain(29));
            Assert.False(DrySiteRule.CanPlace(new Vector3(0, 0, 0), 30f, 1.5f));
            throw new InvalidOperationException("Example scenario failure");
        }));
        Assert.Same(previous, WorldGenerator.instance);
        Assert.True(DrySiteRule.CanPlace(new Vector3(0, 0, 0), 30f, 1.5f));
    }
}
