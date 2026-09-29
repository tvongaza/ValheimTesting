// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// Terrain assertions over the compiler doubles: what a write changed, where, and whether neighbouring zones agree.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Valheim.Testing.Doubles
{
    /// <summary>One terrain vertex whose compiler state differs from a snapshot: its index, world position and what changed.</summary>
    public readonly struct TerrainChange
    {
        public int Index { get; }
        public float X { get; }
        public float Z { get; }
        /// <summary>Which of level, smooth, height flag, paint and paint flag changed, as a short list.</summary>
        public string What { get; }
        public TerrainChange(int index, float x, float z, string what) { Index = index; X = x; Z = z; What = what; }
        public override string ToString() => $"vertex {Index} at ({X:F2}, {Z:F2}): {What}";
    }

    /// <summary>
    /// A copy of one zone compiler's per-vertex state: level and smooth deltas, the modified-height flags, the paint
    /// mask (RGBA) and the modified-paint flags. Compare it with the compiler after a write to see exactly which
    /// vertices the write touched, including a flag set without a value change.
    /// </summary>
    public sealed partial class TerrainSnapshot
    {
        private readonly global::Heightmap _heightmap;
        private readonly float[] _level, _smooth;
        private readonly bool[] _modifiedHeight, _modifiedPaint;
        private readonly UnityEngine.Color[] _paint;

        private TerrainSnapshot(TerrainComp compiler)
        {
            _heightmap = compiler.m_hmap;
            _level = (float[])compiler.m_levelDelta.Clone(); _smooth = (float[])compiler.m_smoothDelta.Clone();
            _modifiedHeight = (bool[])compiler.m_modifiedHeight.Clone(); _modifiedPaint = (bool[])compiler.m_modifiedPaint.Clone();
            _paint = (UnityEngine.Color[])compiler.m_paintMask.Clone();
        }

        public static TerrainSnapshot Of(TerrainComp compiler) => new(compiler ?? throw new ArgumentNullException(nameof(compiler)));

        /// <summary>Every vertex of <paramref name="now"/> that differs from this snapshot, exactly (no tolerance).</summary>
        public List<TerrainChange> ChangesIn(TerrainComp now)
        {
            if (now.m_levelDelta.Length != _level.Length) throw new ArgumentException("The compiler has a different size from the snapshot.", nameof(now));
            var changes = new List<TerrainChange>();
            for (int i = 0; i < _level.Length; i++)
            {
                var what = new List<string>();
                if (now.m_levelDelta[i] != _level[i]) what.Add($"level {_level[i]:F3}->{now.m_levelDelta[i]:F3}");
                if (now.m_smoothDelta[i] != _smooth[i]) what.Add($"smooth {_smooth[i]:F3}->{now.m_smoothDelta[i]:F3}");
                if (now.m_modifiedHeight[i] != _modifiedHeight[i]) what.Add($"height flag {_modifiedHeight[i]}->{now.m_modifiedHeight[i]}");
                var a = _paint[i]; var b = now.m_paintMask[i];
                if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) what.Add($"paint ({a.r:F2},{a.g:F2},{a.b:F2},{a.a:F2})->({b.r:F2},{b.g:F2},{b.b:F2},{b.a:F2})");
                if (now.m_modifiedPaint[i] != _modifiedPaint[i]) what.Add($"paint flag {_modifiedPaint[i]}->{now.m_modifiedPaint[i]}");
                if (what.Count > 0) { var (x, z) = TerrainAssert.VertexPosition(_heightmap, now.m_width, i); changes.Add(new TerrainChange(i, x, z, string.Join(", ", what))); }
            }
            return changes;
        }
    }

    /// <summary>Assertions over terrain compiler doubles. Each throws <see cref="TerrainAssertException"/> naming the first offending vertices.</summary>
    public static partial class TerrainAssert
    {
        private const int Shown = 10;

        /// <summary>The compiler is exactly as it was in the snapshot.</summary>
        public static void Unchanged(TerrainSnapshot before, TerrainComp after, string what = "zone") =>
            Fail(before.ChangesIn(after), $"{what} changed");

        /// <summary>
        /// Every change since the snapshot lies where <paramref name="allowed"/> (world x, z) says a change may be; the
        /// rest of the zone, flags included, is exactly as it was. Also fails when nothing changed at all, since a check
        /// that passes on an untouched zone cannot tell a write from no write; pass <paramref name="requireChange"/> false
        /// when no change is the expectation.
        /// </summary>
        public static void OnlyChangedWithin(TerrainSnapshot before, TerrainComp after, Func<float, float, bool> allowed, string what = "zone", bool requireChange = true)
        {
            var changes = before.ChangesIn(after);
            if (requireChange && changes.Count == 0) throw new TerrainAssertException($"{what}: nothing changed, so the footprint check proves nothing.");
            Fail(changes.Where(c => !allowed(c.X, c.Z)).ToList(), $"{what} changed outside the allowed footprint");
        }

        /// <summary>
        /// Two loaded neighbouring zones agree on the vertices they share: rendered heights within
        /// <paramref name="tolerance"/> metres (rebuild both first) and identical paint. A zone written without its
        /// neighbour shows a step or a paint edge along the seam.
        /// </summary>
        public static void SeamAgrees(global::Heightmap a, global::Heightmap b, float tolerance = 0.001f)
        {
            if (a.m_terrainComp == null || b.m_terrainComp == null) throw new ArgumentException("Both zones need a terrain compiler.");
            if (a.LastRenderedHeights == null || b.LastRenderedHeights == null) throw new ArgumentException("Rebuild both zones (Heightmap.RebuildTerrain) before comparing their seam.");
            var shared = SharedVertices(a, b);
            if (shared.Count == 0) throw new ArgumentException("The zones do not share an edge.");
            var problems = new List<string>();
            foreach (var (ia, ib, x, z) in shared)
            {
                float ha = a.LastRenderedHeights[ia] + a.transform.position.y, hb = b.LastRenderedHeights[ib] + b.transform.position.y;
                if (Math.Abs(ha - hb) > tolerance) problems.Add($"({x:F2}, {z:F2}): height {ha:F3} vs {hb:F3}");
                var pa = a.m_terrainComp!.m_paintMask[ia]; var pb = b.m_terrainComp!.m_paintMask[ib];
                if (pa.r != pb.r || pa.g != pb.g || pa.b != pb.b || pa.a != pb.a)
                    problems.Add($"({x:F2}, {z:F2}): paint ({pa.r:F2},{pa.g:F2},{pa.b:F2},{pa.a:F2}) vs ({pb.r:F2},{pb.g:F2},{pb.b:F2},{pb.a:F2})");
            }
            if (problems.Count > 0)
                throw new TerrainAssertException($"Seam disagrees at {problems.Count} of {shared.Count} shared vertices: " + string.Join("; ", problems.Take(Shown)) + (problems.Count > Shown ? "; ..." : ""));
        }

        /// <summary>The world x, z of a compiler vertex (row-major, (width + 1) per row, centred on the heightmap).</summary>
        public static (float X, float Z) VertexPosition(global::Heightmap heightmap, int width, int index)
        {
            int x = index % (width + 1), z = index / (width + 1);
            return (heightmap.transform.position.x + (x - width / 2f) * heightmap.m_scale, heightmap.transform.position.z + (z - width / 2f) * heightmap.m_scale);
        }

        private static List<(int A, int B, float X, float Z)> SharedVertices(global::Heightmap a, global::Heightmap b)
        {
            int wa = a.m_terrainComp!.m_width, wb = b.m_terrainComp!.m_width;
            var byPosition = new Dictionary<(float, float), int>();
            for (int i = 0; i < (wb + 1) * (wb + 1); i++) byPosition[VertexPosition(b, wb, i)] = i;
            var shared = new List<(int, int, float, float)>();
            for (int i = 0; i < (wa + 1) * (wa + 1); i++)
            {
                var p = VertexPosition(a, wa, i);
                if (byPosition.TryGetValue(p, out int j)) shared.Add((i, j, p.X, p.Z));
            }
            return shared;
        }

        private static void Fail(List<TerrainChange> changes, string message)
        {
            if (changes.Count == 0) return;
            throw new TerrainAssertException($"{message} at {changes.Count} vertices: " + string.Join("; ", changes.Take(Shown)) + (changes.Count > Shown ? "; ..." : ""));
        }
    }

    public sealed class TerrainAssertException : Exception
    {
        public TerrainAssertException(string message) : base(message) { }
    }
}
