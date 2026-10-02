using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;

/// <summary>
/// <c>valheim-test copies ROOT</c> lists the copies runs left under ROOT and changes nothing; <c>--remove COPY</c> (repeated)
/// removes exactly those, keeping what each run changed beside it. See <see cref="OwnedCopies"/> for what counts as a copy.
/// </summary>
internal static class CopiesCommand
{
    internal const string Usage = "valheim-test copies ROOT [--json] | valheim-test copies ROOT --remove COPY [--remove COPY ...] [--allow-world]";

    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        int Fail(string message) { error.WriteLine(message); error.WriteLine("Usage: " + Usage); return 2; }
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) return Fail("Name the directory to look under.");
        string root = Path.GetFullPath(args[0]);
        var remove = new List<string>();
        bool json = false, allowWorld = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json") json = true;
            else if (args[i] == "--allow-world") allowWorld = true;
            else if (args[i] == "--remove" && i + 1 < args.Length) remove.Add(Path.GetFullPath(args[++i]));
            else return Fail("Unknown or incomplete option: " + args[i]);
        }
        if (remove.Count != 0 && json) return Fail("--json lists; it does not combine with --remove.");
        if (allowWorld && remove.Count == 0) return Fail("--allow-world only applies to --remove.");
        if (!Directory.Exists(root)) { error.WriteLine("REFUSED: no such directory: " + root); return 3; }
        if (remove.Count == 0) { List(root, json, output); return 0; }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int refused = 0;
        foreach (string path in remove)
        {
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
            { error.WriteLine($"REFUSED {path}: not under {root}"); refused++; continue; }
            try { output.WriteLine("REMOVED " + OwnedCopies.Remove(path, allowWorld)); }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { error.WriteLine($"REFUSED {path}: {failure.Message}"); refused++; }
        }
        return refused == 0 ? 0 : 3;
    }

    private static void List(string root, bool json, TextWriter output)
    {
        var copies = OwnedCopies.Find(root);
        if (json) { output.WriteLine(JsonSerializer.Serialize(copies, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })); return; }
        output.WriteLine($"{"KIND",-14} {"SIZE",9} {"AGE",7} {"IN USE",-12} {"RUN",-8} PATH");
        foreach (var copy in copies)
            output.WriteLine($"{copy.Kind,-14} {DiskSpace.Format(copy.Bytes),9} {Age(copy.CreatedUtc),7} {(copy.InUse ? "pid " + string.Join(",", copy.InUseBy) : "no"),-12} " +
                $"{(copy.Passed switch { true => "passed", false => "failed", null => copy.Result == null ? "none" : "unknown" }),-8} {copy.Path}");
        var idle = copies.Where(copy => !copy.InUse && copy.Kind is OwnedCopyKind.ServerRuntime or OwnedCopyKind.ClientRuntime).ToList();
        // A copy with no run result belongs to a run still going or one that was killed: named, never suggested.
        var unfinished = idle.Where(copy => copy.Result == null).ToList();
        var removable = idle.Except(unfinished).ToList();
        output.WriteLine($"{copies.Count} copies, {DiskSpace.Format(copies.Sum(copy => copy.Bytes))}. Nothing was changed.");
        if (unfinished.Count != 0)
            output.WriteLine($"{unfinished.Count} game copies have no run result ({DiskSpace.Format(unfinished.Sum(copy => copy.Bytes))}): a run still going, or one that was killed. " +
                "Make sure their run has ended before removing one by name.");
        if (removable.Count == 0) return;
        output.WriteLine($"{removable.Count} game copies not in use ({DiskSpace.Format(removable.Sum(copy => copy.Bytes))}); after checking each one's run, remove it with:");
        foreach (var copy in removable) output.WriteLine($"  valheim-test copies \"{root}\" --remove \"{copy.Path}\"");
    }

    private static string Age(DateTime createdUtc)
    {
        var age = DateTime.UtcNow - createdUtc;
        return age.TotalDays >= 1 ? age.TotalDays.ToString("0.0", CultureInfo.InvariantCulture) + "d" : age.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + "h";
    }
}
