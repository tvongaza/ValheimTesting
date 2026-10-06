using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Valheim.Testing.Game;

/// <summary>
/// The pinned BepInExPack the toolkit ships (loader-dependency.json and its zip, both embedded in Valheim.Testing.GameSessions): a disposable
/// copy takes it when its install's own Doorstop proxy and configuration do not match (a mod manager that swaps the proxy leaves them so), or when
/// its BepInEx is older than <see cref="MinimumBepInEx"/>, and for no other loader fault. The install is never changed; the
/// run prints one line and records which package it used.
/// </summary>
internal static class ShippedLoader
{
    /// <summary>Why the loader was chosen for an actor, and the captured package manifest it uses.</summary>
    internal sealed record Choice(string Manifest, string Reason);

    /// <summary>
    /// The shipped package for <paramref name="actor"/>'s <paramref name="install"/> when its Doorstop pair does not match,
    /// printed; null when the pair matches, the install is not a Windows Doorstop install, or this build carries no package
    /// (then the preflight's own refusal stands).
    /// </summary>
    internal static Choice? Instead(string actor, string install) => Instead(actor, install, Shipped, CliBundle.DataRoot);

    /// <summary>
    /// The oldest BepInEx a disposable copy keeps. Every v5 build before it finds none of Unity's log-writer methods on Unity
    /// 2023.2 and later (Valheim's Unity 6), so its plugins' lines never reach Unity's log and it logs "Unable to start Unity log
    /// writer" at startup (BepInEx/BepInEx#755); BepInEx/BepInEx#1264 fixed that in 5.4.23.5, which the shipped pack carries.
    /// </summary>
    internal static readonly Version MinimumBepInEx = new(5, 4, 23, 5);

    /// <summary>
    /// Why the install's BepInEx core is too old to keep (<see cref="MinimumBepInEx"/>), or null: new enough, not v5, unreadable,
    /// or a macOS install, whose native arm64 loader the pack's x64 Doorstop library cannot replace.
    /// </summary>
    internal static string? OutdatedCore(string install)
    {
        if (Directory.Exists(Path.Combine(install, GameLaunch.ClientMacBundle)) || File.Exists(Path.Combine(install, GameLaunch.ServerMacExecutable))) return null;
        string library = Path.Combine(install, InstallPins.CoreDirectory, "BepInEx.dll");
        if (!File.Exists(library)) return null;
        Version? version;
        try { version = System.Reflection.AssemblyName.GetAssemblyName(library).Version; }
        catch (Exception error) when (error is BadImageFormatException or FileLoadException or IOException) { return null; }
        if (version is not { Major: 5 } || version >= MinimumBepInEx) return null;
        return $"BepInEx {version} is older than {MinimumBepInEx}, which on Unity 6 cannot reach Unity's log writer " +
            "(BepInEx/BepInEx#755, fixed by #1264 in 5.4.23.5)";
    }

    internal static Choice? Instead(string actor, string install, Func<(Stream Zip, LoaderPin Pin)?> shipped, string dataRoot)
    {
        string? mismatch = BepInExLoaderPackage.DoorstopMismatch(install), outdated = OutdatedCore(install);
        if (mismatch == null && outdated == null) return null;
        var found = shipped();
        if (found is not { } pack) return null;
        using (pack.Zip)
        {
            string manifest = Extract(pack.Zip, pack.Pin, dataRoot);
            string identity = BepInExLoaderPackage.Read(manifest).Identity;
            string why = string.Join("; and ", new[] { mismatch == null ? null : $"Doorstop proxy and configuration do not match ({mismatch})", outdated }.OfType<string>());
            string reason = $"{install}'s {why}; its disposable copy takes the toolkit's pinned {identity}, and the install is not changed";
            Console.WriteLine($"{actor} loader: {reason}");
            return new Choice(manifest, reason);
        }
    }

    internal sealed record LoaderPin(string Name, string Version, string Sha256, string Root);

    private static (Stream Zip, LoaderPin Pin)? Shipped()
    {
        var assembly = typeof(ShippedLoader).Assembly; // Valheim.Testing.GameSessions embeds the pack and its pin.
        LoaderPin found;
        using (var pinStream = assembly.GetManifestResourceStream("loader-dependency.json"))
        {
            if (pinStream == null) return null;
            using var pin = JsonDocument.Parse(pinStream);
            string Text(string name) => pin.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("loader-dependency.json has no " + name + ".");
            found = new LoaderPin(Text("name"), Text("version"), Text("sha256"), Text("root"));
        }
        // The pin is read first, so a malformed one leaves no open stream behind.
        return assembly.GetManifestResourceStream("bepinexpack-valheim.zip") is { } zip ? (zip, found) : null;
    }

    /// <summary>
    /// The pack under <paramref name="dataRoot"/>/loader/&lt;name&gt;-&lt;version&gt;-&lt;sha256 prefix&gt;, captured as a loader package
    /// (<c>loader.json</c> beside it). An existing copy is reused while <see cref="BepInExLoaderPackage.Read"/> still accepts it
    /// (every pinned file unchanged); otherwise the pack's folder is extracted again beside it and swapped in, one run at a time
    /// (<c>&lt;folder&gt;.lock</c> beside it), so concurrent runs share one copy and never replace a current one. A zip of another
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
        string prefix = pin.Root.TrimEnd('/') + "/";
        // One extraction at a time for this folder, across processes (<folder>.lock beside it; ExtractOnce, which CliBundle uses
        // too, owns the swap): concurrent runs share one copy and a current copy is never replaced.
        ExtractOnce.Ensure(folder, copy => Current(Path.Combine(copy, "loader.json")), staging =>
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
            if (!Current(staged)) throw new InvalidDataException($"The shipped {pin.Name} {pin.Version} is not a usable loader package.");
        }, $"shipped {pin.Name} {pin.Version}");
        return manifest;
    }

    // A missing or changed copy is not current; any other IO error (a file another process holds) goes to ExtractOnce,
    // which treats it as "not current" outside the lock and as a failure under it, so a copy it cannot read is never replaced.
    private static bool Current(string manifest)
    {
        if (!File.Exists(manifest)) return false;
        try { _ = BepInExLoaderPackage.Read(manifest); return true; }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or ArgumentException or InvalidOperationException or JsonException) { return false; }
    }
}
