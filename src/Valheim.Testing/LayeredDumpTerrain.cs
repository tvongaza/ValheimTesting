using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Valheim.Testing;

/// <summary>A named, pinned native CSV layer. The path may be local; the manifest retains its native provenance.</summary>
public sealed class WorldDumpLayerSpec
{
    public string Name { get; }
    public string Path { get; }
    public WorldDumpManifest Manifest { get; }

    public WorldDumpLayerSpec(string name, string path, WorldDumpManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A layer needs a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A layer needs a file path.", nameof(path));
        Name = name; Path = path; Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }
}

/// <summary>The layer and sampling rule that answered one query. An interpolated answer is approximate.</summary>
public sealed class WorldDumpSource
{
    public string Layer { get; }
    public float Spacing { get; }
    public bool ExactNode { get; }
    internal WorldDumpSource(string layer, float spacing, bool exactNode)
    { Layer = layer; Spacing = spacing; ExactNode = exactNode; }
}

/// <summary>
/// Read-only composition of pinned native world dumps. Ordinary terrain facts use the finest containing grid;
/// base height uses only the explicitly designated lattice, even where another finer grid contains the point.
/// Equal-spacing grids may not overlap, including at an edge. Outside every containing layer, queries fail.
/// Node values retain the CSV precision; between-node heights and river fields are bilinear approximations,
/// while biomes use the selected grid's nearest node. Safe for concurrent readers after loading.
/// </summary>
public sealed class LayeredDumpTerrain : ITerrain
{
    private sealed class Layer
    {
        public string Name { get; }
        public GridDumpTerrain Grid { get; }
        public Layer(string name, GridDumpTerrain grid) { Name = name; Grid = grid; }
        public WorldDumpSource Describe(float x, float z) => new WorldDumpSource(Name, Grid.Spacing, Grid.IsSampleNode(x, z));
    }

    private readonly Layer[] _layers;
    private readonly Layer _baseHeight;

    private LayeredDumpTerrain(Layer[] layers, Layer baseHeight)
    { _layers = layers; _baseHeight = baseHeight; }

    /// <summary>
    /// Verifies every file and manifest before composing them. Different world IDs, seeds or game builds fail;
    /// overlap between layers of equal resolution is ambiguous and fails. At least one layer is required.
    /// </summary>
    public static LayeredDumpTerrain Load(IEnumerable<WorldDumpLayerSpec> specs, string baseHeightLayer)
    {
        if (specs == null) throw new ArgumentNullException(nameof(specs));
        if (string.IsNullOrWhiteSpace(baseHeightLayer)) throw new ArgumentException("Designate one base-height layer.", nameof(baseHeightLayer));
        var entries = specs.ToArray();
        if (entries.Length == 0) throw new ArgumentException("At least one dump layer is required.", nameof(specs));
        if (entries.Any(e => e == null)) throw new ArgumentException("A dump layer cannot be null.", nameof(specs));
        if (entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("Dump layer names must be unique.");
        var loaded = new List<Layer>();
        foreach (var spec in entries)
        {
            WorldDumpContract.RequireSameWorld(entries[0].Manifest, spec.Manifest);
            loaded.Add(new Layer(spec.Name, WorldDumpContract.Verify(spec.Path, spec.Manifest)));
        }
        for (int i = 0; i < loaded.Count; i++)
            for (int j = i + 1; j < loaded.Count; j++)
                if (loaded[i].Grid.Spacing == loaded[j].Grid.Spacing && Overlaps(loaded[i].Grid, loaded[j].Grid))
                    throw new InvalidDataException($"Dump layers '{loaded[i].Name}' and '{loaded[j].Name}' overlap at the same spacing; no precedence is declared.");
        Layer? baseLayer = loaded.SingleOrDefault(layer => layer.Name == baseHeightLayer);
        if (baseLayer == null) throw new InvalidDataException($"Base-height layer '{baseHeightLayer}' was not supplied.");
        if (!baseLayer.Grid.HasBaseHeight) throw new InvalidDataException($"Base-height layer '{baseHeightLayer}' lacks base_height.");
        return new LayeredDumpTerrain(loaded.OrderBy(l => l.Grid.Spacing).ThenBy(l => l.Name, StringComparer.Ordinal).ToArray(), baseLayer);
    }

    private static bool Overlaps(GridDumpTerrain a, GridDumpTerrain b) =>
        a.OriginX <= b.MaxX && b.OriginX <= a.MaxX && a.OriginZ <= b.MaxZ && b.OriginZ <= a.MaxZ;

    private Layer Select(float x, float z)
    {
        foreach (var layer in _layers) if (layer.Grid.Contains(x, z)) return layer;
        throw new ArgumentOutOfRangeException(nameof(x), $"({x},{z}) lies outside all pinned dump layers.");
    }

    private Layer Base(float x, float z)
    {
        if (!_baseHeight.Grid.Contains(x, z))
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{z}) lies outside the designated base-height layer '{_baseHeight.Name}'.");
        return _baseHeight;
    }

    /// <summary>Names the finest containing layer and whether this coordinate is one of its exact CSV nodes.</summary>
    public WorldDumpSource SourceAt(float x, float z) => Select(x, z).Describe(x, z);

    /// <summary>Names the designated base-height layer and whether this coordinate is one of its exact CSV nodes.</summary>
    public WorldDumpSource BaseHeightSourceAt(float x, float z) => Base(x, z).Describe(x, z);

    public float GetHeight(float x, float z) => Select(x, z).Grid.GetHeight(x, z);
    public TerrainBiome GetBiome(float x, float z) => Select(x, z).Grid.GetBiome(x, z);
    public void GetRiverWeight(float x, float z, out float weight, out float width) =>
        Select(x, z).Grid.GetRiverWeight(x, z, out weight, out width);

    /// <summary>Uses only the designated base-height lattice; never treats the finer ground-height grid as its source.</summary>
    public float GetBaseHeight(float x, float z) => Base(x, z).Grid.GetBaseHeight(x, z);
}
