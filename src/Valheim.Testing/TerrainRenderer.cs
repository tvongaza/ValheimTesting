using System;
using System.Collections.Generic;
using System.IO;

namespace Valheim.Testing;

/// <summary>A closed world rectangle on the horizontal x/z plane, in metres.</summary>
public readonly struct TerrainArea
{
    public float MinX { get; }
    public float MinZ { get; }
    public float MaxX { get; }
    public float MaxZ { get; }
    public TerrainArea(float minX, float minZ, float maxX, float maxZ)
    {
        GridValues.Finite(minX); GridValues.Finite(minZ); GridValues.Finite(maxX); GridValues.Finite(maxZ);
        if (maxX <= minX || maxZ <= minZ) throw new ArgumentException("Area must have positive size.");
        MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
    }
}

/// <summary>An opaque 8-bit sRGB colour.</summary>
public readonly struct RenderColor : IEquatable<RenderColor>
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public RenderColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
    public bool Equals(RenderColor other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is RenderColor other && Equals(other);
    public override int GetHashCode() => (R << 16) | (G << 8) | B;
    public override string ToString() => "#" + R.ToString("x2") + G.ToString("x2") + B.ToString("x2");
}

public enum TerrainColoring { Height, Biome }

/// <summary>
/// Renders an <see cref="ITerrain"/> into an RGB image for review. The image covers <see cref="Area"/> with
/// square pixels of <see cref="MetresPerPixel"/>; +x points right and +z points up (north up). Each pixel shows
/// the terrain sampled once at its centre. Contours are drawn over the terrain, then polylines in the order added, then points.
/// The same inputs give the same bytes on every platform, provided the terrain itself answers the same heights.
/// A terrain that refuses a coordinate (outside a grid, an uncaptured replay sample, a missing biome layer)
/// fails the render; nothing is painted in its place. A picture of an input is not evidence the game agrees.
/// </summary>
public sealed class TerrainRenderer
{
    /// <summary>Largest width or height in pixels, to refuse accidentally huge renders.</summary>
    public const int MaxPixels = 8192;
    private const int MaxOverlayOffset = 1000000;
    private readonly List<(float x, float z)[]> _polylines = new List<(float, float)[]>();
    private readonly List<RenderColor> _polylineColors = new List<RenderColor>();
    private readonly List<(float x, float z, RenderColor color, int radius)> _points = new List<(float, float, RenderColor, int)>();
    private readonly List<(float interval, RenderColor color)> _contours = new List<(float, RenderColor)>();

    public TerrainArea Area { get; }
    public float MetresPerPixel { get; }
    public int Width { get; }
    public int Height { get; }
    public TerrainColoring Coloring { get; set; } = TerrainColoring.Height;
    /// <summary>Height mode: the height drawn darkest (deep water, or the lowest land without water).</summary>
    public float LowHeight { get; set; } = 0;
    /// <summary>Height mode: the height drawn white.</summary>
    public float HighHeight { get; set; } = 100;
    /// <summary>Height mode: heights below this are drawn as water; null draws every height as land. The game's sea level is 30.</summary>
    public float? WaterLevel { get; set; } = TerrainMath.SeaLevel;

    /// <summary>The area's width and depth must each be a whole number of pixels.</summary>
    public TerrainRenderer(TerrainArea area, float metresPerPixel)
    {
        GridValues.Finite(metresPerPixel);
        if (metresPerPixel <= 0) throw new ArgumentOutOfRangeException(nameof(metresPerPixel), "Metres per pixel must be positive.");
        Area = area; MetresPerPixel = metresPerPixel;
        Width = WholePixels(area.MaxX - (double)area.MinX, metresPerPixel, "width");
        Height = WholePixels(area.MaxZ - (double)area.MinZ, metresPerPixel, "depth");
    }

    private static int WholePixels(double size, float metresPerPixel, string axis)
    {
        double pixels = size / metresPerPixel, whole = Math.Round(pixels);
        if (Math.Abs(pixels - whole) > 1e-4 || whole < 1)
            throw new ArgumentException($"The area's {axis} is {pixels} pixels; it must be a whole number of pixels.");
        if (whole > MaxPixels) throw new ArgumentOutOfRangeException(nameof(metresPerPixel), $"The image would be {whole} pixels on one side; the limit is {MaxPixels}.");
        return (int)whole;
    }

    /// <summary>Adds a filled disc: every pixel whose offset (dx, dy) from the point's pixel has dx*dx + dy*dy &lt;= radius*radius.</summary>
    public TerrainRenderer Point(float x, float z, RenderColor color, int radius = 2)
    {
        GridValues.Finite(x); GridValues.Finite(z);
        if (radius < 0 || radius > 256) throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be 0..256 pixels.");
        _points.Add((x, z, color, radius));
        return this;
    }

    /// <summary>Adds straight one-pixel segments (Bresenham) between the pixels holding consecutive points.</summary>
    public TerrainRenderer Polyline(IEnumerable<(float x, float z)> points, RenderColor color)
    {
        if (points == null) throw new ArgumentNullException(nameof(points));
        var copy = new List<(float x, float z)>(points).ToArray();
        if (copy.Length == 0) throw new ArgumentException("A polyline needs at least one point.", nameof(points));
        foreach (var (x, z) in copy) { GridValues.Finite(x); GridValues.Finite(z); }
        _polylines.Add(copy); _polylineColors.Add(color);
        return this;
    }

    /// <summary>
    /// Adds contour lines at <see cref="WaterLevel"/> (the game's sea level, 30 m, when it is null) and every
    /// <paramref name="interval"/> metres above and below it.
    /// A pixel is drawn when a 4-neighbour's height lies below a level its own height reaches, so each line is one pixel
    /// wide on its uphill side and the coastline is always one of the lines. Heights are the pixel-centre samples the
    /// terrain answers, in either colouring mode; between nodes of a grid those are its interpolation, not the game's.
    /// </summary>
    public TerrainRenderer Contours(float interval, RenderColor color)
    {
        GridValues.Finite(interval);
        if (interval < .01f) throw new ArgumentOutOfRangeException(nameof(interval), "Contours must be at least 0.01 m apart.");
        _contours.Add((interval, color));
        return this;
    }

    /// <summary>World x of a pixel column's centre.</summary>
    public float PixelX(int px) => (float)(Area.MinX + (px + .5) * MetresPerPixel);
    /// <summary>World z of a pixel row's centre; row 0 is the top (largest z).</summary>
    public float PixelZ(int py) => (float)(Area.MaxZ - (py + .5) * MetresPerPixel);

    public TerrainImage Render(ITerrain terrain)
    {
        if (terrain == null) throw new ArgumentNullException(nameof(terrain));
        if (Coloring == TerrainColoring.Height)
        {
            GridValues.Finite(LowHeight); GridValues.Finite(HighHeight);
            if (HighHeight <= LowHeight) throw new InvalidOperationException("HighHeight must be above LowHeight.");
            if (WaterLevel.HasValue && (float.IsNaN(WaterLevel.Value) || WaterLevel.Value < LowHeight || WaterLevel.Value >= HighHeight))
                throw new InvalidOperationException("WaterLevel must be at least LowHeight and below HighHeight.");
        }
        var image = new TerrainImage(Width, Height);
        float[]? heights = _contours.Count > 0 ? new float[checked(Width * Height)] : null;
        for (int py = 0; py < Height; py++)
        {
            float z = PixelZ(py);
            for (int px = 0; px < Width; px++)
            {
                float x = PixelX(px);
                if (Coloring == TerrainColoring.Biome)
                {
                    image.Set(px, py, BiomeColor(terrain.GetBiome(x, z)));
                    if (heights != null) heights[py * Width + px] = Finite(terrain.GetHeight(x, z));
                }
                else
                {
                    float height = terrain.GetHeight(x, z);
                    image.Set(px, py, HeightColor(height));
                    if (heights != null) heights[py * Width + px] = height;
                }
            }
        }
        if (heights != null)
            foreach (var (interval, color) in _contours) DrawContours(image, heights, interval, color);
        for (int i = 0; i < _polylines.Count; i++)
        {
            var line = _polylines[i];
            var (lastX, lastY) = ToPixel(line[0].x, line[0].z);
            Plot(image, lastX, lastY, _polylineColors[i]);
            for (int k = 1; k < line.Length; k++)
            {
                var (nextX, nextY) = ToPixel(line[k].x, line[k].z);
                Segment(image, lastX, lastY, nextX, nextY, _polylineColors[i]);
                lastX = nextX; lastY = nextY;
            }
        }
        foreach (var (x, z, color, radius) in _points)
        {
            var (cx, cy) = ToPixel(x, z);
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dy * dy <= radius * radius) Plot(image, cx + dx, cy + dy, color);
        }
        return image;
    }

    /// <summary>The pixel holding a world position; may lie outside the image, where drawing is clipped.</summary>
    public (int px, int py) ToPixel(float x, float z)
    {
        double px = Math.Floor((x - (double)Area.MinX) / MetresPerPixel), py = Math.Floor((Area.MaxZ - (double)z) / MetresPerPixel);
        if (Math.Abs(px) > MaxOverlayOffset || Math.Abs(py) > MaxOverlayOffset)
            throw new ArgumentOutOfRangeException(nameof(x), $"Overlay point ({x},{z}) is more than {MaxOverlayOffset} pixels from the image.");
        return ((int)px, (int)py);
    }

    private static float Finite(float height) =>
        float.IsNaN(height) || float.IsInfinity(height) ? throw new InvalidOperationException("The terrain returned a non-finite height.") : height;

    private void DrawContours(TerrainImage image, float[] heights, float interval, RenderColor color)
    {
        double sea = WaterLevel ?? TerrainMath.SeaLevel;
        // The level index a height reaches. A height within a thousandth of an interval below a level counts as on it, so
        // float rounding cannot move a height written as exactly a level (30.3f at 0.1 m) into the band below.
        double Band(int i) => Math.Floor((heights[i] - sea) / interval + 1e-3);
        for (int py = 0; py < Height; py++)
            for (int px = 0; px < Width; px++)
            {
                int i = py * Width + px;
                double own = Band(i);
                if ((px > 0 && Band(i - 1) < own) || (px + 1 < Width && Band(i + 1) < own) ||
                    (py > 0 && Band(i - Width) < own) || (py + 1 < Height && Band(i + Width) < own))
                    image.Set(px, py, color);
            }
    }

    private static void Plot(TerrainImage image, int px, int py, RenderColor color)
    {
        if (px >= 0 && py >= 0 && px < image.Width && py < image.Height) image.Set(px, py, color);
    }

    private static void Segment(TerrainImage image, int x0, int y0, int x1, int y1, RenderColor color)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, error = dx + dy;
        while (true)
        {
            Plot(image, x0, y0, color);
            if (x0 == x1 && y0 == y1) return;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
    }

    private static readonly RenderColor Shallow = new RenderColor(96, 160, 210), Deep = new RenderColor(16, 40, 100);
    private static readonly RenderColor Lowland = new RenderColor(64, 120, 48), Upland = new RenderColor(160, 140, 96), Peak = new RenderColor(245, 245, 245);

    /// <summary>
    /// Height mode colour. Water (below <see cref="WaterLevel"/>) runs from light blue at the water level to dark
    /// blue at <see cref="LowHeight"/>. Land runs green, brown, white from the water level (or LowHeight) to
    /// <see cref="HighHeight"/>, brown at the midpoint. Heights beyond the range take the end colour.
    /// </summary>
    public RenderColor HeightColor(float height)
    {
        if (float.IsNaN(height) || float.IsInfinity(height)) throw new InvalidOperationException("The terrain returned a non-finite height.");
        if (WaterLevel.HasValue && height < WaterLevel.Value)
        {
            float water = WaterLevel.Value;
            double depth = water > LowHeight ? Clamp01((water - (double)height) / (water - (double)LowHeight)) : 1;
            return Mix(Shallow, Deep, depth);
        }
        float low = WaterLevel ?? LowHeight;
        double t = Clamp01((height - (double)low) / (HighHeight - (double)low));
        return t < .5 ? Mix(Lowland, Upland, t * 2) : Mix(Upland, Peak, t * 2 - 1);
    }

    /// <summary>Biome mode colour: one fixed colour per biome; Unknown is magenta.</summary>
    public static RenderColor BiomeColor(TerrainBiome biome)
    {
        switch (biome)
        {
            case TerrainBiome.Meadows: return new RenderColor(140, 176, 80);
            case TerrainBiome.BlackForest: return new RenderColor(60, 90, 50);
            case TerrainBiome.Swamp: return new RenderColor(110, 90, 60);
            case TerrainBiome.Mountain: return new RenderColor(225, 225, 235);
            case TerrainBiome.Plains: return new RenderColor(215, 195, 100);
            case TerrainBiome.Mistlands: return new RenderColor(120, 110, 130);
            case TerrainBiome.Ocean: return new RenderColor(40, 80, 160);
            case TerrainBiome.AshLands: return new RenderColor(160, 60, 40);
            case TerrainBiome.DeepNorth: return new RenderColor(190, 220, 240);
            default: return new RenderColor(255, 0, 255);
        }
    }

    private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    private static byte Channel(byte a, byte b, double t) => (byte)Math.Floor(a + (b - a) * t + .5);
    private static RenderColor Mix(RenderColor a, RenderColor b, double t) => new RenderColor(Channel(a.R, b.R, t), Channel(a.G, b.G, t), Channel(a.B, b.B, t));
}

/// <summary>A rendered RGB image, row 0 at the top.</summary>
public sealed class TerrainImage
{
    private readonly byte[] _rgb;
    public int Width { get; }
    public int Height { get; }
    internal TerrainImage(int width, int height) { Width = width; Height = height; _rgb = new byte[checked(width * height * 3)]; }
    private int Index(int px, int py)
    {
        if (px < 0 || px >= Width) throw new ArgumentOutOfRangeException(nameof(px));
        if (py < 0 || py >= Height) throw new ArgumentOutOfRangeException(nameof(py));
        return (py * Width + px) * 3;
    }
    public RenderColor this[int px, int py] { get { int i = Index(px, py); return new RenderColor(_rgb[i], _rgb[i + 1], _rgb[i + 2]); } }
    internal void Set(int px, int py, RenderColor color) { int i = Index(px, py); _rgb[i] = color.R; _rgb[i + 1] = color.G; _rgb[i + 2] = color.B; }

    /// <summary>
    /// Encodes a truecolour 8-bit PNG. The image data uses uncompressed (stored) deflate blocks, so the bytes do
    /// not depend on a runtime's zlib version: about three bytes per pixel. Recompress with any PNG tool if size matters.
    /// </summary>
    public byte[] ToPng()
    {
        int row = Width * 3 + 1;
        var raw = new byte[checked(row * Height)];
        for (int py = 0; py < Height; py++) Buffer.BlockCopy(_rgb, py * Width * 3, raw, py * row + 1, Width * 3); // filter 0 per row
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        var header = new byte[13];
        BigEndian(header, 0, (uint)Width); BigEndian(header, 4, (uint)Height);
        header[8] = 8; header[9] = 2; // 8 bits per channel, truecolour; compression, filter and interlace 0
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", Zlib(raw));
        Chunk(png, "IEND", new byte[0]);
        return png.ToArray();
    }

    public void WritePng(string path) => File.WriteAllBytes(path, ToPng());

    private static byte[] Zlib(byte[] data)
    {
        const int block = 65535;
        int blocks = Math.Max(1, (data.Length + block - 1) / block);
        var output = new byte[checked(2 + blocks * 5 + data.Length + 4)];
        output[0] = 0x78; output[1] = 0x01; // deflate, 32K window, no dictionary; (0x78 * 256 + 0x01) % 31 == 0
        int at = 2;
        for (int b = 0, offset = 0; b < blocks; b++, offset += block)
        {
            int length = Math.Min(block, data.Length - offset);
            output[at++] = (byte)(b == blocks - 1 ? 1 : 0); // BFINAL on the last block, BTYPE 00 (stored)
            output[at++] = (byte)length; output[at++] = (byte)(length >> 8);
            output[at++] = (byte)~length; output[at++] = (byte)(~length >> 8);
            Buffer.BlockCopy(data, offset, output, at, length); at += length;
        }
        uint a = 1, s = 0;
        foreach (byte value in data) { a = (a + value) % 65521; s = (s + a) % 65521; }
        BigEndian(output, at, (s << 16) | a);
        return output;
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var buffer = new byte[checked(12 + data.Length)];
        BigEndian(buffer, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) buffer[4 + i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, buffer, 8, data.Length);
        BigEndian(buffer, 8 + data.Length, Crc32(buffer, 4, 4 + data.Length));
        png.Write(buffer, 0, buffer.Length);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
    private static uint Crc32(byte[] data, int offset, int count)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = offset; i < offset + count; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
    private static void BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16); buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
    }
}
