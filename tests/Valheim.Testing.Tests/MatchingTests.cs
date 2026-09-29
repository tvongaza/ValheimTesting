using Valheim.Testing.Game;
using Xunit;

public class MatchingTests
{
    private static PlacedObject At(double y = 0, double yawDegrees = 0, int prefab = 10)
    {
        double half = yawDegrees * Math.PI / 360;
        return new(prefab, [1, y, 3], [0, Math.Sin(half), 0, Math.Cos(half)]);
    }
    [Fact] public void StackedObjectsMatchByTheirFullTransform() => TransformMatch.Match([At(y: 3), At(y: 0)], [At(y: 0), At(y: 3)], .05, 1);
    [Fact] public void ADuplicateCannotStandInForAMissingObject() =>
        Assert.Throws<InvalidOperationException>(() => TransformMatch.Match([At(), At()], [At(), At(y: 3)], .05, 1));
    [Fact] public void ARotatedObjectCannotMatchByPositionAlone() =>
        Assert.Throws<InvalidOperationException>(() => TransformMatch.Match([At(yawDegrees: 90)], [At()], .05, 1));
    [Fact] public void SmallRotationNoiseAndTheOppositeQuaternionAreAccepted()
    {
        TransformMatch.Match([At(yawDegrees: .8)], [At()], .05, 1);
        var flipped = new PlacedObject(10, [1, 0, 3], [0, 0, 0, -1]);
        TransformMatch.Match([flipped], [At()], .05, 1);
    }
    [Fact] public void AGreedyFirstFitWouldFailWhereTheMatchingSucceeds() =>
        TransformMatch.Match([At(y: .04), At(y: -.04)], [At(), At(y: .08)], .05, 1);
    [Fact] public void CountsPrefabsAndTransformsAreChecked()
    {
        Assert.Throws<InvalidOperationException>(() => TransformMatch.Match([At()], [At(), At(y: 3)], .05, 1));
        Assert.Throws<InvalidOperationException>(() => TransformMatch.Match([At(prefab: 11)], [At()], .05, 1));
        Assert.Throws<InvalidOperationException>(() => TransformMatch.Match([new PlacedObject(10, [0, 0], [0, 0, 0, 1])], [At()], .05, 1));
        Assert.Throws<ArgumentException>(() => TransformMatch.Match([At()], [At()], -1, 1));
    }
    [Fact] public void ExactCoveragePairsEveryRowWithOneExpectedIdentity()
    {
        var expected = new Dictionary<(int, int), float> { [(0, 0)] = 1f, [(0, 1)] = 2f };
        var pairs = Coverage.Exact(expected, new[] { (X: 0, Z: 1, H: 2.1f), (X: 0, Z: 0, H: .9f) }, r => (r.X, r.Z));
        Assert.Equal(new[] { 2f, 1f }, pairs.Select(p => p.Expected));
        Assert.Throws<InvalidOperationException>(() => Coverage.Exact(expected, new[] { (X: 0, Z: 0), (X: 0, Z: 0) }, r => (r.X, r.Z)));
        Assert.Throws<InvalidOperationException>(() => Coverage.Exact(expected, new[] { (X: 0, Z: 0), (X: 5, Z: 5) }, r => (r.X, r.Z)));
        Assert.Throws<InvalidOperationException>(() => Coverage.Exact(expected, new[] { (X: 0, Z: 0) }, r => (r.X, r.Z)));
        Assert.Equal(2, Coverage.Exact(new[] { "a", "b" }, new[] { "b", "a" }, r => r).Count);
    }
}
