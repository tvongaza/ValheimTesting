using System;
using System.Collections.Generic;

namespace Valheim.Testing;

internal static class GridValues
{
    internal static void Finite(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "A finite value is required.");
    }
}

/// <summary>Raw, linear RGBA fixture values; no quantization or game paint semantics.</summary>
public readonly struct PaintRgba : IEquatable<PaintRgba>
{
    public float R { get; }
    public float G { get; }
    public float B { get; }
    public float A { get; }
    public PaintRgba(float r, float g, float b, float a)
    {
        foreach (float value in new[] { r, g, b, a })
        {
            GridValues.Finite(value);
            if (value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(r), "RGBA must be within [0,1].");
        }
        R = r; G = g; B = b; A = a;
    }
    public bool Equals(PaintRgba other) => R == other.R && G == other.G && B == other.B && A == other.A;
    public override bool Equals(object? obj) => obj is PaintRgba other && Equals(other);
    public override int GetHashCode() => R.GetHashCode() ^ G.GetHashCode() ^ B.GetHashCode() ^ A.GetHashCode();
}

/// <summary>
/// Row-major declared samples, x fastest. Origin is the first sample's world
/// x/z, not a zone centre. No interpolation, loading or implicit edge updates.
/// T must be a value-only sample type (the supplied uses are float and PaintRgba).
/// </summary>
public sealed class TerrainGrid<T> where T : struct
{
    private readonly T[] _values;
    public int CountX { get; }
    public int CountZ { get; }
    public float OriginX { get; }
    public float OriginZ { get; }
    public float Spacing { get; }
    public TerrainGrid(int countX, int countZ, float originX, float originZ, float spacing, Func<float, float, T> sample)
    {
        if (countX <= 0 || countZ <= 0) throw new ArgumentOutOfRangeException(nameof(countX));
        GridValues.Finite(originX); GridValues.Finite(originZ); GridValues.Finite(spacing);
        if (spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing));
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        CountX = countX; CountZ = countZ; OriginX = originX; OriginZ = originZ; Spacing = spacing;
        GridValues.Finite(WorldX(countX - 1)); GridValues.Finite(WorldZ(countZ - 1));
        _values = new T[checked(countX * countZ)];
        for (int z = 0; z < countZ; z++) for (int x = 0; x < countX; x++) _values[z * countX + x] = sample(WorldX(x), WorldZ(z));
    }
    private TerrainGrid(TerrainGrid<T> source)
    {
        CountX = source.CountX; CountZ = source.CountZ; OriginX = source.OriginX; OriginZ = source.OriginZ; Spacing = source.Spacing;
        _values = (T[])source._values.Clone();
    }
    private static void Check(int index, int count)
    {
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
    }
    public float WorldX(int x) { Check(x, CountX); return OriginX + x * Spacing; }
    public float WorldZ(int z) { Check(z, CountZ); return OriginZ + z * Spacing; }
    private int Index(int x, int z) { Check(x, CountX); Check(z, CountZ); return z * CountX + x; }
    public T this[int x, int z] { get => _values[Index(x, z)]; set => _values[Index(x, z)] = value; }
    public TerrainGrid<T> Clone() => new TerrainGrid<T>(this);
}

/// <summary>
/// A test-owned state's absolute heights and independently sized paint samples.
/// Mod adapters decide how these map to compiler deltas, masks and native textures.
/// </summary>
public sealed class TerrainZoneState
{
    public TerrainGrid<float> Heights { get; }
    public TerrainGrid<PaintRgba> Paint { get; }
    public TerrainZoneState(TerrainGrid<float> heights, TerrainGrid<PaintRgba> paint)
    {
        Heights = heights ?? throw new ArgumentNullException(nameof(heights));
        Paint = paint ?? throw new ArgumentNullException(nameof(paint));
    }
    public TerrainZoneState Clone() => new TerrainZoneState(Heights.Clone(), Paint.Clone());
}

/// <summary>
/// Explicitly loaded test zones. Missing zones throw; duplicated boundary vertices
/// remain independent so a one-zone-only write can be detected. Clone is an in-memory
/// fixture snapshot, not a model of Valheim saving, network replication or physics.
/// </summary>
public sealed class TerrainWorldState
{
    private readonly Dictionary<(int x, int z), TerrainZoneState> _zones = new Dictionary<(int, int), TerrainZoneState>();
    public int Count => _zones.Count;
    public void Add(int x, int z, TerrainZoneState zone) => _zones.Add((x, z), zone ?? throw new ArgumentNullException(nameof(zone)));
    public TerrainZoneState this[int x, int z] => _zones[(x, z)];
    public TerrainWorldState Clone()
    {
        var copy = new TerrainWorldState();
        foreach (var pair in _zones) copy.Add(pair.Key.x, pair.Key.z, pair.Value.Clone());
        return copy;
    }
}
