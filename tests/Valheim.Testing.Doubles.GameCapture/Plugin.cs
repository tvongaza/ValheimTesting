using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace Valheim.Testing.Doubles.GameCapture;

/// <summary>
/// Writes each <see cref="ZPackageCases"/> case into a new package with the game's own code and saves the bytes to
/// <c>BepInEx/zpackage-capture.tsv</c>: one <c>exact</c> or <c>compressed</c> line per case, then its name and bytes. The
/// header records the game version and the runtime. It runs once, in <c>Awake</c>, where the game's assemblies are
/// loaded and no world is needed. Test runtimes only.
/// </summary>
[BepInPlugin(Guid, "ValheimTesting: ZPackage byte capture", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "valheimtesting.doubles.gamecapture";

    private void Awake()
    {
        var lines = new List<string>
        {
            "# Bytes written by the game's own ZPackage and ZRpc for each case in ZPackageCases.cs (tests/Valheim.Testing.Doubles.GameCapture).",
            "# game " + global::Version.GetVersionString() + ", runtime " + Environment.Version + ", " + Environment.OSVersion,
        };
        int failed = 0;
        void Capture(string kind, IReadOnlyList<ZPackageCase> cases)
        {
            foreach (var c in cases)
            {
                try { var pkg = new ZPackage(); c.Write(pkg); lines.Add(kind + "\t" + c.Name + "\t" + ZPackageCases.Hex(pkg.GetArray())); }
                catch (Exception error) { failed++; lines.Add(kind + "\t" + c.Name + "\tERROR " + error.GetType().Name + ": " + error.Message.Replace('\t', ' ').Replace('\n', ' ')); }
            }
        }
        Capture("exact", ZPackageCases.Exact());
        Capture("compressed", ZPackageCases.Compressed());
        string path = Path.Combine(Paths.BepInExRootPath, "zpackage-capture.tsv");
        File.WriteAllLines(path, lines);
        Logger.LogInfo($"ZPackage capture: {lines.Count - 2} cases ({failed} failed) written to {path}");
    }
}
