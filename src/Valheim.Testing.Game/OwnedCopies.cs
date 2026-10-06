using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>What an owned copy holds, from its contents.</summary>
public enum OwnedCopyKind { ServerRuntime, ClientRuntime, World, Other }

/// <summary>
/// A copy a run left behind: where it is, what it holds, its size, when it was made, which running processes use it, and
/// the result of the run it belongs to (the nearest <c>result.json</c>), when there is one.
/// </summary>
public sealed record OwnedCopy(string Path, OwnedCopyKind Kind, long Bytes, DateTime CreatedUtc, IReadOnlyList<int> InUseBy, string? Result, bool? Passed)
{
    public bool InUse => InUseBy.Count != 0;
}

/// <summary>
/// Finds and removes the copies runs leave behind. A copy is recognised only by what <see cref="WorldFixture"/> writes: a
/// directory named <c>valheim-test-</c> and 32 hex digits holding <c>fixture-provenance.json</c>, the manifest of what was
/// copied into it. Nothing else under a root is ever listed or removed, and links are never followed. <see cref="Find"/>
/// only reads. <see cref="Remove"/> takes one copy at a time by path, refuses one a running process uses and refuses a
/// world copy (a run's save) unless asked, and keeps what the run changed in the copy beside it (<see cref="WorldFixture.Retire"/>).
/// </summary>
public static class OwnedCopies
{
    private static readonly Regex CopyName = new("^valheim-test-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private const string Provenance = "fixture-provenance.json";

    // Test seam: the running processes and their executables, in place of this machine's. Async-local for parallel tests.
    private static readonly AsyncLocal<Func<IReadOnlyList<(int Pid, string Executable)>>?> s_processesOverride = new();
    internal static Func<IReadOnlyList<(int Pid, string Executable)>>? ProcessesOverride { get => s_processesOverride.Value; set => s_processesOverride.Value = value; }

    /// <summary>Every owned copy under <paramref name="root"/>, largest first. Reads only.</summary>
    public static IReadOnlyList<OwnedCopy> Find(string root)
    {
        root = System.IO.Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("No such directory: " + root);
        var processes = Processes();
        var holders = RunJournal.LocalHolders();
        var found = new List<OwnedCopy>();
        void Walk(string directory)
        {
            string[] children;
            // EnumerateDirectories is lazy: the read must happen inside the catch for an inaccessible subtree.
            try { children = Directory.GetDirectories(directory); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return; }
            foreach (string child in children)
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                if (IsCopy(child)) found.Add(Describe(child, root, processes, holders));
                else Walk(child);
            }
        }
        if (IsCopy(root)) found.Add(Describe(root, root, processes, holders)); else Walk(root);
        return found.OrderByDescending(copy => copy.Bytes).ThenBy(copy => copy.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Removes one owned copy, keeping every file its run added or changed in <c>&lt;copy&gt;-changes</c> beside it (with
    /// <c>changes.json</c>). Refuses a path that is not an owned copy, one a running process uses, and a world copy unless
    /// <paramref name="allowWorld"/>. A copy whose run did not pass keeps larger files (<see cref="PinnedServerRun"/>'s failure limits).
    /// </summary>
    public static RetiredCopy Remove(string path, bool allowWorld = false)
    {
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!IsCopy(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException($"Not a copy the toolkit made (a valheim-test-<32 hex> directory with {Provenance}): {path}", nameof(path));
        var copy = Describe(path, System.IO.Path.GetPathRoot(path)!, Processes(), RunJournal.LocalHolders());
        if (copy.InUse) throw new InvalidOperationException($"Process {string.Join(", ", copy.InUseBy)} runs from {path}; stop it before removing the copy.");
        if (copy.Kind == OwnedCopyKind.World && !allowWorld)
            throw new InvalidOperationException($"{path} is a world copy, a run's save: kept unless removing worlds is asked for explicitly.");
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(System.IO.Path.Combine(path, Provenance)))
            ?? throw new InvalidDataException("Empty manifest: " + System.IO.Path.Combine(path, Provenance));
        if (hashes.Count == 0 || hashes.Values.Any(hash => hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid or empty copy manifest: " + System.IO.Path.Combine(path, Provenance));
        var (perFile, total) = PinnedServerRun.RetainLimits(copy.Passed == true);
        // A retry after a removal that could not finish keeps its changes in a new folder beside the first.
        string keep = path + "-changes";
        for (int attempt = 2; System.IO.Path.Exists(keep); attempt++) keep = path + "-changes-" + attempt;
        return WorldFixture.Existing(path, hashes).Retire(keep, perFile, total);
    }

    /// <summary>Whether <paramref name="directory"/> is named as a copy is (<c>valheim-test-</c> and 32 hex digits), finished or not.</summary>
    internal static bool IsCopyName(string directory) => CopyName.IsMatch(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(directory)));

    private static bool IsCopy(string directory) =>
        CopyName.IsMatch(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(directory))) && File.Exists(System.IO.Path.Combine(directory, Provenance));

    private static OwnedCopy Describe(string copy, string root, IReadOnlyList<(int Pid, string Executable)> processes, IReadOnlyDictionary<string, int> holders)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // The system reports an executable's path with links resolved (on macOS /private/var/... for /var/...): match either form.
        string[] forms = [copy + System.IO.Path.DirectorySeparatorChar, Resolved(copy) + System.IO.Path.DirectorySeparatorChar];
        var users = processes.Where(process => forms.Any(form => process.Executable.StartsWith(form, comparison)))
            .Select(process => process.Pid).Append(LiveOwner(copy) ?? -1)
            // The run that made the copy holds it while its process runs (this machine's journal), before any game runs from it.
            .Append(holders.TryGetValue(copy, out int holder) || holders.TryGetValue(Resolved(copy), out holder) ? holder : -1)
            .Where(pid => pid >= 0).Distinct().Order().ToList();
        var (result, passed) = RunResult(copy, root);
        return new(copy, KindOf(copy), DiskSpace.DirectoryBytes(copy), File.GetLastWriteTimeUtc(System.IO.Path.Combine(copy, Provenance)), users, result, passed);
    }

    // The path with every linked component resolved, as the system names a running executable. Windows paths stay as given.
    internal static string Resolved(string path, int depth = 0)
    {
        path = System.IO.Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() || depth > 32) return path; // 32 links deep is a loop
        string current = System.IO.Path.GetPathRoot(path)!;
        foreach (string part in path[current.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, part);
            // A link's target may itself pass through links (a link to /var/... on macOS): resolve the target in turn.
            try { if (new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target) current = Resolved(target.FullName, depth + 1); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return current;
    }

    // The process that made the copy and still holds it (WorldFixture's owner record), when that process still runs: the
    // same id and the same start time, so a reused id does not count. One whose start time cannot be read counts as running.
    private static int? LiveOwner(string copy)
    {
        string file = WorldFixture.OwnerFile(copy);
        if (!File.Exists(file)) return null;
        CopyOwner? owner;
        try { owner = JsonSerializer.Deserialize<CopyOwner>(File.ReadAllText(file)); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return null; }
        if (owner == null) return null;
        try
        {
            using var process = Process.GetProcessById(owner.Pid);
            try { if (Math.Abs((process.StartTime.ToUniversalTime() - owner.StartedUtc).TotalSeconds) > 2) return null; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            return process.HasExited ? null : owner.Pid;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; } // no such process
    }

    private static OwnedCopyKind KindOf(string copy)
    {
        bool Has(string relative) => File.Exists(System.IO.Path.Combine(copy, relative)) || Directory.Exists(System.IO.Path.Combine(copy, relative));
        if (Has(GameLaunch.ServerWindowsExecutable) || Has(GameLaunch.ServerLinuxExecutable) || Has(GameLaunch.ServerMacExecutable)) return OwnedCopyKind.ServerRuntime;
        if (Has(GameLaunch.ClientWindowsExecutable) || Has(GameLaunch.ClientLinuxExecutable) || Has(GameLaunch.ClientMacBundle)) return OwnedCopyKind.ClientRuntime;
        // A world: a save directory, or world files in either layout the game loads (the older .fwl/.db pair, or 1.0's
        // chunked <name>/_main.N.fwl2 and .db2), at the top or one directory down.
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        if (Has("worlds_local") || Has("worlds") || Directory.EnumerateFiles(copy, "*", options).Any(file =>
                System.IO.Path.GetExtension(file).ToLowerInvariant() is ".fwl" or ".fwl2" or ".db" or ".db2"))
            return OwnedCopyKind.World;
        return OwnedCopyKind.Other;
    }

    // The run a copy belongs to: the nearest result.json in the copy's parent or the parent above it, or their evidence/
    // folder (PinnedServerRun writes its copies beside result.json; NativeSmoke stages copies beside evidence/). Never above the root.
    private static (string? Path, bool? Passed) RunResult(string copy, string root)
    {
        string? directory = System.IO.Path.GetDirectoryName(copy);
        for (int up = 0; up < 2 && directory != null && directory.Length >= root.Length; up++, directory = System.IO.Path.GetDirectoryName(directory))
            foreach (string candidate in new[] { System.IO.Path.Combine(directory, "result.json"), System.IO.Path.Combine(directory, "evidence", "result.json") })
                if (File.Exists(candidate))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                        return (candidate, document.RootElement.TryGetProperty("Passed", out var passed) && passed.ValueKind is JsonValueKind.True or JsonValueKind.False ? passed.GetBoolean() : null);
                    }
                    catch (JsonException) { return (candidate, null); }
                }
        return (null, null);
    }

    // Each running process's executable, where the system lets this user read it. On Windows that includes some processes of
    // other users and elevated ones (their image path needs only limited query access), which then count as using a copy.
    internal static IReadOnlyList<(int Pid, string Executable)> Processes()
    {
        if (ProcessesOverride is { } fake) return fake();
        var list = new List<(int, string)>();
        char[] buffer = new char[1024];
        foreach (var process in Process.GetProcesses())
            using (process)
                if (Executable(process, ref buffer) is { Length: > 0 } file) list.Add((process.Id, System.IO.Path.GetFullPath(file)));
        return list;
    }

    // Windows and Linux ask for the image path alone. MainModule lists every module of the process first: a scan of 284
    // processes took 1.7 s that way on a Windows machine, against 0.03 s for the image path, which names the same file in the
    // same form (an 8.3 path stays one). On Linux both read /proc/<pid>/exe. macOS keeps MainModule.
    private static string? Executable(Process process, ref char[] buffer)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsImage.Path(process.Id, ref buffer);
            if (OperatingSystem.IsLinux()) return new FileInfo($"/proc/{process.Id}/exe").LinkTarget;
            return process.MainModule?.FileName;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException
                                          or UnauthorizedAccessException or IOException) { return null; }
    }

    private static class WindowsImage
    {
        private const uint QueryLimitedInformation = 0x1000; // PROCESS_QUERY_LIMITED_INFORMATION
        private const int InsufficientBuffer = 122; // ERROR_INSUFFICIENT_BUFFER
        private const int LongestPath = 32768; // characters, with the terminating null

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, char[] name, ref uint size);

        // Null when the process cannot be opened (gone, or protected from this user). The buffer grows once for a long path
        // and is kept for the rest of the scan.
        public static string? Path(int processId, ref char[] buffer)
        {
            using var handle = OpenProcess(QueryLimitedInformation, false, processId);
            if (handle.IsInvalid) return null;
            while (true)
            {
                uint size = (uint)buffer.Length;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size)) return new string(buffer, 0, (int)size);
                if (Marshal.GetLastWin32Error() != InsufficientBuffer || buffer.Length >= LongestPath) return null;
                buffer = new char[LongestPath];
            }
        }
    }
}
