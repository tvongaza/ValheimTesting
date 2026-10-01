using System;

namespace Valheim.Testing;

/// <summary>A declared region replacing all terrain facts within its boundary.</summary>
public sealed class TerrainRegion
{
    private readonly Func<float, float, bool> _contains;
    public ITerrain Terrain { get; }
    public TerrainRegion(Func<float, float, bool> contains, ITerrain terrain)
    {
        _contains = contains ?? throw new ArgumentNullException(nameof(contains));
        Terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
    }
    public bool Contains(float x, float z) => _contains(x, z);

    /// <summary>Half-open bounds: minimum included, maximum excluded.</summary>
    public static TerrainRegion Rectangle(float minX, float minZ, float maxX, float maxZ, ITerrain terrain)
    {
        GridValues.Finite(minX); GridValues.Finite(minZ); GridValues.Finite(maxX); GridValues.Finite(maxZ);
        if (maxX <= minX || maxZ <= minZ) throw new ArgumentException("Region must have positive area.");
        return new TerrainRegion((x, z) => x >= minX && x < maxX && z >= minZ && z < maxZ, terrain);
    }
}

/// <summary>
/// Ordered fixture composition. Last matching region wins for height, biome and
/// river facts together. This is explicit replacement, not native biome blending.
/// Use pure terrain inputs and predicates when sharing a fixture across workers.
/// </summary>
/// <example>
/// Give a real mod algorithm a declared slope with a flat, half-open terrace; assert the result independently in the
/// mod's test project. No Unity process or game data is involved:
/// <code>
/// ITerrain terrain = new CompositeTerrain(
///     new PlaneTerrain(40f, 0.25f, -0.5f),
///     TerrainRegion.Rectangle(4f, -2f, 8f, 2f, new PlaneTerrain(60f)));
/// float terraceHeight = terrain.GetHeight(4f, -2f); // 60; minimum edge included
/// float outsideHeight = terrain.GetHeight(8f, 0f);  // 42; maximum edge excluded
/// </code>
/// See the <see href="https://github.com/tvongaza/ValheimTesting/blob/main/examples/SharedWorld/Program.cs">runnable shared-world example</see>
/// for a complete pure test fixture.
/// </example>
public sealed class CompositeTerrain : ITerrain
{
    private readonly ITerrain _background;
    private readonly TerrainRegion[] _regions;
    public CompositeTerrain(ITerrain background, params TerrainRegion[] regions)
    {
        _background = background ?? throw new ArgumentNullException(nameof(background));
        _regions = (TerrainRegion[])(regions ?? throw new ArgumentNullException(nameof(regions))).Clone();
        foreach (var region in _regions) if (region == null) throw new ArgumentException("Null region.", nameof(regions));
    }
    private ITerrain At(float x, float z)
    {
        GridValues.Finite(x); GridValues.Finite(z);
        for (int i = _regions.Length - 1; i >= 0; i--) if (_regions[i].Contains(x, z)) return _regions[i].Terrain;
        return _background;
    }
    public float GetHeight(float x, float z) => At(x, z).GetHeight(x, z);
    public TerrainBiome GetBiome(float x, float z) => At(x, z).GetBiome(x, z);
    public void GetRiverWeight(float x, float z, out float weight, out float width) => At(x, z).GetRiverWeight(x, z, out weight, out width);
}
