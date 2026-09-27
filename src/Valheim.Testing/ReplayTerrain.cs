using System;
using System.Collections.Generic;

namespace Valheim.Testing;
// Exact sample lookup deliberately refuses interpolation or unknown input layers.
public sealed class ReplayTerrain : ITerrain
{
    private readonly Dictionary<(float x, float z), TerrainSample> _samples = new Dictionary<(float, float), TerrainSample>();
    public string Provenance { get; }
    public string Layer { get; }
    public ReplayTerrain(string provenance, string layer, IEnumerable<TerrainSample> samples)
    {
        if (string.IsNullOrWhiteSpace(provenance) || string.IsNullOrWhiteSpace(layer)) throw new ArgumentException("Provenance and terrain layer are required.");
        Provenance = provenance; Layer = layer;
        foreach (var sample in samples)
        {
            if (!Finite(sample.X) || !Finite(sample.Z) || !Finite(sample.Height) || !Finite(sample.RiverWeight) || !Finite(sample.RiverWidth)) throw new ArgumentException("Non-finite terrain sample.");
            if (_samples.ContainsKey((sample.X, sample.Z))) throw new ArgumentException("Duplicate sample coordinates.");
            _samples.Add((sample.X, sample.Z), sample);
        }
        if (_samples.Count == 0) throw new ArgumentException("Replay has no samples.");
    }
    private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    private TerrainSample At(float x, float z) => _samples.TryGetValue((x, z), out var sample) ? sample : throw new InvalidOperationException("Uncaptured terrain coordinate: " + x + "," + z);
    public float GetHeight(float x, float z) => At(x, z).Height;
    public TerrainBiome GetBiome(float x, float z)
    {
        var biome = At(x, z).Biome;
        return biome != TerrainBiome.Unknown ? biome : throw new NotSupportedException("Biome was not captured at this coordinate.");
    }
    public void GetRiverWeight(float x, float z, out float weight, out float width)
    {
        var sample = At(x, z);
        if (!sample.HasRiverSample) throw new NotSupportedException("River data was not captured at this coordinate.");
        weight = sample.RiverWeight; width = sample.RiverWidth;
    }
}
public sealed class TerrainSample
{
    public float X { get; }
    public float Z { get; }
    public float Height { get; }
    public TerrainBiome Biome { get; }
    public bool HasRiverSample { get; }
    public float RiverWeight { get; }
    public float RiverWidth { get; }
    public TerrainSample(float x, float z, float height, TerrainBiome biome, float? riverWeight = null, float? riverWidth = null)
    {
        if (riverWeight.HasValue != riverWidth.HasValue) throw new ArgumentException("River weight and width must be captured together.");
        X = x; Z = z; Height = height; Biome = biome; HasRiverSample = riverWeight.HasValue;
        RiverWeight = riverWeight.GetValueOrDefault(); RiverWidth = riverWidth.GetValueOrDefault();
    }
}
