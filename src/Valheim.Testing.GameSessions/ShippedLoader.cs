using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The pinned BepInExPack every Windows and Linux profile runs on (#711): loader-dependency.json and its zip, both embedded in
/// Valheim.Testing.GameSessions. A profile never inspects or inherits its install's loader. A macOS profile keeps its explicit
/// arm64 package, or its install's native loader. The install is never changed; the run prints one line and records which
/// pin it used.
/// </summary>
internal static class ShippedLoader
{
    /// <summary>The captured package manifest an actor's profile uses, and the line that says which pin it came from.</summary>
    internal sealed record Choice(string Manifest, string Reason);

    /// <summary>The choice for an actor's install on a host of <paramref name="platform"/> (<c>windows</c>, <c>linux</c> or <c>macos</c>).</summary>
    internal delegate Choice? Rule(string actor, string install, string platform);

    /// <summary>A pin, where it came from (for evidence), and its zip (null when this build carries none).</summary>
    internal sealed record Pinned(LoaderPin Pin, string Source, Func<Stream?> Zip);

    /// <summary>The pinned choice for an actor that names no loader package (the <see cref="Rule"/> every caller passes).</summary>
    internal static Choice? Choose(string actor, string install, string platform) => For(actor, install, platform, BuiltIn());

    /// <summary>The pinned choice for an install on this machine.</summary>
    internal static Choice? Local(string actor, string install) => Choose(actor, install, HostProfile.CurrentPlatform);

    /// <summary>
    /// The pinned package for <paramref name="actor"/>'s profile on a <paramref name="platform"/> host; null on macOS. A run prints
    /// its reason (<see cref="Print"/>); a read-only check stays quiet. Refuses when the pin has no zip: a profile never falls
    /// back to its install's loader.
    /// </summary>
    internal static Choice? For(string actor, string install, string platform, Pinned pinned, string? dataRoot = null)
    {
        if (platform == "macos") return null;
        if (platform is not ("windows" or "linux")) throw new ArgumentException($"No pinned loader for platform {platform}.", nameof(platform));
        using var zip = pinned.Zip() ?? throw new InvalidDataException(
            $"This build of Valheim.Testing.GameSessions carries no pinned {pinned.Pin.Name} ({pinned.Source}): run dotnet run scripts/bootstrap-cli.cs before building, " +
            "or name a reviewed loader package. A Windows or Linux profile never runs on its install's own loader.");
        string manifest = Extract(zip, pinned.Pin, dataRoot ?? CliBundle.DataRoot);
        if (platform == "linux")
        {
            manifest = Path.Combine(Path.GetDirectoryName(manifest)!, LinuxManifest);
            if (!File.Exists(manifest)) throw new InvalidDataException($"The pinned {pinned.Pin.Name} {pinned.Pin.Version} ({pinned.Source}) has no Linux loader ({BepInExLoader.LinuxDoorstop}).");
        }
        string identity = BepInExLoaderPackage.Read(manifest).Identity;
        string reason = $"{install}'s disposable profile runs on the pinned {identity}, sha256 {pinned.Pin.Sha256.ToLowerInvariant()}, from {pinned.Source}; " +
            "the install's own loader is not used and the install is not changed";
        return new Choice(manifest, reason);
    }

    /// <summary>The one line a run prints for an actor's pinned loader.</summary>
    internal static void Print(string actor, Choice choice) => Console.WriteLine($"{actor} loader: {choice.Reason}");

    /// <summary>The toolkit's pin, loader-dependency.json, embedded with its zip when the build fetched it.</summary>
    internal static Pinned BuiltIn()
    {
        var assembly = typeof(ShippedLoader).Assembly; // Valheim.Testing.GameSessions embeds the pack and its pin.
        using var pinStream = assembly.GetManifestResourceStream("loader-dependency.json")
            ?? throw new InvalidDataException("This build of Valheim.Testing.GameSessions carries no loader-dependency.json.");
        using var pin = JsonDocument.Parse(pinStream);
        string Text(string name) => pin.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("loader-dependency.json has no " + name + ".");
        return new Pinned(new LoaderPin(Text("name"), Text("version"), Text("sha256"), Text("root")), "loader-dependency.json",
            () => assembly.GetManifestResourceStream("bepinexpack-valheim.zip"));
    }

    /// <summary>The manifest beside <c>loader.json</c> for a Linux profile: the same pack without the Windows proxy and its configuration.</summary>
    internal const string LinuxManifest = "loader-linux.json";

    internal sealed record LoaderPin(string Name, string Version, string Sha256, string Root);

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
            // A Linux profile takes the same pack without the Windows proxy and its configuration, which a bash host refuses.
            var files = json["files"]!.AsObject();
            if (files.ContainsKey(BepInExLoader.LinuxDoorstop))
            {
                foreach (string windowsOnly in new[] { BepInExLoader.WindowsProxy, BepInExLoader.WindowsConfig }) files.Remove(windowsOnly);
                File.WriteAllText(Path.Combine(staging, LinuxManifest), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            if (!Current(staged)) throw new InvalidDataException($"The shipped {pin.Name} {pin.Version} is not a usable loader package.");
        }, $"shipped {pin.Name} {pin.Version}");
        return manifest;
    }

    // A missing or changed copy is not current; any other IO error (a file another process holds) goes to ExtractOnce,
    // which treats it as "not current" outside the lock and as a failure under it, so a copy it cannot read is never replaced.
    private static bool Current(string manifest)
    {
        if (!File.Exists(manifest)) return false;
        try
        {
            var package = BepInExLoaderPackage.Read(manifest);
            string linux = Path.Combine(Path.GetDirectoryName(manifest)!, LinuxManifest);
            if (package.Files.ContainsKey(BepInExLoader.LinuxDoorstop)) _ = BepInExLoaderPackage.Read(linux);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or ArgumentException or InvalidOperationException or JsonException) { return false; }
    }
}
