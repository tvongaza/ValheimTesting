using System.Text;

namespace Valheim.Testing.Game;

/// <summary>
/// Files the game writes into the user's own Valheim data folder whatever the launch asks (#822), and the one owner of leaving
/// them as found: the macOS dedicated server's access lists, which it reads beside its world but outside <c>-savedir</c>, and
/// every game client's recent-servers list, which it writes under Unity's per-user data folder (a game client reads no
/// <c>-savedir</c>). Before an owned process starts, each file is copied into the run's backup folder, the backup journalled
/// (<see cref="Captured"/>) and the file moved out of the game's way (<see cref="Isolate"/>). Once that process is proven
/// stopped, or by <c>env recover</c> from the journal, the user's versions go back (<see cref="Restore"/>) and a version
/// written during the run is kept in the backup's <c>after</c> folder as evidence. On this machine it works in process; on a
/// host through that host's shell (<see cref="Remote"/>: bash on a Mac, PowerShell on Windows), with the same checks.
/// One capture of a folder's files at a time: the capture creates an owner marker for that folder and kind exclusively, in the
/// toolkit's own folder beside the journal (never in the user's), and a second run's capture refuses, changing nothing, until the
/// first one's restore (or recovery) removes it.
/// </summary>
internal sealed class UserSaveFiles
{
    internal enum Kind { MacServerLists, ClientServerList }

    /// <summary>The files of each kind, relative to the save root with '/'. Never read from a journal: the kind names them.</summary>
    internal static IReadOnlyList<string> Names(Kind kind) => kind switch
    {
        Kind.MacServerLists => ["adminlist.txt", "permittedlist.txt", "bannedlist.txt"],
        _ => ["serverlist_local/recent", "serverlist_local/recent.old"],
    };

    private static string KindName(Kind kind) => kind == Kind.MacServerLists ? "mac-server-lists" : "client-server-list";

    /// <summary>
    /// The run's backup folder for one owned process: <c>user-files-&lt;run&gt;-&lt;label&gt;</c>. Recovery restores only from a folder
    /// named for its own run.
    /// </summary>
    internal static string BackupName(string runId, string label)
    {
        if (!RunJournal.SafeName(runId) || !RunJournal.SafeName(label)) throw new ArgumentException("A run id and a label are letters, digits, '.', '_' and '-'.");
        return BackupPrefix + runId + "-" + label;
    }
    private const string BackupPrefix = "user-files-";

    /// <summary>
    /// The owner marker's file name for <paramref name="kind"/> in <paramref name="saveRoot"/>: one per folder and kind, so every run that
    /// would touch the same files meets the same name. A Windows path is compared without case, as Windows does.
    /// </summary>
    internal static string MarkerName(Kind kind, string saveRoot, bool windows)
    {
        string root = saveRoot.TrimEnd('/', '\\');
        if (windows) root = root.Replace('/', '\\').ToLowerInvariant();
        return KindName(kind) + "-" + FileHash.Sha256(Encoding.UTF8.GetBytes(root))[..16] + ".owner";
    }

    /// <summary>This machine's marker folder: <c>user-files</c> beside its journal, in the toolkit's data folder.</summary>
    internal static string LocalMarkers => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RunJournal.LocalDirectory).TrimEnd(Path.DirectorySeparatorChar))!, "user-files");

    /// <summary>Runs one of <see cref="BashScript"/> or <see cref="PowerShellScript"/> on a host and returns its standard output; throws when it fails.</summary>
    internal sealed record Remote(bool PowerShell, Func<string, IReadOnlyDictionary<string, string>, Task<string>> Run);

    internal Kind FileKind { get; }
    internal string SaveRoot { get; }
    internal string Backup { get; }
    internal string Marker { get; }
    private readonly string[] _hashes;
    private readonly Remote? _remote;
    private IReadOnlyList<string> FileNames => Names(FileKind);

    private UserSaveFiles(Kind kind, string saveRoot, string backup, string marker, string[] hashes, Remote? remote)
    {
        FileKind = kind; SaveRoot = saveRoot; Backup = backup; Marker = marker; _hashes = hashes; _remote = remote;
    }

    /// <summary>
    /// Creates the folder's owner marker in <paramref name="markers"/> (refused, with nothing changed, while another capture of the
    /// same files holds it), then copies each file of <paramref name="kind"/> under <paramref name="saveRoot"/> into
    /// <paramref name="backup"/> (a new folder) and records its SHA-256, or that it was absent. Refuses a link or a directory in a
    /// file's place, and a backup folder that exists. A capture that fails after its marker removes the marker.
    /// </summary>
    internal static async Task<UserSaveFiles> CaptureAsync(Kind kind, string saveRoot, string backup, string markers, Remote? remote = null)
    {
        if (remote == null) return Capture(kind, saveRoot, backup, markers);
        string marker = Join(markers, MarkerName(kind, saveRoot, remote.PowerShell), remote.PowerShell);
        string reply = await remote.Run(remote.PowerShell ? PowerShellScript : BashScript, Variables("capture", kind, saveRoot, backup, marker, null)).ConfigureAwait(false);
        string[] hashes = new string[Names(kind).Count];
        for (int i = 0; i < hashes.Length; i++)
        {
            string? value = Line(reply, "VT-USER-FILE " + Names(kind)[i] + " ")
                ?? throw new InvalidDataException($"The user file {Names(kind)[i]} was not reported by its host.");
            if (!ValidHash(value)) throw new InvalidDataException($"The user file {Names(kind)[i]} has no valid hash on its host.");
            hashes[i] = value;
        }
        return new(kind, saveRoot, backup, marker, hashes, remote);
    }

    internal static UserSaveFiles Capture(Kind kind, string saveRoot, string backup, string markers)
    {
        if (Directory.Exists(backup) || File.Exists(backup)) throw new IOException($"The user-file backup {backup} exists; refusing to overwrite it.");
        Directory.CreateDirectory(markers);
        string marker = Path.Combine(markers, MarkerName(kind, saveRoot, OperatingSystem.IsWindows()));
        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.UTF8.GetBytes(backup + "\n"));
        }
        catch (IOException) when (File.Exists(marker))
        {
            string owner = "";
            try { owner = File.ReadAllText(marker).Trim(); } catch (IOException) { }
            throw new IOException($"Another capture holds the user's {KindName(kind)} in {saveRoot} (its backup: {owner}); nothing was changed. " +
                $"Let that run finish, or run env recover for it, then retry. If env status lists no run with that backup, nothing was moved: " +
                $"delete the marker {marker} and retry.");
        }
        try
        {
            Directory.CreateDirectory(backup);
            var names = Names(kind);
            string[] hashes = new string[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                string source = Local(saveRoot, names[i]);
                RequireFileOrNothing(source, "the user's " + names[i]);
                if (!File.Exists(source)) { hashes[i] = "-"; continue; }
                string target = Local(backup, names[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: false);
                hashes[i] = FileHash.Sha256(source);
                if (FileHash.Sha256(target) != hashes[i]) throw new IOException($"The user's {names[i]} changed while it was copied.");
            }
            return new(kind, saveRoot, backup, marker, hashes, null);
        }
        catch
        {
            File.Delete(marker); // Nothing of the user's moved: the next capture may go ahead.
            throw;
        }
    }

    /// <summary>The journal line that names this backup and marker, written before any user file is moved.</summary>
    internal JournalEntry Captured() => JournalEntry.Of(JournalEntry.UserFilesCaptured,
        ("files", KindName(FileKind)), ("saveRoot", SaveRoot), ("backup", Backup), ("marker", Marker), ("hashes", string.Join(',', _hashes)));

    internal JournalEntry Restored() => JournalEntry.Of(JournalEntry.UserFilesRestored, ("backup", Backup));

    /// <summary>
    /// Rebuilds a journalled capture for recovery. Refuses an unknown kind, a save root other than <paramref name="expectedSaveRoot"/>
    /// (the user's own, resolved where the files are), a backup folder not named for <paramref name="runId"/>, a marker not named for
    /// that folder and kind, and a bad hash.
    /// </summary>
    internal static UserSaveFiles FromJournal(IReadOnlyDictionary<string, string> fields, string runId, string expectedSaveRoot, Remote? remote = null)
    {
        string files = fields.GetValueOrDefault("files") ?? "", root = fields.GetValueOrDefault("saveRoot") ?? "", backup = fields.GetValueOrDefault("backup") ?? "";
        string marker = fields.GetValueOrDefault("marker") ?? "";
        Kind kind = files == KindName(Kind.MacServerLists) ? Kind.MacServerLists : files == KindName(Kind.ClientServerList) ? Kind.ClientServerList
            : throw new InvalidDataException($"The journal names unknown user files '{files}'.");
        bool windows = remote?.PowerShell ?? OperatingSystem.IsWindows();
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Trimmed(string path) => path.TrimEnd('/', '\\');
        if (!Trimmed(root).Equals(Trimmed(expectedSaveRoot), comparison))
            throw new InvalidDataException("The journal does not name this user's Valheim data folder.");
        string[] parts = backup.Split('/', '\\');
        if (!parts[^1].StartsWith(BackupPrefix + runId + "-", StringComparison.Ordinal) || parts.Any(part => part is "." or "..") ||
            (remote == null && !Path.IsPathFullyQualified(backup)))
            throw new InvalidDataException("The journal does not name this run's user-file backup.");
        string[] markerParts = marker.Split('/', '\\');
        if (markerParts[^1] != MarkerName(kind, root, windows) || markerParts.Any(part => part is "." or "..") || (remote == null && !Path.IsPathFullyQualified(marker)))
            throw new InvalidDataException("The journal does not name this folder's user-file marker.");
        string[] hashes = (fields.GetValueOrDefault("hashes") ?? "").Split(',');
        if (hashes.Length != Names(kind).Count || !hashes.All(ValidHash)) throw new InvalidDataException("The journal has an invalid user-file hash.");
        return new(kind, root, backup, marker, hashes, remote);
    }

    internal static Kind? KindOf(IReadOnlyDictionary<string, string> fields) => fields.GetValueOrDefault("files") switch
    {
        "mac-server-lists" => Kind.MacServerLists,
        "client-server-list" => Kind.ClientServerList,
        _ => null,
    };

    /// <summary>
    /// Moves the captured files out of the game's way. Every file is checked first: one that changed since the capture refuses the
    /// whole step and changes nothing. A crash part way is recoverable: the capture was journalled and every byte is in the backup.
    /// </summary>
    internal async Task IsolateAsync()
    {
        if (_remote == null) { Isolate(); return; }
        string reply = await RunAsync("isolate").ConfigureAwait(false);
        if (Line(reply, "VT-USER-FILES-ISOLATED") == null) throw new InvalidDataException("The user files were not moved aside on their host.");
    }

    internal void Isolate()
    {
        var names = FileNames;
        for (int i = 0; i < names.Count; i++)
        {
            string path = Local(SaveRoot, names[i]);
            RequireFileOrNothing(path, "the user's " + names[i]);
            string found = File.Exists(path) ? FileHash.Sha256(path) : "-";
            if (found != _hashes[i]) throw new IOException($"The user's {names[i]} changed during preparation; nothing was started.");
        }
        for (int i = 0; i < names.Count; i++)
            if (_hashes[i] != "-") File.Delete(Local(SaveRoot, names[i]));
    }

    /// <summary>
    /// Undoes an isolation that refused or broke off before any process started: a captured file that is missing now goes back
    /// from the backup, and a file that is present is the user's (nothing of the run ran) and is never overwritten or deleted. Then
    /// the marker goes. Checks the backup of every missing file before it copies one.
    /// </summary>
    internal async Task UndoIsolateAsync()
    {
        if (_remote == null) { UndoIsolate(); return; }
        string reply = await RunAsync("undo").ConfigureAwait(false);
        if (Line(reply, "VT-USER-FILES-UNDONE") == null) throw new InvalidDataException("The user files were not put back on their host.");
    }

    internal void UndoIsolate()
    {
        var names = FileNames;
        for (int i = 0; i < names.Count; i++)
        {
            string original = Local(SaveRoot, names[i]), before = Local(Backup, names[i]);
            RequireFileOrNothing(original, "the user's " + names[i]);
            if (_hashes[i] != "-" && !File.Exists(original) && (!File.Exists(before) || FileHash.Sha256(before) != _hashes[i]))
                throw new IOException($"The backup of {names[i]} is missing or changed; it was not put back.");
        }
        for (int i = 0; i < names.Count; i++)
        {
            string original = Local(SaveRoot, names[i]);
            if (_hashes[i] == "-" || File.Exists(original)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(Local(Backup, names[i]), original, overwrite: false);
        }
        ReleaseMarker();
    }

    /// <summary>
    /// Puts the user's versions back; only after the owned process is proven stopped. The whole backup is checked before any file
    /// changes. A file the run wrote is kept in the backup's <c>after</c> folder first, and never overwritten there: a second run
    /// version (a retry after the file changed again) refuses rather than lose one. Then the marker goes. Repeating a finished
    /// restore changes nothing.
    /// </summary>
    internal async Task RestoreAsync()
    {
        if (_remote == null) { Restore(); return; }
        string reply = await RunAsync("restore").ConfigureAwait(false);
        if (Line(reply, "VT-USER-FILES-RESTORED") == null) throw new InvalidDataException("The user files were not restored on their host.");
    }

    internal void Restore()
    {
        var names = FileNames;
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i], original = Local(SaveRoot, name), before = Local(Backup, name), after = Local(Path.Combine(Backup, "after"), name);
            RequireFileOrNothing(original, "the user's " + name);
            RequireFileOrNothing(before, "the backup of " + name);
            RequireFileOrNothing(after, "the run's version of " + name);
            if (_hashes[i] != "-" && (!File.Exists(before) || FileHash.Sha256(before) != _hashes[i]))
                throw new IOException($"The backup of {name} is missing or changed; the user's files were not overwritten.");
            if (File.Exists(original) && (_hashes[i] == "-" || FileHash.Sha256(original) != _hashes[i]) &&
                File.Exists(after) && FileHash.Sha256(after) != FileHash.Sha256(original))
                throw new IOException($"The user's {name} changed again after an interrupted restore; the earlier run version is kept at {after}, and the current file was not overwritten.");
        }
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i], original = Local(SaveRoot, name), before = Local(Backup, name), expected = _hashes[i];
            if (File.Exists(original) && (expected == "-" || FileHash.Sha256(original) != expected))
            {
                // Copy first: an interrupted restore can retry safely. Never overwrite a kept run version.
                string after = Local(Path.Combine(Backup, "after"), name);
                Directory.CreateDirectory(Path.GetDirectoryName(after)!);
                if (!File.Exists(after)) File.Copy(original, after);
            }
            if (expected == "-")
            {
                if (File.Exists(original)) File.Delete(original);
            }
            else if (!File.Exists(original) || FileHash.Sha256(original) != expected)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(before, original, overwrite: true);
            }
        }
        ReleaseMarker();
    }

    // The marker goes only while it still names this backup: one another run made after a lost restore line stays its own.
    private void ReleaseMarker()
    {
        if (File.Exists(Marker) && File.ReadAllText(Marker).Trim() == Backup) File.Delete(Marker);
    }

    private Task<string> RunAsync(string mode) =>
        _remote!.Run(_remote.PowerShell ? PowerShellScript : BashScript, Variables(mode, FileKind, SaveRoot, Backup, Marker, _hashes));

    private static string Local(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Join(string folder, string name, bool windows) => folder.TrimEnd('/', '\\') + (windows ? "\\" : "/") + name;

    private static void RequireFileOrNothing(string path, string what)
    {
        if (new FileInfo(path).LinkTarget != null) throw new IOException($"{Capital(what)} ({path}) is a link; refusing to touch it.");
        if (Directory.Exists(path)) throw new IOException($"{Capital(what)} ({path}) is a directory, not a file.");
    }
    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static bool ValidHash(string value) => value == "-" || value.Length == 64 && value.All(Uri.IsHexDigit);

    private static Dictionary<string, string> Variables(string mode, Kind kind, string saveRoot, string backup, string marker, string[]? hashes) => new()
    {
        ["mode"] = mode, ["saveRoot"] = saveRoot, ["backup"] = backup, ["marker"] = marker,
        // One "<hash or -> <name>" line per file; capture reports the hashes, so it passes "-" for each.
        ["files"] = string.Join('\n', Names(kind).Select((name, i) => (hashes?[i] ?? "-") + " " + name)),
    };

    private static string? Line(string output, string prefix) =>
        output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..]).FirstOrDefault();

    // Variables: mode (capture, isolate, undo or restore), saveRoot, backup, marker, files ("<hash or -> <name>" lines, names
    // relative with '/'). Each mode checks every file before changing one, as the in-process steps do, then ends with its verdict.
    internal static readonly string BashScript = """
        set -eu
        sha() { shasum -a 256 "$1" | awk '{print $1}'; }
        plain() { [ ! -L "$1" ] && [ ! -d "$1" ]; }
        release() { if [ -f "$marker" ] && [ "$(cat -- "$marker")" = "$backup" ]; then rm -f -- "$marker"; fi; }
        case "$mode" in
          capture)
            [ ! -e "$backup" ] || { echo 'VT-USER-FILES-EXISTS'; exit 4; }
            mkdir -p -- "$(dirname -- "$marker")"
            if ! ( set -C; printf '%s\n' "$backup" > "$marker" ) 2> /dev/null; then
              printf 'VT-USER-FILES-HELD %s (marker %s; if env status lists no run with that backup, nothing was moved: delete the marker and retry)\n' "$(cat -- "$marker" 2> /dev/null || true)" "$marker"; exit 4
            fi
            trap 'rm -f -- "$marker"' EXIT
            mkdir -p -- "$backup"
            while read -r expected name; do
              [ -n "$name" ] || continue
              original="$saveRoot/$name"
              plain "$original" || exit 4
              if [ -f "$original" ]; then
                hash=$(sha "$original")
                mkdir -p -- "$(dirname -- "$backup/$name")"
                cp -- "$original" "$backup/$name"
                [ "$(sha "$backup/$name")" = "$hash" ] || exit 4
              else hash=-; fi
              printf 'VT-USER-FILE %s %s\n' "$name" "$hash"
            done <<< "$files"
            trap - EXIT
            ;;
          isolate)
            while read -r expected name; do
              [ -n "$name" ] || continue
              original="$saveRoot/$name"
              plain "$original" || exit 4
              if [ -f "$original" ]; then actual=$(sha "$original"); else actual=-; fi
              [ "$actual" = "$expected" ] || exit 4
            done <<< "$files"
            while read -r expected name; do
              [ -n "$name" ] || continue
              [ "$expected" = - ] || rm -- "$saveRoot/$name"
            done <<< "$files"
            echo VT-USER-FILES-ISOLATED
            ;;
          undo)
            while read -r expected name; do
              [ -n "$name" ] || continue
              original="$saveRoot/$name"; before="$backup/$name"
              plain "$original" || exit 4
              if [ "$expected" != - ] && [ ! -e "$original" ]; then [ -f "$before" ] && [ "$(sha "$before")" = "$expected" ] || exit 4; fi
            done <<< "$files"
            while read -r expected name; do
              [ -n "$name" ] || continue
              original="$saveRoot/$name"
              if [ "$expected" != - ] && [ ! -e "$original" ]; then mkdir -p -- "$(dirname -- "$original")"; cp -- "$backup/$name" "$original"; fi
            done <<< "$files"
            release
            echo VT-USER-FILES-UNDONE
            ;;
          restore)
            while read -r expected name; do
              [ -n "$name" ] || continue
              original="$saveRoot/$name"; before="$backup/$name"; after="$backup/after/$name"
              plain "$original" && plain "$before" && plain "$after" || exit 4
              if [ "$expected" != - ]; then
                [ -f "$before" ] && [ "$(sha "$before")" = "$expected" ] || exit 4
              fi
              if [ -f "$original" ]; then
                current=$(sha "$original")
                if [ "$current" != "$expected" ] && [ -f "$after" ]; then [ "$(sha "$after")" = "$current" ] || exit 4; fi
              fi
            done <<< "$files"
            while read -r expected name; do
              [ -n "$name" ] || continue
              original="$saveRoot/$name"; before="$backup/$name"; after="$backup/after/$name"
              if [ -f "$original" ]; then current=$(sha "$original"); else current=-; fi
              if [ "$current" != "$expected" ] && [ "$current" != - ]; then
                mkdir -p -- "$(dirname -- "$after")"
                [ -f "$after" ] || cp -- "$original" "$after"
              fi
              if [ "$expected" = - ]; then [ ! -f "$original" ] || rm -- "$original"
              elif [ "$current" != "$expected" ]; then mkdir -p -- "$(dirname -- "$original")"; cp -- "$before" "$original"; fi
            done <<< "$files"
            release
            echo VT-USER-FILES-RESTORED
            ;;
          *) exit 4 ;;
        esac
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellScript = """
        $ErrorActionPreference = 'Stop'
        function Get-VtSha([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
        function Test-VtPlain([string]$path) {
            if ([IO.Directory]::Exists($path)) { return $false }
            if (-not [IO.File]::Exists($path)) { return $true }
            return ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -eq 0
        }
        function Join-VtPath([string]$root, [string]$name) { [IO.Path]::Combine($root, $name.Replace('/', [IO.Path]::DirectorySeparatorChar)) }
        function Clear-VtMarker { if ([IO.File]::Exists($marker) -and ([IO.File]::ReadAllText($marker).Trim() -eq $backup)) { [IO.File]::Delete($marker) } }
        $entries = @($files -split "`n" | Where-Object { $_ } | ForEach-Object { $hash, $name = $_.TrimEnd("`r") -split ' ', 2; [pscustomobject]@{ Hash = $hash; Name = $name } })
        switch ($mode) {
            'capture' {
                if ([IO.Directory]::Exists($backup) -or [IO.File]::Exists($backup)) { 'VT-USER-FILES-EXISTS'; exit 4 }
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($marker))
                try {
                    $stream = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                    $bytes = [Text.Encoding]::UTF8.GetBytes($backup + "`n"); $stream.Write($bytes, 0, $bytes.Length); $stream.Dispose()
                } catch {
                    $owner = if ([IO.File]::Exists($marker)) { [IO.File]::ReadAllText($marker).Trim() } else { '' }
                    'VT-USER-FILES-HELD ' + $owner + ' (marker ' + $marker + '; if env status lists no run with that backup, nothing was moved: delete the marker and retry)'; exit 4
                }
                try {
                    [void][IO.Directory]::CreateDirectory($backup)
                    foreach ($entry in $entries) {
                        $original = Join-VtPath $saveRoot $entry.Name
                        if (-not (Test-VtPlain $original)) { throw 'not a plain file: ' + $original }
                        $hash = '-'
                        if ([IO.File]::Exists($original)) {
                            $hash = Get-VtSha $original
                            $copy = Join-VtPath $backup $entry.Name
                            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
                            [IO.File]::Copy($original, $copy, $false)
                            if ((Get-VtSha $copy) -ne $hash) { throw 'changed while copied: ' + $original }
                        }
                        'VT-USER-FILE ' + $entry.Name + ' ' + $hash
                    }
                } catch { [IO.File]::Delete($marker); exit 4 }
            }
            'isolate' {
                foreach ($entry in $entries) {
                    $original = Join-VtPath $saveRoot $entry.Name
                    if (-not (Test-VtPlain $original)) { exit 4 }
                    $actual = if ([IO.File]::Exists($original)) { Get-VtSha $original } else { '-' }
                    if ($actual -ne $entry.Hash) { exit 4 }
                }
                foreach ($entry in $entries) { if ($entry.Hash -ne '-') { [IO.File]::Delete((Join-VtPath $saveRoot $entry.Name)) } }
                'VT-USER-FILES-ISOLATED'
            }
            'undo' {
                foreach ($entry in $entries) {
                    $original = Join-VtPath $saveRoot $entry.Name; $before = Join-VtPath $backup $entry.Name
                    if (-not (Test-VtPlain $original)) { exit 4 }
                    if ($entry.Hash -ne '-' -and -not [IO.File]::Exists($original) -and (-not [IO.File]::Exists($before) -or (Get-VtSha $before) -ne $entry.Hash)) { exit 4 }
                }
                foreach ($entry in $entries) {
                    $original = Join-VtPath $saveRoot $entry.Name
                    if ($entry.Hash -ne '-' -and -not [IO.File]::Exists($original)) {
                        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($original))
                        [IO.File]::Copy((Join-VtPath $backup $entry.Name), $original, $false)
                    }
                }
                Clear-VtMarker
                'VT-USER-FILES-UNDONE'
            }
            'restore' {
                $afterRoot = Join-Path $backup 'after'
                foreach ($entry in $entries) {
                    $original = Join-VtPath $saveRoot $entry.Name; $before = Join-VtPath $backup $entry.Name; $after = Join-VtPath $afterRoot $entry.Name
                    if (-not ((Test-VtPlain $original) -and (Test-VtPlain $before) -and (Test-VtPlain $after))) { exit 4 }
                    if ($entry.Hash -ne '-' -and (-not [IO.File]::Exists($before) -or (Get-VtSha $before) -ne $entry.Hash)) { exit 4 }
                    if ([IO.File]::Exists($original)) {
                        $current = Get-VtSha $original
                        if ($current -ne $entry.Hash -and [IO.File]::Exists($after) -and (Get-VtSha $after) -ne $current) { exit 4 }
                    }
                }
                foreach ($entry in $entries) {
                    $original = Join-VtPath $saveRoot $entry.Name; $before = Join-VtPath $backup $entry.Name; $after = Join-VtPath $afterRoot $entry.Name
                    $current = if ([IO.File]::Exists($original)) { Get-VtSha $original } else { '-' }
                    if ($current -ne $entry.Hash -and $current -ne '-') {
                        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($after))
                        if (-not [IO.File]::Exists($after)) { [IO.File]::Copy($original, $after, $false) }
                    }
                    if ($entry.Hash -eq '-') { if ([IO.File]::Exists($original)) { [IO.File]::Delete($original) } }
                    elseif ($current -ne $entry.Hash) {
                        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($original))
                        [IO.File]::Copy($before, $original, $true)
                    }
                }
                Clear-VtMarker
                'VT-USER-FILES-RESTORED'
            }
            default { exit 4 }
        }
        """.ReplaceLineEndings("\n");
}

/// <summary>
/// One owned process's <see cref="UserSaveFiles"/>: <see cref="Protect"/> before it starts (capture, journal, move aside),
/// <see cref="Restore"/> once it is proven stopped, or proven never to have started (<see cref="NoProcess"/>). A capture left
/// unrestored stays journalled for <c>env recover</c>. The capture is journalled before anything moves (<paramref name="journal"/>);
/// the restore is noted after it happened (<paramref name="note"/>, the journal unless given: during teardown, when the run's
/// cancellation has fired).
/// </summary>
internal sealed class UserSaveFilesGuard(Func<Task<UserSaveFiles>> capture, Func<JournalEntry, Task> journal, Func<JournalEntry, Task>? note = null)
{
    private const string NoProcessKey = "Valheim.Testing.Game.UserSaveFilesGuard.NoProcess";
    private UserSaveFiles? _files;
    private bool _restored;

    /// <summary>
    /// Marks <paramref name="error"/> as proof that its launch left no process: it failed before one existed (the start was refused
    /// or never made one), or the process was proven stopped. Only such a failure lets the user's files go back at once.
    /// </summary>
    internal static T NoProcess<T>(T error) where T : Exception
    {
        error.Data[NoProcessKey] = true;
        return error;
    }

    internal static bool ProvesNoProcess(Exception error) => error.Data[NoProcessKey] is true;

    internal void Protect()
    {
        if (_files != null) throw new InvalidOperationException("These user files are already captured.");
        var files = capture().GetAwaiter().GetResult(); // A refused capture changed nothing and journalled nothing.
        journal(files.Captured()).GetAwaiter().GetResult();
        _files = files;
        try { files.IsolateAsync().GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            // Nothing started, so a present file is the user's: put back only what was moved, or leave the capture for env recover.
            try
            {
                files.UndoIsolateAsync().GetAwaiter().GetResult();
                _restored = true;
                (note ?? journal)(files.Restored()).GetAwaiter().GetResult();
            }
            catch (Exception undo) when (undo is not OperationCanceledException) { error.Data["user-files-restore"] = undo.Message; }
            throw;
        }
    }

    internal void Restore()
    {
        if (_files == null || _restored) return;
        _files.RestoreAsync().GetAwaiter().GetResult();
        _restored = true;
        (note ?? journal)(_files.Restored()).GetAwaiter().GetResult();
    }

    /// <summary>
    /// A guard for a game client on this machine: its recent-servers list in this user's data folder, backed up into
    /// <paramref name="evidence"/>, journalled in this process's run, its marker beside this machine's journal.
    /// </summary>
    internal static UserSaveFilesGuard ForLocalClient(string evidence)
    {
        var run = RunJournal.ThisProcess;
        string root = HostedWorld.DefaultSaveDirectory(GameLaunch.CurrentClientHost);
        string backup = Path.Combine(Path.GetFullPath(evidence), UserSaveFiles.BackupName(run.RunId, "client-" + Guid.NewGuid().ToString("N")[..8]));
        return new(() => Task.FromResult(UserSaveFiles.Capture(UserSaveFiles.Kind.ClientServerList, root, backup, UserSaveFiles.LocalMarkers)),
            entry => { run.AppendLocal("client", entry); return Task.CompletedTask; });
    }
}
