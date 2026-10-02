using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Valheim.Testing;

/// <summary>
/// Terrain read from an evenly spaced grid of samples, such as a CSV world dump.
/// Nodes sit at <c>OriginX + i * Spacing</c>, <c>OriginZ + j * Spacing</c>; the covered area is the closed
/// rectangle from the origin to <see cref="MaxX"/>/<see cref="MaxZ"/>, so the last row and column are inside.
/// <list type="bullet">
/// <item>Height, river weight and river width: at a node, the node's value unchanged (a query within 1e-4 of a
/// cell of a node is that node); between nodes, bilinear between the four surrounding nodes. Bilinear is an
/// approximation of the dumped world between its samples, not a replay.</item>
/// <item>Biome: the nearest node's label, never an average. A node owns the half-open cell from half a spacing
/// below it to half a spacing above it on each axis, so a query exactly halfway takes the node at larger x (or z).</item>
/// <item>Outside the covered area every query throws <see cref="ArgumentOutOfRangeException"/>; there is no edge
/// clamping or extrapolation. A missing biome or river layer throws <see cref="NotSupportedException"/>.</item>
/// </list>
/// Immutable after construction and safe for concurrent readers.
/// </summary>
public sealed class GridDumpTerrain : ITerrain
{
    private const double NodeSnap = 1e-4;
    private readonly float[] _heights;
    private readonly float[]? _baseHeights;
    private readonly TerrainBiome[]? _biomes;
    private readonly float[]? _riverWeights;
    private readonly float[]? _riverWidths;

    public string Provenance { get; }
    public float OriginX { get; }
    public float OriginZ { get; }
    public float Spacing { get; }
    public int CountX { get; }
    public int CountZ { get; }
    public float MaxX => (float)(OriginX + (double)(CountX - 1) * Spacing);
    public float MaxZ => (float)(OriginZ + (double)(CountZ - 1) * Spacing);
    public bool HasBiome => _biomes != null;
    public bool HasRiver => _riverWeights != null;
    /// <summary>Whether the dump includes Valheim's unitless `GetBaseHeight` samples, distinct from metre-valued height.</summary>
    public bool HasBaseHeight => _baseHeights != null;

    /// <summary>
    /// Declares a grid directly. Arrays are row-major with x fastest (<c>index = j * countX + i</c>) and are copied.
    /// River weight and width are given together or not at all. A biome layer must name a biome at every node.
    /// </summary>
    public GridDumpTerrain(string provenance, float originX, float originZ, float spacing, int countX, int countZ,
        float[] heights, TerrainBiome[]? biomes = null, float[]? riverWeights = null, float[]? riverWidths = null,
        float[]? baseHeights = null)
    {
        if (string.IsNullOrWhiteSpace(provenance)) throw new ArgumentException("Provenance is required.", nameof(provenance));
        GridValues.Finite(originX); GridValues.Finite(originZ); GridValues.Finite(spacing);
        if (spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing must be positive.");
        if (countX < 2 || countZ < 2) throw new ArgumentOutOfRangeException(nameof(countX), "A grid needs at least two nodes on each axis.");
        if (heights == null) throw new ArgumentNullException(nameof(heights));
        if ((riverWeights == null) != (riverWidths == null)) throw new ArgumentException("River weight and width must be given together.");
        long count = (long)countX * countZ;
        if (count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(countX), "Grid is too large.");
        Provenance = provenance; OriginX = originX; OriginZ = originZ; Spacing = spacing; CountX = countX; CountZ = countZ;
        GridValues.Finite(MaxX); GridValues.Finite(MaxZ);
        _heights = Layer(heights, count, nameof(heights));
        if (baseHeights != null) _baseHeights = Layer(baseHeights, count, nameof(baseHeights));
        if (biomes != null)
        {
            if (biomes.Length != count) throw new ArgumentException($"{nameof(biomes)} has {biomes.Length} values; the grid has {count} nodes.", nameof(biomes));
            foreach (var biome in biomes)
                if (biome == TerrainBiome.Unknown || !Enum.IsDefined(typeof(TerrainBiome), biome)) throw new ArgumentException("Every node of a biome layer needs a known biome.", nameof(biomes));
            _biomes = (TerrainBiome[])biomes.Clone();
        }
        if (riverWeights != null) _riverWeights = Layer(riverWeights, count, nameof(riverWeights));
        if (riverWidths != null) _riverWidths = Layer(riverWidths, count, nameof(riverWidths));
    }

    private static float[] Layer(float[] values, long count, string name)
    {
        if (values.Length != count) throw new ArgumentException($"{name} has {values.Length} values; the grid has {count} nodes.", name);
        foreach (float value in values) if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException($"{name} holds a non-finite value.", name);
        return (float[])values.Clone();
    }

    /// <summary>Whether a query at this coordinate is answered rather than refused.</summary>
    public bool Contains(float x, float z) => Cell(x, OriginX, CountX, out _) && Cell(z, OriginZ, CountZ, out _);

    public float GetHeight(float x, float z) => Sample(_heights, x, z);

    /// <summary>
    /// Valheim's unitless generator base-height value as exported by `cli_world_dump`, not a ground elevation in metres.
    /// At nodes this is the dumped value; between nodes it is bilinear and only an approximation.
    /// </summary>
    public float GetBaseHeight(float x, float z)
    {
        Check(x, z);
        if (_baseHeights == null) throw new NotSupportedException($"{Provenance} has no base_height layer.");
        return Sample(_baseHeights, x, z);
    }

    public TerrainBiome GetBiome(float x, float z)
    {
        Check(x, z);
        if (_biomes == null) throw new NotSupportedException($"{Provenance} has no biome layer.");
        int i = Nearest(x, OriginX, CountX), j = Nearest(z, OriginZ, CountZ);
        return _biomes[j * CountX + i];
    }

    public void GetRiverWeight(float x, float z, out float weight, out float width)
    {
        Check(x, z);
        if (_riverWeights == null || _riverWidths == null) throw new NotSupportedException($"{Provenance} has no river layer.");
        weight = Sample(_riverWeights, x, z);
        width = Sample(_riverWidths, x, z);
    }

    /// <summary>The fractional node position along one axis; false when outside the covered range.</summary>
    private bool Cell(float value, float origin, int count, out double position)
    {
        position = ((double)value - origin) / Spacing;
        if (double.IsNaN(position) || double.IsInfinity(position)) return false;
        double node = Math.Round(position);
        if (Math.Abs(position - node) <= NodeSnap) position = node;
        return position >= 0 && position <= count - 1;
    }

    private void Check(float x, float z)
    {
        GridValues.Finite(x); GridValues.Finite(z);
        if (!Contains(x, z))
            throw new ArgumentOutOfRangeException(nameof(x), string.Format(CultureInfo.InvariantCulture,
                "({0},{1}) is outside {2}, which covers x {3}..{4}, z {5}..{6}. A grid does not guess beyond its samples.",
                x, z, Provenance, OriginX, MaxX, OriginZ, MaxZ));
    }

    private int Nearest(float value, float origin, int count)
    {
        Cell(value, origin, count, out double position);
        return Math.Min((int)Math.Floor(position + .5), count - 1);
    }

    private float Sample(float[] values, float x, float z)
    {
        Check(x, z);
        Cell(x, OriginX, CountX, out double fx);
        Cell(z, OriginZ, CountZ, out double fz);
        int i = Math.Min((int)Math.Floor(fx), CountX - 2), j = Math.Min((int)Math.Floor(fz), CountZ - 2);
        double tx = fx - i, tz = fz - j;
        float v00 = values[j * CountX + i], v10 = values[j * CountX + i + 1];
        float v01 = values[(j + 1) * CountX + i], v11 = values[(j + 1) * CountX + i + 1];
        // Exact node values stay exact: a zero weight never mixes in a neighbour.
        if (tx == 0 && tz == 0) return v00;
        if (tx == 1 && tz == 0) return v10;
        if (tx == 0 && tz == 1) return v01;
        if (tx == 1 && tz == 1) return v11;
        double near = v00 + (v10 - (double)v00) * tx, far = v01 + (v11 - (double)v01) * tx;
        return (float)(near + (far - near) * tz);
    }

    /// <summary>Reads a CSV grid file; the provenance is the file name without its directory.</summary>
    public static GridDumpTerrain Load(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        using var reader = new StreamReader(path);
        return Read(reader, Path.GetFileName(path));
    }

    /// <summary>
    /// Reads a CSV grid: a header row, then one row per node. Columns are found by name (case-insensitive, any
    /// order): <c>x</c>, <c>z</c> and <c>height</c> are required; <c>biome</c> is optional; <c>river</c> and
    /// <c>river_width</c> are optional but only together; <c>base_height</c> is an optional unitless generator
    /// value, not metre-valued ground height. Other columns are ignored. Rows may come in any order but must cover every node of one evenly spaced grid exactly once,
    /// with the same spacing on x and z. Numbers use the invariant culture. Empty lines are skipped. This reads
    /// ValheimCLI's <c>cli_world_dump</c> output (<c>x,z,height,biome,river,river_width,base_height</c>) as is.
    /// Every refusal throws <see cref="InvalidDataException"/> naming the line.
    /// </summary>
    public static GridDumpTerrain Read(TextReader reader, string provenance)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (string.IsNullOrWhiteSpace(provenance)) throw new ArgumentException("Provenance is required.", nameof(provenance));
        string header = reader.ReadLine() ?? throw new InvalidDataException($"{provenance}: empty file; expected a header row.");
        string[] names = header.Split(',');
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < names.Length; c++)
        {
            string name = names[c].Trim();
            if (name.Length == 0) throw new InvalidDataException($"{provenance}: header column {c + 1} has no name.");
            if (columns.ContainsKey(name)) throw new InvalidDataException($"{provenance}: header names column '{name}' twice.");
            columns.Add(name, c);
        }
        int cx = Required(columns, "x", provenance), cz = Required(columns, "z", provenance), ch = Required(columns, "height", provenance);
        int cb = Optional(columns, "biome"), cr = Optional(columns, "river"), cw = Optional(columns, "river_width"),
            cbase = Optional(columns, "base_height");
        if ((cr < 0) != (cw < 0)) throw new InvalidDataException($"{provenance}: columns 'river' and 'river_width' must be present together.");

        var rows = new List<(float x, float z, float height, TerrainBiome biome, float river, float width, float baseHeight, int line)>();
        int lineNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (line.Trim().Length == 0) continue;
            string[] cells = line.Split(',');
            if (cells.Length != names.Length)
                throw new InvalidDataException($"{provenance}: line {lineNumber} has {cells.Length} cells; the header has {names.Length}.");
            rows.Add((Number(cells[cx], "x", lineNumber, provenance), Number(cells[cz], "z", lineNumber, provenance),
                Number(cells[ch], "height", lineNumber, provenance),
                cb < 0 ? TerrainBiome.Unknown : Biome(cells[cb], lineNumber, provenance),
                cr < 0 ? 0 : Number(cells[cr], "river", lineNumber, provenance),
                cw < 0 ? 0 : Number(cells[cw], "river_width", lineNumber, provenance),
                cbase < 0 ? 0 : Number(cells[cbase], "base_height", lineNumber, provenance), lineNumber));
        }
        if (rows.Count == 0) throw new InvalidDataException($"{provenance}: no samples after the header.");

        var xs = Axis(rows.ConvertAll(r => r.x), "x", provenance, out double stepX);
        var zs = Axis(rows.ConvertAll(r => r.z), "z", provenance, out double stepZ);
        if (Math.Abs(stepX - stepZ) > Math.Max(stepX, stepZ) * 1e-3)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0}: x spacing {1} and z spacing {2} differ; a grid needs one spacing.", provenance, stepX, stepZ));
        int countX = xs.Count, countZ = zs.Count;
        var xIndex = new Dictionary<float, int>(); for (int i = 0; i < countX; i++) xIndex.Add(xs[i], i);
        var zIndex = new Dictionary<float, int>(); for (int j = 0; j < countZ; j++) zIndex.Add(zs[j], j);

        long count = (long)countX * countZ;
        if (count > int.MaxValue) throw new InvalidDataException($"{provenance}: {countX}x{countZ} nodes is too large.");
        var heights = new float[count];
        var biomes = cb < 0 ? null : new TerrainBiome[count];
        var weights = cr < 0 ? null : new float[count];
        var widths = cw < 0 ? null : new float[count];
        var baseHeights = cbase < 0 ? null : new float[count];
        var seenLine = new int[count];
        var perRow = new int[countZ];
        foreach (var row in rows)
        {
            int i = xIndex[row.x], j = zIndex[row.z], k = j * countX + i;
            if (seenLine[k] != 0)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0}: line {1} repeats the node ({2},{3}) from line {4}.", provenance, row.line, row.x, row.z, seenLine[k]));
            seenLine[k] = row.line; perRow[j]++;
            heights[k] = row.height;
            if (biomes != null) biomes[k] = row.biome;
            if (weights != null) weights[k] = row.river;
            if (widths != null) widths[k] = row.width;
            if (baseHeights != null) baseHeights[k] = row.baseHeight;
        }
        for (int j = 0; j < countZ; j++)
        {
            if (perRow[j] == countX) continue;
            int missing = 0; while (seenLine[j * countX + missing] != 0) missing++;
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "{0}: the row at z={1} has {2} of {3} samples (first missing x={4}); the grid is ragged.", provenance, zs[j], perRow[j], countX, xs[missing]));
        }
        return new GridDumpTerrain(provenance, xs[0], zs[0], (float)stepX, countX, countZ, heights, biomes, weights, widths, baseHeights);
    }

    private static List<float> Axis(List<float> values, string axis, string provenance, out double step)
    {
        var distinct = new SortedSet<float>(values);
        var sorted = new List<float>(distinct);
        if (sorted.Count < 2) throw new InvalidDataException($"{provenance}: a grid needs at least two distinct {axis} values.");
        step = ((double)sorted[sorted.Count - 1] - sorted[0]) / (sorted.Count - 1);
        for (int i = 1; i < sorted.Count; i++)
        {
            double gap = (double)sorted[i] - sorted[i - 1];
            // Text coordinates carry rounding; 0.1% of a spacing is far below any real gap.
            if (Math.Abs(gap - step) > step * 1e-3)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1} values are not evenly spaced ({2} to {3} is {4}; the grid needs {5}).", provenance, axis, sorted[i - 1], sorted[i], gap, step));
        }
        return sorted;
    }

    private static int Required(Dictionary<string, int> columns, string name, string provenance) =>
        columns.TryGetValue(name, out int c) ? c : throw new InvalidDataException($"{provenance}: required column '{name}' is missing; x, z and height are required.");

    private static int Optional(Dictionary<string, int> columns, string name) => columns.TryGetValue(name, out int c) ? c : -1;

    private static float Number(string cell, string column, int line, string provenance)
    {
        if (!float.TryParse(cell.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
            throw new InvalidDataException($"{provenance}: line {line} column '{column}' is not a finite number: '{cell}'.");
        return value == 0 ? 0 : value; // -0 and 0 are one coordinate
    }

    private static TerrainBiome Biome(string cell, int line, string provenance)
    {
        string name = cell.Trim();
        // Names only (the game writes AshLands; this enum says Ashlands); a bare number or Unknown is refused.
        foreach (TerrainBiome biome in Enum.GetValues(typeof(TerrainBiome)))
            if (biome != TerrainBiome.Unknown && string.Equals(biome.ToString(), name, StringComparison.OrdinalIgnoreCase)) return biome;
        throw new InvalidDataException($"{provenance}: line {line} has unknown biome '{name}'.");
    }
}
