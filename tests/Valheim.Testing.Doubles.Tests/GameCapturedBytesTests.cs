using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Valheim.Testing.Doubles;
using Xunit;

// The doubles against bytes the game itself wrote (issue #15): GameBytes/zpackage-capture.tsv comes from the capture plugin
// (tests/Valheim.Testing.Doubles.GameCapture) running ZPackageCases in a real 1.0.16 server, so a hand-derived expectation
// in RpcTests cannot agree with the double and still be wrong. Recapture after a game update changes the encoding.
public sealed class GameCapturedBytesTests
{
    private sealed record Captured(string Kind, string Hex);

    private static Dictionary<string, Captured> Capture()
    {
        var captured = new Dictionary<string, Captured>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GameBytes", "zpackage-capture.tsv")))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Length == 0) continue;
            string[] fields = line.Split('\t');
            Assert.True(fields.Length == 3, "A capture line has kind, name and bytes: " + line);
            captured.Add(fields[1], new(fields[0], fields[2]));
        }
        return captured;
    }

    private static string Bytes(ZPackageCase c) { var pkg = new ZPackage(); c.Write(pkg); return ZPackageCases.Hex(pkg.GetArray()); }

    // The bytes must come from the game the doubles copy: a game update that is not recaptured fails here (#308).
    // MemberIndexTests.TheCaptureCoversExactlyTheIndex does the same for the member capture.
    [Fact] public void TheCaptureComesFromTheGameTheDoublesCopy()
    {
        string header = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "GameBytes", "zpackage-capture.tsv"))
            .First(l => l.StartsWith("# game ", StringComparison.Ordinal));
        Assert.StartsWith("# game " + DoubledGame.Version + ",", header);
    }

    [Fact] public void TheCaptureHasExactlyTheCasesThisSourceDefines()
    {
        var captured = Capture();
        Assert.Equal(ZPackageCases.Exact().Select(c => "exact " + c.Name).Concat(ZPackageCases.Compressed().Select(c => "compressed " + c.Name)).OrderBy(n => n, StringComparer.Ordinal),
            captured.Select(e => e.Value.Kind + " " + e.Key).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(captured.Values, c => c.Hex.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact] public void EveryExactCaseHasTheBytesTheGameWrote()
    {
        var captured = Capture();
        var mismatches = ZPackageCases.Exact().Where(c => captured[c.Name].Hex != Bytes(c))
            .Select(c => $"{c.Name}: game {captured[c.Name].Hex}, double {Bytes(c)}").ToList();
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    // Gzip output differs between runtimes, so the compressed bytes are not compared; each side must read the other's.
    // The game reading the doubles' stream is the exact case "read double-compressed inner".
    [Fact] public void TheDoublesReadWhatTheGameCompressed()
    {
        var game = new ZPackage(ZPackageCases.FromHex(Capture()["compressed inner"].Hex));
        Assert.Equal(ZPackageCases.Inner().GetArray(), game.ReadCompressedPackage().GetArray());
        Assert.Equal(game.Size(), game.GetPos());
    }
}
