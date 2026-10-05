using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing.Game;

/// <summary>
/// The BepInExPack this tool ships (loader-dependency.json and its zip, both embedded): a disposable copy takes it when its
/// install's own Doorstop proxy and configuration do not match (a mod manager that swaps the proxy leaves them so), and
/// for no other loader fault. The install is never changed; the run prints one line and records which package it used.
/// </summary>
internal static class ShippedLoader
{
    /// <summary>Why the loader was chosen for an actor, and the captured package manifest it uses.</summary>
    internal sealed record Choice(string Manifest, string Reason);

    /// <summary>
    /// The shipped package for <paramref name="actor"/>'s <paramref name="install"/> when its Doorstop pair does not match,
    /// printed; null when the pair matches, the install is not a Windows Doorstop install, or this tool carries no package
    /// (then the preflight's own refusal stands).
    /// </summary>
    internal static Choice? Instead(string actor, string install) => Instead(actor, install, Shipped, CliBundle.DataRoot);

    internal static Choice? Instead(string actor, string install, Func<(Stream Zip, LoaderPin Pin)?> shipped, string dataRoot)
    {
        string? mismatch = BepInExLoaderPackage.DoorstopMismatch(install);
        if (mismatch == null) return null;
        var found = shipped();
        if (found is not { } pack) return null;
        using (pack.Zip)
        {
            string manifest = Extract(pack.Zip, pack.Pin, dataRoot);
            string identity = BepInExLoaderPackage.Read(manifest).Identity;
            string reason = $"{install}'s Doorstop proxy and configuration do not match ({mismatch}); its disposable copy takes valheim-test's {identity}, and the install is not changed";
            Console.WriteLine($"{actor} loader: {reason}");
            return new Choice(manifest, reason);
        }
    }

    internal sealed record LoaderPin(string Name, string Version, string Sha256, string Root);

    private static (Stream Zip, LoaderPin Pin)? Shipped()
    {
        var tool = typeof(ShippedLoader).Assembly;
        var zip = tool.GetManifestResourceStream("bepinexpack-valheim.zip");
        using var pinStream = tool.GetManifestResourceStream("loader-dependency.json");
        if (zip == null || pinStream == null) { zip?.Dispose(); return null; }
        using var pin = JsonDocument.Parse(pinStream);
        string Text(string name) => pin.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("loader-dependency.json has no " + name + ".");
        return (zip, new LoaderPin(Text("name"), Text("version"), Text("sha256"), Text("root")));
    }

    /// <summary>
    /// The pack under <paramref name="dataRoot"/>/loader/&lt;name&gt;-&lt;version&gt;-&lt;sha256 prefix&gt;, captured as a loader package
    /// (<c>loader.json</c> beside it). An existing copy is reused while <see cref="BepInExLoaderPackage.Read"/> still accepts it
    /// (every pinned file unchanged); otherwise the pack's folder is extracted again beside it and swapped in. A zip of another
    /// hash is refused.
    /// </summary>
    internal static string Extract(Stream zip, LoaderPin pin, string dataRoot)
    {
        using var buffer = new MemoryStream();
        zip.CopyTo(buffer);
        string found = FileHash.Sha256(buffer.ToArray());
        if (!found.Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The shipped {pin.Name} {pin.Version} is sha256 {found}, not the pinned {pin.Sha256.ToLowerInvariant()}.");
        string folder = Path.Combine(dataRoot, "loader", $"{pin.Name}-{pin.Version}-{found[..12]}");
        string manifest = Path.Combine(folder, "loader.json");
        if (Usable(manifest)) return manifest;

        string staging = folder + ".extract-" + Guid.NewGuid().ToString("N");
        string prefix = pin.Root.TrimEnd('/') + "/";
        try
        {
            buffer.Position = 0;
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
                foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && entry.Name.Length != 0))
                {
                    string target = Path.GetFullPath(Path.Combine(staging, entry.FullName[prefix.Length..]));
                    if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException($"The shipped {pin.Name} holds {entry.FullName}, which is outside its folder.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            if (!Directory.Exists(staging)) throw new InvalidDataException($"The shipped {pin.Name} has no {prefix} folder.");
            // Captured in place, with its root written as the manifest's own folder ("."), so the folder and its manifest move
            // in together and a run never sees one without the other.
            string staged = Path.Combine(staging, "loader.json");
            BepInExLoaderPackage.Capture(staging, pin.Name, pin.Version).Write(staged);
            var json = JsonNode.Parse(File.ReadAllText(staged))!.AsObject();
            json["root"] = ".";
            File.WriteAllText(staged, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            // Another run may have extracted the same pack meanwhile: use its copy rather than replace it under it.
            if (Usable(manifest)) return manifest;
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            if (Directory.Exists(folder))
            {
                string old = folder + ".old-" + Guid.NewGuid().ToString("N");
                try { Directory.Move(folder, old); } catch (IOException) when (Usable(manifest)) { return manifest; }
                try { Directory.Delete(old, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            try { Directory.Move(staging, folder); }
            catch (IOException) when (Usable(manifest)) { return manifest; }
            if (!Usable(manifest)) throw new InvalidDataException($"The shipped {pin.Name} {pin.Version} at {folder} is not a usable loader package.");
            return manifest;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static bool Usable(string manifest)
    {
        if (!File.Exists(manifest)) return false;
        try { _ = BepInExLoaderPackage.Read(manifest); return true; }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or JsonException) { return false; }
    }
}
