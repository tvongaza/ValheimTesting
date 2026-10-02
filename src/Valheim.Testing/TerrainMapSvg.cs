using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;

namespace Valheim.Testing;

/// <summary>
/// A terrain-only overview and fine window from verified native dumps. This is a review picture, not proof of
/// the game's loaded terrain or walking behavior. The CLI's world-map.py is the visual reference for this palette;
/// location labels and route overlays remain outside this renderer's terrain-only contract.
/// </summary>
public static class TerrainMapSvg
{
    private const long MaxMapPixels = 4_000_000;
    private const float Sea = 30f;
    private const float RiverFloor = Sea - 8f;
    private static readonly RenderColor Outside = new(226, 226, 226);
    private static readonly RenderColor Ocean = new(185, 210, 232);
    private static readonly RenderColor SwampWater = new(159, 175, 134);
    private static readonly RenderColor RiverWater = new(127, 166, 212);
    private static readonly RenderColor RiverBank = new(211, 228, 212);

    /// <summary>
    /// Verify the exact coarse/fine files and render both views in one deterministic SVG. The zoom's four corners
    /// must be answered by the same layer finer than the designated base lattice. Pixels between dump nodes and
    /// contour intersections are interpolated approximations; the caption records that limitation and provenance.
    /// </summary>
    public static string Render(IEnumerable<WorldDumpLayerSpec> layers, string baseHeightLayer,
        TerrainArea overview, float overviewMetresPerPixel, TerrainArea zoom, float zoomMetresPerPixel,
        float contourInterval = 10f, float worldRadius = 10000f)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));
        var specs = layers.ToArray();
        if (specs.Length < 2) throw new ArgumentException("A coarse layer and a separate fine window are required.", nameof(layers));
        if (float.IsNaN(contourInterval) || float.IsInfinity(contourInterval) || contourInterval < 1)
            throw new ArgumentOutOfRangeException(nameof(contourInterval), "Use contours at least one metre apart.");
        if (float.IsNaN(worldRadius) || float.IsInfinity(worldRadius) || worldRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(worldRadius));

        var terrain = LayeredDumpTerrain.Load(specs, baseHeightLayer);
        var baseSpec = specs.Single(spec => spec.Name == baseHeightLayer);
        var corners = new[] { (zoom.MinX, zoom.MinZ), (zoom.MinX, zoom.MaxZ),
            (zoom.MaxX, zoom.MinZ), (zoom.MaxX, zoom.MaxZ) };
        var zoomSources = corners.Select(p => terrain.SourceAt(p.Item1, p.Item2)).ToArray();
        string fine = zoomSources[0].Layer;
        if (zoomSources.Any(source => source.Layer != fine || source.Spacing >= baseSpec.Manifest.Step))
            throw new InvalidOperationException("The entire zoom must lie in one declared layer finer than the base lattice.");
        if (zoom.MinX < overview.MinX || zoom.MaxX > overview.MaxX || zoom.MinZ < overview.MinZ || zoom.MaxZ > overview.MaxZ)
            throw new ArgumentException("The zoom must lie inside the overview so its bounds can be marked.", nameof(zoom));

        var broadShape = new TerrainRenderer(overview, overviewMetresPerPixel);
        var detailShape = new TerrainRenderer(zoom, zoomMetresPerPixel);
        if ((long)broadShape.Width * broadShape.Height + (long)detailShape.Width * detailShape.Height > MaxMapPixels)
            throw new ArgumentOutOfRangeException(nameof(overviewMetresPerPixel), "The two views may contain at most four million pixels total.");
        var broad = new View(terrain, broadShape, contourInterval, worldRadius);
        var detail = new View(terrain, detailShape, contourInterval, worldRadius);
        const int margin = 20;
        int width = Math.Max(640, checked(broad.Width + detail.Width + margin));
        string caption = $"World UID {baseSpec.Manifest.WorldUid} | seed {baseSpec.Manifest.Seed} | Valheim {baseSpec.Manifest.GameBuild} | " +
            $"base {baseHeightLayer} {baseSpec.Manifest.Step} m | zoom {fine} {zoomSources[0].Spacing:G} m | " +
            $"contours {contourInterval:G} m, sea {Sea:G} m | between-node terrain is approximate";
        var captionLines = Wrap(caption, Math.Max(20, (width - 12) / 7));
        int top = 20 + captionLines.Count * 16 + 20;
        int height = checked(Math.Max(broad.Height, detail.Height) + top);
        var svg = new StringBuilder();
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\">\n");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>\n");
        for (int i = 0; i < captionLines.Count; i++)
            svg.Append($"<text x=\"6\" y=\"{18 + i * 16}\" font-size=\"12\">{SecurityElement.Escape(captionLines[i])}</text>\n");
        svg.Append($"<text x=\"6\" y=\"{top - 9}\" font-size=\"11\">Overview</text>\n");
        svg.Append($"<text x=\"{broad.Width + margin}\" y=\"{top - 9}\" font-size=\"11\">Fine window</text>\n");
        broad.Append(svg, "overview", 0, top, zoom);
        detail.Append(svg, "fine", broad.Width + margin, top, null);
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static List<string> Wrap(string text, int limit)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > limit)
            {
                lines.Add(line.ToString()); line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    private static RenderColor Ground(float height, TerrainBiome biome, float river)
    {
        if (height < Sea)
        {
            if (biome == TerrainBiome.Swamp) return SwampWater;
            return river > .5f && height >= RiverFloor ? RiverWater : Ocean;
        }
        if (river > .5f) return RiverBank;
        return biome switch
        {
            TerrainBiome.Meadows => new(228, 238, 211),
            TerrainBiome.BlackForest => new(201, 220, 196),
            TerrainBiome.Swamp => new(220, 217, 192),
            TerrainBiome.Mountain => new(243, 245, 247),
            TerrainBiome.Plains => new(240, 234, 203),
            TerrainBiome.Mistlands => new(220, 219, 230),
            TerrainBiome.Ashlands => new(234, 207, 195),
            TerrainBiome.DeepNorth => new(238, 243, 247),
            TerrainBiome.Ocean => Ocean,
            _ => throw new InvalidOperationException($"The dump contains unsupported biome {biome}.")
        };
    }

    private sealed class View
    {
        private readonly TerrainRenderer _mapping;
        private readonly TerrainImage _image;
        private readonly float[,] _heights;
        private readonly float _interval;
        private readonly float _radius;
        public int Width => _mapping.Width;
        public int Height => _mapping.Height;
        public View(LayeredDumpTerrain terrain, TerrainRenderer mapping, float interval, float radius)
        {
            _mapping = mapping;
            _image = new TerrainImage(Width, Height);
            _heights = new float[Width, Height];
            _interval = interval; _radius = radius;
            for (int py = 0; py < Height; py++)
            for (int px = 0; px < Width; px++)
            {
                float x = _mapping.PixelX(px), z = _mapping.PixelZ(py);
                if ((double)x * x + (double)z * z > (double)radius * radius)
                {
                    _image.Set(px, py, Outside); _heights[px, py] = float.NaN;
                    continue;
                }
                float h = terrain.GetHeight(x, z);
                terrain.GetRiverWeight(x, z, out float river, out _);
                _image.Set(px, py, Ground(h, terrain.GetBiome(x, z), river));
                _heights[px, py] = h;
            }
        }
        private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private double X(float x) => (x - (double)_mapping.Area.MinX) / _mapping.MetresPerPixel;
        private double Y(float z) => (_mapping.Area.MaxZ - (double)z) / _mapping.MetresPerPixel;

        public void Append(StringBuilder svg, string id, int left, int top, TerrainArea? zoom)
        {
            string png = Convert.ToBase64String(_image.ToPng());
            double cx = X(0), cy = Y(0), radius = _radius / _mapping.MetresPerPixel;
            svg.Append($"<defs><clipPath id=\"{id}-disc\"><circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(radius)}\"/></clipPath></defs>\n");
            svg.Append($"<g transform=\"translate({left},{top})\">\n");
            svg.Append($"<image width=\"{Width}\" height=\"{Height}\" href=\"data:image/png;base64,{png}\"/>\n");
            svg.Append($"<g clip-path=\"url(#{id}-disc)\">\n");
            AppendContours(svg);
            svg.Append("</g>\n");
            svg.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(radius)}\" fill=\"none\" stroke=\"#909090\" stroke-width=\"0.8\" stroke-dasharray=\"6,4\"/>\n");
            if (zoom is { } box)
                svg.Append($"<rect x=\"{F(X(box.MinX))}\" y=\"{F(Y(box.MaxZ))}\" width=\"{F(X(box.MaxX) - X(box.MinX))}\" height=\"{F(Y(box.MinZ) - Y(box.MaxZ))}\" fill=\"none\" stroke=\"#111\" stroke-width=\"1.2\" stroke-dasharray=\"5,3\"/>\n");
            svg.Append($"<rect width=\"{Width}\" height=\"{Height}\" fill=\"none\" stroke=\"#666\" stroke-width=\"0.8\"/></g>\n");
        }

        private void AppendContours(StringBuilder svg)
        {
            var paths = new SortedDictionary<float, StringBuilder>();
            for (int py = 0; py < Height - 1; py++)
            for (int px = 0; px < Width - 1; px++)
            {
                // Clockwise, north-up: top-left, top-right, bottom-right, bottom-left.
                var hs = new[] { _heights[px, py], _heights[px + 1, py], _heights[px + 1, py + 1], _heights[px, py + 1] };
                if (hs.Any(float.IsNaN)) continue;
                float low = hs.Min(), high = hs.Max();
                if (high < Sea) continue;
                if ((high - low) / _interval > 1000)
                    throw new InvalidOperationException("One map cell crosses more than 1000 contour levels; increase the contour interval or inspect the dump.");
                Cut(Sea);
                for (int k = Math.Max(1, (int)Math.Floor((low - Sea) / _interval) + 1); Sea + k * _interval <= high; k++)
                    Cut(Sea + k * _interval);

                void Cut(float level)
                {
                    if (level < low || level > high) return;
                    var corners = new[] { (x: px + .5, y: py + .5), (x: px + 1.5, y: py + .5),
                        (x: px + 1.5, y: py + 1.5), (x: px + .5, y: py + 1.5) };
                    var cuts = new List<(double x, double y)>();
                    for (int i = 0; i < 4; i++)
                    {
                        int j = (i + 1) % 4;
                        if ((hs[i] >= level) == (hs[j] >= level)) continue;
                        double t = (level - hs[i]) / (double)(hs[j] - hs[i]);
                        cuts.Add((corners[i].x + (corners[j].x - corners[i].x) * t,
                            corners[i].y + (corners[j].y - corners[i].y) * t));
                    }
                    if (cuts.Count == 4 && (hs[0] >= level) != (hs.Average() >= level))
                        cuts = cuts.Skip(1).Concat(cuts.Take(1)).ToList();
                    if (!paths.TryGetValue(level, out var path)) paths[level] = path = new StringBuilder();
                    for (int i = 0; i + 1 < cuts.Count; i += 2)
                        path.Append($"M{F(cuts[i].x)} {F(cuts[i].y)}L{F(cuts[i + 1].x)} {F(cuts[i + 1].y)}");
                }
            }
            foreach (var entry in paths)
                if (entry.Value.Length > 0)
                    svg.Append($"<path d=\"{entry.Value}\" fill=\"none\" stroke=\"{(entry.Key == Sea ? "#1f3a5f" : "#5a4a30")}\" stroke-width=\"{(entry.Key == Sea ? "1.2" : "0.35")}\" stroke-opacity=\"{(entry.Key == Sea ? "1" : "0.6")}\"/>\n");
        }
    }
}
