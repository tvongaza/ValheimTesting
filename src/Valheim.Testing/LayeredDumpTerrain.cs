using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Valheim.Testing;

/// <summary>
/// Read-only composition of pinned native world dumps. Ordinary terrain facts use the finest containing grid;
/// base height uses only the explicitly designated lattice, even where another finer grid contains the point.
/// Equal-spacing grids may not overlap, including at an edge. Outside every containing layer, queries fail.
/// Node values retain the CSV precision; between-node heights and river fields are bilinear approximations,
/// while biomes use the selected grid's nearest node. Safe for concurrent readers after loading.
/// </summary>
public sealed class LayeredDumpTerrain : ITerrain
{
    private readonly GridDumpTerrain[] _layers;

    private LayeredDumpTerrain(GridDumpTerrain[] layers, GridDumpTerrain baseHeight)
    { _layers = layers; BaseHeightLayer = baseHeight; }

    /// <summary>
    /// Verifies every manifest's file (<see cref="WorldDumpManifest.Verify"/>) before composing them. Different world
    /// IDs, seeds or game builds fail; overlap between layers of equal resolution is ambiguous and fails. At least one
    /// layer is required, and <paramref name="baseHeightLayer"/> must name one of them.
    /// </summary>
    public static LayeredDumpTerrain Load(IEnumerable<WorldDumpManifest> layers, string baseHeightLayer)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));
        if (string.IsNullOrWhiteSpace(baseHeightLayer)) throw new ArgumentException("Designate one base-height layer.", nameof(baseHeightLayer));
        var manifests = layers.ToArray();
        if (manifests.Length == 0) throw new ArgumentException("At least one dump layer is required.", nameof(layers));
        if (manifests.Any(m => m == null)) throw new ArgumentException("A dump layer cannot be null.", nameof(layers));
        if (manifests.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != manifests.Length)
            throw new InvalidDataException("Dump layer names must be unique.");
        var loaded = new List<GridDumpTerrain>();
        foreach (var manifest in manifests)
        {
            manifests[0].RequireSameWorld(manifest);
            loaded.Add(manifest.Verify());
        }
        for (int i = 0; i < loaded.Count; i++)
            for (int j = i + 1; j < loaded.Count; j++)
                if (loaded[i].Spacing == loaded[j].Spacing && Overlaps(loaded[i], loaded[j]))
                    throw new InvalidDataException($"Dump layers '{loaded[i].Provenance}' and '{loaded[j].Provenance}' overlap at the same spacing; no precedence is declared.");
        GridDumpTerrain? baseLayer = loaded.SingleOrDefault(layer => layer.Provenance == baseHeightLayer);
        if (baseLayer == null) throw new InvalidDataException($"Base-height layer '{baseHeightLayer}' was not supplied.");
        return new LayeredDumpTerrain(loaded.OrderBy(l => l.Spacing).ThenBy(l => l.Provenance, StringComparer.Ordinal).ToArray(), baseLayer);
    }

    private static bool Overlaps(GridDumpTerrain a, GridDumpTerrain b) =>
        a.OriginX <= b.MaxX && b.OriginX <= a.MaxX && a.OriginZ <= b.MaxZ && b.OriginZ <= a.MaxZ;

    /// <summary>
    /// The finest layer containing the point: the grid that answers height, biome and river here. Its
    /// <see cref="GridDumpTerrain.Provenance"/> is the layer's name, and <see cref="GridDumpTerrain.IsSampleNode"/>
    /// says whether the point is one of its exact CSV nodes rather than an interpolation.
    /// </summary>
    public GridDumpTerrain LayerAt(float x, float z)
    {
        foreach (var layer in _layers) if (layer.Contains(x, z)) return layer;
        throw new ArgumentOutOfRangeException(nameof(x), $"({x},{z}) lies outside all pinned dump layers.");
    }

    /// <summary>The designated base-height lattice: the only layer <see cref="GetBaseHeight"/> reads.</summary>
    public GridDumpTerrain BaseHeightLayer { get; }

    public float GetHeight(float x, float z) => LayerAt(x, z).GetHeight(x, z);
    public TerrainBiome GetBiome(float x, float z) => LayerAt(x, z).GetBiome(x, z);
    public void GetRiverWeight(float x, float z, out float weight, out float width) =>
        LayerAt(x, z).GetRiverWeight(x, z, out weight, out width);

    /// <summary>Uses only the designated base-height lattice; never treats the finer ground-height grid as its source.</summary>
    public float GetBaseHeight(float x, float z)
    {
        if (!BaseHeightLayer.Contains(x, z))
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{z}) lies outside the designated base-height layer '{BaseHeightLayer.Provenance}'.");
        return BaseHeightLayer.GetBaseHeight(x, z);
    }
}
