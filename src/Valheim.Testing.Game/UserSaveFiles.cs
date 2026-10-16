using System.Text;

namespace Valheim.Testing.Game;

/// <summary>
/// The single owner of capture, isolation, restore/undo and journal settlement for files Valheim writes into the user's
/// data folder: client recent-server files and Mac server access lists. A folder/kind has one versioned owner marker.
/// Immutable restore intent precedes mutation; atomic same-directory completion receipts authorize journal settlement only.
/// The in-process implementation and host scripts below use the same record and transitions. Callers supply transport.
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
    private string[] _hashes;
    private readonly Remote? _remote;
    private IReadOnlyList<string> FileNames => Names(FileKind);

    private UserSaveFiles(Kind kind, string saveRoot, string backup, string marker, string[] hashes, Remote? remote)
    {
        FileKind = kind; SaveRoot = saveRoot; Backup = backup; Marker = marker; _hashes = hashes; _remote = remote;
    }

    // The only live record is the folder/kind owner marker. Completion renames that same record into a
    // capture-specific receipt beside it. A receipt can settle a lost journal reply, never authorize more writes.
    private const string Version = "VT-USER-FILES-1";
    private string? _identity;
    private string Identity => _identity ?? FileHash.Sha256(Encoding.UTF8.GetBytes(KindName(FileKind) + "\n" + SaveRoot + "\n" + Backup));
    internal string Receipt => Marker + "." + Identity + ".completed";
    private bool _captureIntended;
    private sealed record Owner(string Phase, string[] Hashes, string[] Observed, string[] Final);

    internal static async Task<UserSaveFiles> CaptureAsync(Kind kind, string saveRoot, string backup, string markers, Remote? remote = null,
        Func<JournalEntry, Task>? journal = null)
    {
        string marker = Join(markers, MarkerName(kind, saveRoot, remote?.PowerShell ?? OperatingSystem.IsWindows()), remote?.PowerShell ?? OperatingSystem.IsWindows());
        var files = new UserSaveFiles(kind, saveRoot, backup, marker, [], remote);
        // No owner marker can become orphaned: even an uncertain capture reply has its identity journalled first.
        if (journal != null) await journal(files.Entry(JournalEntry.UserFilesCaptureIntended, ("captureState", "intended"))).ConfigureAwait(false);
        if (remote == null) files.CaptureLocal();
        else
        {
            string reply = await files.RunAsync("capture").ConfigureAwait(false);
            files._hashes = Names(kind).Select(name => Line(reply, "VT-USER-FILE " + name + " ")
                ?? throw new InvalidDataException($"The user file {name} was not reported by its host.")).ToArray();
            if (!files._hashes.All(ValidHash)) throw new InvalidDataException("The host reported an invalid user-file hash.");
        }
        if (journal != null) await journal(files.Captured()).ConfigureAwait(false);
        return files;
    }

    internal static UserSaveFiles Capture(Kind kind, string saveRoot, string backup, string markers) =>
        CaptureAsync(kind, saveRoot, backup, markers).GetAwaiter().GetResult();

    private void CaptureLocal()
    {
        RequireFileOrNothing(Marker, "the user-file owner marker");
        if (File.Exists(Marker)) throw Refusal("Another capture holds these files", Marker);
        RequireFileOrNothing(Receipt, "the completed user-file receipt");
        if (File.Exists(Receipt)) throw Refusal("This capture identity already completed; use a new run/label backup", Receipt);
        if (Directory.Exists(Backup) || File.Exists(Backup)) throw new IOException($"The user-file backup {Backup} exists; refusing to overwrite it.");
        Directory.CreateDirectory(Backup);
        _hashes = FileNames.Select(name =>
        {
            string source = Local(SaveRoot, name), target = Local(Backup, name);
            string hash = Hash(source);
            if (hash != "-")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: false);
                if (Hash(target) != hash) throw new IOException($"The user's {name} changed while it was copied.");
            }
            return hash;
        }).ToArray();
        WriteOwner(new("captured", _hashes, [], []), acquire: true);
        var current = FileNames.Select(name => Hash(Local(SaveRoot, name))).ToArray();
        if (!current.SequenceEqual(_hashes))
        {
            // A different owner may have completed while we copied. This capture moved nothing.
            WriteOwner(new("completed-undo", _hashes, current, current));
            File.Move(Marker, Receipt, overwrite: false);
            throw Refusal("The personal files changed before ownership was acquired; capture cancelled without mutations", Receipt);
        }
    }

    private JournalEntry Entry(string kind, params (string Key, string Value)[] extra) => JournalEntry.Of(kind,
        [("files", KindName(FileKind)), ("saveRoot", SaveRoot), ("backup", Backup), ("marker", Marker), .. extra]);

    /// <summary>Owns capture/isolation failure handling; callers supply journal transport only.</summary>
    internal async Task ProtectAsync(Func<JournalEntry, Task> note)
    {
        try { await IsolateAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            try { await FinishAsync(undo: true, note).ConfigureAwait(false); }
            catch (Exception undo) when (undo is not OperationCanceledException) { error.Data["user-files-restore"] = undo.Message; }
            throw;
        }
    }

    /// <summary>The journal line that names this backup and marker, written before any user file is moved.</summary>
    internal JournalEntry Captured() => SnapshotEntry(JournalEntry.UserFilesCaptured);

    // A duplicate intent can follow a durable completion whose acknowledgement was lost. It must be
    // self-contained even after the earlier completion removed the old item from the status projection.
    private JournalEntry SnapshotEntry(string kind) => Entry(kind,
        ("hashes", string.Join(',', _hashes)), ("captureState", _captureIntended ? "intended" : "captured"));

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
        bool intended = fields.GetValueOrDefault("captureState") == "intended";
        string[] hashes = intended ? [] : (fields.GetValueOrDefault("hashes") ?? "").Split(',');
        if (!intended && (hashes.Length != Names(kind).Count || !hashes.All(ValidHash))) throw new InvalidDataException("The journal has an invalid user-file hash.");
        return new(kind, root, backup, marker, hashes, remote) { _captureIntended = intended };
    }

    internal static Kind? KindOf(IReadOnlyDictionary<string, string> fields) => fields.GetValueOrDefault("files") switch
    {
        "mac-server-lists" => Kind.MacServerLists,
        "client-server-list" => Kind.ClientServerList,
        _ => null,
    };

    internal async Task IsolateAsync()
    {
        if (_remote == null) { Isolate(); return; }
        await RunCheckedAsync("isolate", "VT-USER-FILES-ISOLATED").ConfigureAwait(false);
    }

    internal void Isolate()
    {
        var owner = ReadOwner(Marker);
        if (owner.Phase != "captured") throw Refusal("Isolation was already started or completed", Marker);
        for (int i = 0; i < FileNames.Count; i++)
            if (Hash(Local(SaveRoot, FileNames[i])) != _hashes[i]) throw new IOException($"The user's {FileNames[i]} changed during preparation; nothing was started.");
        WriteOwner(owner with { Phase = "isolating" });
        for (int i = 0; i < FileNames.Count; i++)
            if (_hashes[i] != "-") File.Delete(Local(SaveRoot, FileNames[i]));
        WriteOwner(owner with { Phase = "isolated" });
    }

    internal Task UndoIsolateAsync() => FinishAsync(undo: true);
    internal void UndoIsolate() => UndoIsolateAsync().GetAwaiter().GetResult();
    internal Task RestoreAsync() => FinishAsync(undo: _captureIntended);
    internal Task RestoreAsync(Func<JournalEntry, Task> note) => FinishAsync(undo: _captureIntended, note);
    internal void Restore() => RestoreAsync().GetAwaiter().GetResult();

    private async Task FinishAsync(bool undo, Func<JournalEntry, Task>? note = null)
    {
        // Intent lives in the owner record before any mutation. Its pre-state never changes on retry.
        await PrepareRestoreAsync(undo).ConfigureAwait(false);
        if (note != null) await note(SnapshotEntry(JournalEntry.UserFilesRestoreIntended)).ConfigureAwait(false);
        if (_remote == null) CompleteLocal();
        else await RunCheckedAsync(undo ? "undo" : "restore", undo ? "VT-USER-FILES-UNDONE" : "VT-USER-FILES-RESTORED").ConfigureAwait(false);
        if (note != null) await note(Restored()).ConfigureAwait(false);
    }

    internal async Task PrepareRestoreAsync(bool undo = false)
    {
        if (_remote == null) { PrepareLocal(undo); return; }
        await RunCheckedAsync(undo ? "plan-undo" : "plan-restore", "VT-USER-FILES-PLANNED").ConfigureAwait(false);
    }

    private bool Settled()
    {
        RequireFileOrNothing(Receipt, "the completed user-file receipt");
        if (!File.Exists(Receipt)) return false;
        var done = ReadOwner(Receipt);
        if (done.Phase is not ("completed-restore" or "completed-undo")) throw Refusal("Invalid completion receipt", Receipt);
        return true; // Never inspect or mutate personal files, or another capture's current marker.
    }

    private Owner? Active()
    {
        if (Settled()) return null;
        RequireFileOrNothing(Marker, "the user-file owner marker");
        // Capture intent alone cannot have moved personal files. A different identity belongs to another capture.
        if (_captureIntended && (!File.Exists(Marker) || File.ReadLines(Marker).Skip(1).FirstOrDefault() != Identity)) return null;
        return ReadOwner(Marker);
    }

    private void PrepareLocal(bool undo)
    {
        var owner = Active();
        if (owner == null || owner.Phase.StartsWith("completed-", StringComparison.Ordinal)) return;
        if (owner.Phase is "restore" or "undo") return; // Preserve the first plan, including its operation.
        if (owner.Phase is not ("captured" or "isolating" or "isolated")) throw Refusal("Unknown user-file owner phase", Marker);
        undo = undo || owner.Phase is "captured" or "isolating";
        string[] observed = FileNames.Select(name => Hash(Local(SaveRoot, name))).ToArray();
        string[] final = observed.Select((hash, i) => undo && hash != "-" ? hash : _hashes[i]).ToArray();
        ValidateFiles(new(undo ? "undo" : "restore", _hashes, observed, final));
        WriteOwner(new(undo ? "undo" : "restore", _hashes, observed, final));
    }

    private void CompleteLocal()
    {
        var owner = Active();
        if (owner == null) return;
        if (!owner.Phase.StartsWith("completed-", StringComparison.Ordinal))
        {
            if (owner.Phase is not ("restore" or "undo")) throw Refusal("Restore intent is missing", Marker);
            ValidateFiles(owner); // Check every file before changing any.
            for (int i = 0; i < FileNames.Count; i++)
            {
                string name = FileNames[i], original = Local(SaveRoot, name), current = Hash(original), final = owner.Final[i];
                if (current == final) continue;
                if (current != "-")
                {
                    string after = Local(Path.Combine(Backup, "after"), name);
                    Directory.CreateDirectory(Path.GetDirectoryName(after)!);
                    if (!File.Exists(after)) File.Copy(original, after, overwrite: false);
                }
                if (final == "-") File.Delete(original);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Copy(Local(Backup, name), original, overwrite: true);
                }
            }
            owner = owner with { Phase = "completed-" + owner.Phase };
            WriteOwner(owner);
        }
        // Same directory, atomic and no-clobber. A failed publication leaves the completed owner in place.
        File.Move(Marker, Receipt, overwrite: false);
    }

    private void ValidateFiles(Owner owner)
    {
        for (int i = 0; i < FileNames.Count; i++)
        {
            string name = FileNames[i], current = Hash(Local(SaveRoot, name));
            if (current != owner.Observed[i] && current != owner.Final[i])
                throw Refusal($"The user's {name} changed after restore was planned; current bytes were preserved", Marker);
            string before = Hash(Local(Backup, name)), after = Hash(Local(Path.Combine(Backup, "after"), name));
            if (_hashes[i] != "-" && before != _hashes[i]) throw Refusal($"The backup of {name} is missing or changed", Marker);
            if (after != "-" && after != owner.Observed[i]) throw Refusal($"The kept run version of {name} changed", Marker);
        }
    }

    private Owner ReadOwner(string path)
    {
        RequireFileOrNothing(path, "the user-file ownership record");
        if (!File.Exists(path)) throw Refusal("User-file ownership cannot be proved (marker missing)", path);
        string[] lines = File.ReadAllText(path).Split('\n');
        if (lines.Length != 7 || lines[0] != Version || lines[1] != Identity)
            throw Refusal("Unrecognized, legacy, or changed user-file owner record; do not delete it or replay the backup", path);
        string[] hashes = lines[3].Split(','), observed = lines[4].Length == 0 ? [] : lines[4].Split(','), final = lines[5].Length == 0 ? [] : lines[5].Split(',');
        if (lines[2] is not ("captured" or "isolating" or "isolated" or "restore" or "undo" or "completed-restore" or "completed-undo") || hashes.Length != FileNames.Count || !hashes.All(ValidHash) ||
            (_hashes.Length != 0 && !hashes.SequenceEqual(_hashes)) ||
            (lines[2] is not ("captured" or "isolating" or "isolated") && (observed.Length != FileNames.Count || final.Length != FileNames.Count || !observed.All(ValidHash) || !final.All(ValidHash))))
            throw Refusal("Invalid or changed user-file owner state", path);
        _hashes = hashes;
        return new(lines[2], hashes, observed, final);
    }

    private void WriteOwner(Owner owner, bool acquire = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        string temporary = Marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(string.Join('\n', Version, Identity, owner.Phase, string.Join(',', owner.Hashes), string.Join(',', owner.Observed), string.Join(',', owner.Final), "")));
                stream.Flush(flushToDisk: true);
            }
            if (!acquire) ReadOwner(Marker);
            File.Move(temporary, Marker, overwrite: !acquire);
        }
        finally { File.Delete(temporary); }
    }

    private IOException Refusal(string reason, string path) => new($"{reason}. Backup: {Backup}; marker: {path}. " +
        "Keep these files and run env status / env recover for this run. If ownership cannot be proved, inspect this folder/kind and restore manually; other folders and kinds are unaffected.");

    private static string Hash(string path)
    {
        RequireFileOrNothing(path, "the user file");
        return File.Exists(path) ? FileHash.Sha256(path) : "-";
    }

    private async Task RunCheckedAsync(string mode, string verdict)
    {
        string reply = await RunAsync(mode).ConfigureAwait(false);
        if (Line(reply, verdict) == null) throw Refusal("The host did not confirm the user-file transition", Marker);
    }

    private Task<string> RunAsync(string mode) => _remote!.Run(_remote.PowerShell ? PowerShellScript : BashScript,
        Variables(mode, FileKind, SaveRoot, Backup, Marker, _hashes, Identity, _captureIntended));

    // Fake hosts map transport paths and execute this same owner, rather than reimplementing its policy.
    internal static string RunLocalStep(IReadOnlyDictionary<string, string> variables, Func<string, string> map)
    {
        var kind = variables["kind"] == "mac-server-lists" ? Kind.MacServerLists : Kind.ClientServerList;
        var files = new UserSaveFiles(kind, map(variables["saveRoot"]), map(variables["backup"]), map(variables["marker"]),
            variables["hashes"].Length == 0 ? [] : variables["hashes"].Split(','), null)
            { _identity = variables["identity"], _captureIntended = variables["intended"] == "true" };
        switch (variables["mode"])
        {
            case "capture":
                files.CaptureLocal();
                return string.Concat(Names(kind).Select((name, i) => "VT-USER-FILE " + name + " " + files._hashes[i] + "\n"));
            case "isolate": files.Isolate(); return "VT-USER-FILES-ISOLATED\n";
            case "plan-undo": files.PrepareLocal(true); return "VT-USER-FILES-PLANNED\n";
            case "plan-restore": files.PrepareLocal(false); return "VT-USER-FILES-PLANNED\n";
            case "undo": files.CompleteLocal(); return "VT-USER-FILES-UNDONE\n";
            case "restore": files.CompleteLocal(); return "VT-USER-FILES-RESTORED\n";
            default: throw new InvalidDataException("Unknown user-file step.");
        }
    }

    private static string Local(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Join(string folder, string name, bool windows) => folder.TrimEnd('/', '\\') + (windows ? "\\" : "/") + name;

    private static void RequireFileOrNothing(string path, string what)
    {
        if (new FileInfo(path).LinkTarget != null) throw new IOException($"{Capital(what)} ({path}) is a link; refusing to touch it.");
        if (Directory.Exists(path)) throw new IOException($"{Capital(what)} ({path}) is a directory, not a file.");
    }
    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static bool ValidHash(string value) => value == "-" || value.Length == 64 && value.All(Uri.IsHexDigit);

    private static Dictionary<string, string> Variables(string mode, Kind kind, string saveRoot, string backup, string marker, string[]? hashes, string identity, bool intended) => new()
    {
        ["mode"] = mode, ["saveRoot"] = saveRoot, ["backup"] = backup, ["marker"] = marker, ["identity"] = identity,
        ["kind"] = KindName(kind), ["intended"] = intended ? "true" : "false", ["hashes"] = string.Join(',', hashes ?? []),
        // One "<hash or -> <name>" line per file; capture reports the hashes, so it passes "-" for each.
        ["files"] = string.Join('\n', Names(kind).Select((name, i) => (hashes?.Length > i ? hashes[i] : "-") + " " + name)),
    };

    private static string? Line(string output, string prefix) =>
        output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..]).FirstOrDefault();

    // The host implementations of this owner: the same fixed file names, record and transition checks.
    // Variables are transport only; restore plans are persisted in the owner record, never recomputed on retry.
    internal static readonly string BashScript = """
        set -eu
        sha() { shasum -a 256 "$1" | awk '{print $1}'; }
        plain() { [ ! -L "$1" ] && [ ! -d "$1" ]; }
        refuse() { printf 'VT-USER-FILES-REFUSED %s; backup %s; marker %s. Keep these files; use env status / env recover for this run, or inspect and restore this folder/kind manually.\n' "$1" "$backup" "$marker" >&2; exit 4; }
        hash() { plain "$1" || refuse 'linked or non-file path'; if [ -f "$1" ]; then sha "$1"; else printf '%s\n' -; fi; }
        receipt="$marker.$identity.completed"
        names=(); expected=(); observed=(); final=()
        while read -r value name; do [ -n "$name" ] || continue; names+=("$name"); expected+=("$value"); done <<< "$files"
        count=${#names[@]}
        valid() { [ "$1" = - ] || [[ "$1" =~ ^[0-9a-fA-F]{64}$ ]]; }
        read_owner() {
          plain "$1" && [ -f "$1" ] || refuse 'ownership missing or linked'
          [ "$(sed -n '1p' "$1")" = VT-USER-FILES-1 ] && [ "$(sed -n '2p' "$1")" = "$identity" ] || refuse 'legacy, changed or other capture owner; do not delete the marker or replay the backup'
          [ "$(wc -l < "$1" | tr -d ' ')" = 6 ] || refuse 'invalid owner record'
          phase=$(sed -n '3p' "$1")
          owner_hashes=$(sed -n '4p' "$1")
          [ -z "$hashes" ] || [ "$owner_hashes" = "$hashes" ] || refuse 'changed capture hashes'
          IFS=, read -r -a expected <<< "$owner_hashes"
          IFS=, read -r -a observed <<< "$(sed -n '5p' "$1")"
          IFS=, read -r -a final <<< "$(sed -n '6p' "$1")"
          [ "${#expected[@]}" = "$count" ] || refuse 'invalid capture hashes'
          for value in "${expected[@]}"; do valid "$value" || refuse 'invalid capture hash'; done
          case "$phase" in
            captured|isolating|isolated) ;;
            restore|undo|completed-restore|completed-undo)
              [ "${#observed[@]}" = "$count" ] && [ "${#final[@]}" = "$count" ] || refuse 'invalid restore plan'
              for value in "${observed[@]}" "${final[@]}"; do valid "$value" || refuse 'invalid restore hash'; done ;;
            *) refuse 'unknown owner phase' ;;
          esac
        }
        write_owner() {
          mkdir -p -- "$(dirname -- "$marker")"
          temporary=$(mktemp "$marker.XXXXXXXX")
          ( IFS=,; printf '%s\n' VT-USER-FILES-1 "$identity" "$phase" "${expected[*]}" "${observed[*]-}" "${final[*]-}" ) > "$temporary"
          # Flush before publication: the intent must survive interruption before user-file writes.
          sync
          if [ "$1" = acquire ]; then
            if ! ln -- "$temporary" "$marker" 2>/dev/null; then rm -- "$temporary"; refuse 'another capture holds these files'; fi
            rm -- "$temporary"
          else
            read_owner "$marker" # Prove it is still this capture before replacing the record.
            mv -f -- "$temporary" "$marker"
          fi
          sync
          read_owner "$marker"
        }
        active() {
          plain "$receipt" || refuse 'linked completion receipt'
          if [ -f "$receipt" ]; then
            read_owner "$receipt"
            case "$phase" in completed-restore|completed-undo) return 1 ;; *) refuse 'invalid completion receipt' ;; esac
          fi
          plain "$marker" || refuse 'linked owner marker'
          if [ "$intended" = true ] && { [ ! -e "$marker" ] || [ "$(sed -n '2p' "$marker")" != "$identity" ]; }; then return 1; fi
          read_owner "$marker"
        }
        validate_files() {
          i=0
          while [ "$i" -lt "$count" ]; do
            name=${names[$i]}; current=$(hash "$saveRoot/$name")
            [ "$current" = "${observed[$i]}" ] || [ "$current" = "${final[$i]}" ] || refuse 'personal file changed after restore intent; current bytes preserved'
            before=$(hash "$backup/$name"); after=$(hash "$backup/after/$name")
            [ "${expected[$i]}" = - ] || [ "$before" = "${expected[$i]}" ] || refuse 'backup missing or changed'
            [ "$after" = - ] || [ "$after" = "${observed[$i]}" ] || refuse 'kept run version changed'
            i=$((i+1))
          done
        }
        case "$mode" in
          capture)
            plain "$marker" || refuse 'linked owner marker'
            [ ! -e "$marker" ] || refuse 'another capture holds these files'
            plain "$receipt" && [ ! -e "$receipt" ] || refuse 'capture identity already completed; use a new run/label backup'
            [ ! -e "$backup" ] && [ ! -L "$backup" ] || refuse 'backup already exists'
            mkdir -p -- "$backup"
            i=0
            while [ "$i" -lt "$count" ]; do
              name=${names[$i]}; original="$saveRoot/$name"; value=$(hash "$original")
              if [ "$value" != - ]; then
                mkdir -p -- "$(dirname -- "$backup/$name")"; cp -- "$original" "$backup/$name"
                [ "$(hash "$backup/$name")" = "$value" ] || refuse 'file changed during capture'
              fi
              expected[$i]=$value
              printf 'VT-USER-FILE %s %s\n' "$name" "$value"
              i=$((i+1))
            done
            phase=captured; write_owner acquire
            changed=false; i=0
            while [ "$i" -lt "$count" ]; do
              observed[$i]=$(hash "$saveRoot/${names[$i]}"); final[$i]=${observed[$i]}
              [ "${observed[$i]}" = "${expected[$i]}" ] || changed=true
              i=$((i+1))
            done
            if [ "$changed" = true ]; then phase=completed-undo; write_owner replace; mv -n -- "$marker" "$receipt"; refuse 'personal files changed before ownership; capture cancelled without mutations'; fi ;;
          isolate)
            read_owner "$marker"
            [ "$phase" = captured ] || refuse 'isolation cannot replay this phase'
            i=0
            while [ "$i" -lt "$count" ]; do [ "$(hash "$saveRoot/${names[$i]}")" = "${expected[$i]}" ] || refuse 'file changed during preparation'; i=$((i+1)); done
            phase=isolating; write_owner replace
            i=0
            while [ "$i" -lt "$count" ]; do [ "${expected[$i]}" = - ] || rm -- "$saveRoot/${names[$i]}"; i=$((i+1)); done
            phase=isolated; write_owner replace
            echo VT-USER-FILES-ISOLATED ;;
          plan-restore|plan-undo)
            if active; then
              if [ "$phase" = captured ] || [ "$phase" = isolating ] || [ "$phase" = isolated ]; then
                undo=false; if [ "$mode" = plan-undo ] || [ "$phase" != isolated ]; then undo=true; fi
                i=0
                while [ "$i" -lt "$count" ]; do
                  observed[$i]=$(hash "$saveRoot/${names[$i]}"); final[$i]=${expected[$i]}
                  if [ "$undo" = true ] && [ "${observed[$i]}" != - ]; then final[$i]=${observed[$i]}; fi
                  i=$((i+1))
                done
                if [ "$undo" = true ]; then phase=undo; else phase=restore; fi
                validate_files; write_owner replace
              fi
            fi
            echo VT-USER-FILES-PLANNED ;;
          restore|undo)
            if active; then
              case "$phase" in
                restore|undo)
                  validate_files
                  i=0
                  while [ "$i" -lt "$count" ]; do
                    name=${names[$i]}; original="$saveRoot/$name"; current=$(hash "$original"); target=${final[$i]}
                    if [ "$current" != "$target" ]; then
                      if [ "$current" != - ]; then
                        after="$backup/after/$name"; mkdir -p -- "$(dirname -- "$after")"
                        [ -f "$after" ] || cp -- "$original" "$after"
                      fi
                      if [ "$target" = - ]; then rm -- "$original"
                      else mkdir -p -- "$(dirname -- "$original")"; cp -- "$backup/$name" "$original"; fi
                    fi
                    i=$((i+1))
                  done
                  phase="completed-$phase"; write_owner replace ;;
                completed-restore|completed-undo) ;;
                *) refuse 'restore intent missing' ;;
              esac
              # No-clobber rename on the same filesystem; a collision keeps the live owner for inspection.
              mv -n -- "$marker" "$receipt"
              [ ! -e "$marker" ] || refuse 'completion receipt collision'
              sync
            fi
            if [ "$mode" = undo ]; then echo VT-USER-FILES-UNDONE; else echo VT-USER-FILES-RESTORED; fi ;;
          *) refuse 'unknown operation' ;;
        esac
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellScript = """
        $ErrorActionPreference = 'Stop'
        function Stop-Vt([string]$reason) { throw ($reason + '; backup ' + $backup + '; marker ' + $marker + '. Keep these files; use env status / env recover for this run, or inspect and restore this folder/kind manually.') }
        function Test-VtPlain([string]$path) {
            $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            if ($null -eq $item) { return $true }
            return -not $item.PSIsContainer -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)
        }
        function Get-VtSha([string]$path) {
            if (-not (Test-VtPlain $path)) { Stop-Vt 'linked or non-file path' }
            if ([IO.File]::Exists($path)) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
            return '-'
        }
        function Join-VtPath([string]$root, [string]$name) { [IO.Path]::Combine($root, $name.Replace('/', [IO.Path]::DirectorySeparatorChar)) }
        $receipt = $marker + '.' + $identity + '.completed'
        $entries = @($files -split "`n" | Where-Object { $_ } | ForEach-Object { $value, $name = $_.TrimEnd("`r") -split ' ', 2; $name })
        function Read-VtOwner([string]$path) {
            if (-not (Test-VtPlain $path) -or -not [IO.File]::Exists($path)) { Stop-Vt 'ownership missing or linked' }
            $lines = [IO.File]::ReadAllLines($path)
            if ($lines.Count -ne 6 -or $lines[0] -cne 'VT-USER-FILES-1' -or $lines[1] -cne $identity) { Stop-Vt 'legacy, changed or other capture owner; do not delete the marker or replay the backup' }
            if ($hashes -and $lines[3] -cne $hashes) { Stop-Vt 'changed capture hashes' }
            $expected = @($lines[3] -split ','); $observed = @(); $final = @()
            if ($expected.Count -ne $entries.Count -or @($expected | Where-Object { $_ -cnotmatch '^(-|[0-9a-fA-F]{64})$' }).Count) { Stop-Vt 'invalid capture hashes' }
            if ($lines[2] -cnotmatch '^(captured|isolating|isolated)$') {
                if ($lines[2] -cnotmatch '^(restore|undo|completed-restore|completed-undo)$') { Stop-Vt 'unknown owner phase' }
                $observed = @($lines[4] -split ','); $final = @($lines[5] -split ',')
                if ($observed.Count -ne $entries.Count -or $final.Count -ne $entries.Count -or @(@($observed + $final) | Where-Object { $_ -cnotmatch '^(-|[0-9a-fA-F]{64})$' }).Count) { Stop-Vt 'invalid restore plan' }
            }
            return [pscustomobject]@{ Phase = $lines[2]; Hashes = $expected; Observed = $observed; Final = $final }
        }
        function Write-VtOwner($owner, [bool]$acquire) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($marker))
            $temporary = $marker + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
            try {
                $content = @('VT-USER-FILES-1', $identity, $owner.Phase, ($owner.Hashes -join ','), ($owner.Observed -join ','), ($owner.Final -join ','), '') -join "`n"
                $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $bytes = [Text.Encoding]::UTF8.GetBytes($content); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
                if ($acquire) { [IO.File]::Move($temporary, $marker) }
                else { $null = Read-VtOwner $marker; [IO.File]::Move($temporary, $marker, $true) }
            } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        }
        function Get-VtActive {
            if (-not (Test-VtPlain $receipt)) { Stop-Vt 'linked completion receipt' }
            if ([IO.File]::Exists($receipt)) {
                $done = Read-VtOwner $receipt
                if ($done.Phase -cnotmatch '^completed-(restore|undo)$') { Stop-Vt 'invalid completion receipt' }
                return $null
            }
            if (-not (Test-VtPlain $marker)) { Stop-Vt 'linked owner marker' }
            if ($intended -eq 'true' -and (-not [IO.File]::Exists($marker) -or [IO.File]::ReadAllLines($marker)[1] -cne $identity)) { return $null }
            return Read-VtOwner $marker
        }
        function Test-VtFiles($owner) {
            for ($i = 0; $i -lt $entries.Count; $i++) {
                $name = $entries[$i]; $current = Get-VtSha (Join-VtPath $saveRoot $name)
                if ($current -cne $owner.Observed[$i] -and $current -cne $owner.Final[$i]) { Stop-Vt 'personal file changed after restore intent; current bytes preserved' }
                $before = Get-VtSha (Join-VtPath $backup $name); $after = Get-VtSha (Join-VtPath (Join-VtPath $backup 'after') $name)
                if ($owner.Hashes[$i] -cne '-' -and $before -cne $owner.Hashes[$i]) { Stop-Vt 'backup missing or changed' }
                if ($after -cne '-' -and $after -cne $owner.Observed[$i]) { Stop-Vt 'kept run version changed' }
            }
        }
        switch ($mode) {
            'capture' {
                if (-not (Test-VtPlain $marker) -or [IO.File]::Exists($marker)) { Stop-Vt 'another capture or linked marker holds these files' }
                if (-not (Test-VtPlain $receipt) -or [IO.File]::Exists($receipt)) { Stop-Vt 'capture identity already completed; use a new run/label backup' }
                if (-not (Test-VtPlain $backup) -or [IO.File]::Exists($backup) -or [IO.Directory]::Exists($backup)) { Stop-Vt 'backup already exists' }
                [void][IO.Directory]::CreateDirectory($backup)
                $expected = @()
                foreach ($name in $entries) {
                    $original = Join-VtPath $saveRoot $name; $value = Get-VtSha $original
                    if ($value -cne '-') {
                        $copy = Join-VtPath $backup $name; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
                        [IO.File]::Copy($original, $copy, $false)
                        if ((Get-VtSha $copy) -cne $value) { Stop-Vt 'file changed during capture' }
                    }
                    $expected += $value; 'VT-USER-FILE ' + $name + ' ' + $value
                }
                $owner = [pscustomobject]@{ Phase = 'captured'; Hashes = $expected; Observed = @(); Final = @() }
                Write-VtOwner $owner $true
                $current = @($entries | ForEach-Object { Get-VtSha (Join-VtPath $saveRoot $_) })
                if (($current -join ',') -cne ($expected -join ',')) {
                    $owner.Phase = 'completed-undo'; $owner.Observed = $current; $owner.Final = $current; Write-VtOwner $owner $false
                    [IO.File]::Move($marker, $receipt); Stop-Vt 'personal files changed before ownership; capture cancelled without mutations'
                }
            }
            'isolate' {
                $owner = Read-VtOwner $marker
                if ($owner.Phase -cne 'captured') { Stop-Vt 'isolation cannot replay this phase' }
                for ($i = 0; $i -lt $entries.Count; $i++) { if ((Get-VtSha (Join-VtPath $saveRoot $entries[$i])) -cne $owner.Hashes[$i]) { Stop-Vt 'file changed during preparation' } }
                $owner.Phase = 'isolating'; Write-VtOwner $owner $false
                for ($i = 0; $i -lt $entries.Count; $i++) { if ($owner.Hashes[$i] -cne '-') { [IO.File]::Delete((Join-VtPath $saveRoot $entries[$i])) } }
                $owner.Phase = 'isolated'; Write-VtOwner $owner $false
                'VT-USER-FILES-ISOLATED'
            }
            { $_ -eq 'plan-restore' -or $_ -eq 'plan-undo' } {
                $owner = Get-VtActive
                if ($null -ne $owner -and $owner.Phase -cmatch '^(captured|isolating|isolated)$') {
                    $undo = $mode -eq 'plan-undo' -or $owner.Phase -cne 'isolated'
                    $observed = @(); $final = @()
                    for ($i = 0; $i -lt $entries.Count; $i++) {
                        $value = Get-VtSha (Join-VtPath $saveRoot $entries[$i]); $observed += $value
                        if ($undo -and $value -cne '-') { $final += $value } else { $final += $owner.Hashes[$i] }
                    }
                    $owner.Observed = $observed; $owner.Final = $final; $owner.Phase = if ($undo) { 'undo' } else { 'restore' }
                    Test-VtFiles $owner; Write-VtOwner $owner $false
                }
                'VT-USER-FILES-PLANNED'
            }
            { $_ -eq 'restore' -or $_ -eq 'undo' } {
                $owner = Get-VtActive
                if ($null -ne $owner) {
                    if ($owner.Phase -ceq 'restore' -or $owner.Phase -ceq 'undo') {
                        Test-VtFiles $owner
                        for ($i = 0; $i -lt $entries.Count; $i++) {
                            $name = $entries[$i]; $original = Join-VtPath $saveRoot $name; $current = Get-VtSha $original; $target = $owner.Final[$i]
                            if ($current -ceq $target) { continue }
                            if ($current -cne '-') {
                                $after = Join-VtPath (Join-VtPath $backup 'after') $name; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($after))
                                if (-not [IO.File]::Exists($after)) { [IO.File]::Copy($original, $after, $false) }
                            }
                            if ($target -ceq '-') { [IO.File]::Delete($original) }
                            else { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($original)); [IO.File]::Copy((Join-VtPath $backup $name), $original, $true) }
                        }
                        $owner.Phase = 'completed-' + $owner.Phase; Write-VtOwner $owner $false
                    } elseif ($owner.Phase -cnotmatch '^completed-(restore|undo)$') { Stop-Vt 'restore intent missing' }
                    # Atomic no-clobber rename, within the markers directory.
                    [IO.File]::Move($marker, $receipt)
                }
                if ($mode -eq 'undo') { 'VT-USER-FILES-UNDONE' } else { 'VT-USER-FILES-RESTORED' }
            }
            default { Stop-Vt 'unknown operation' }
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
internal sealed class UserSaveFilesGuard(Func<Func<JournalEntry, Task>, Task<UserSaveFiles>> capture, Func<JournalEntry, Task> journal, Func<JournalEntry, Task>? note = null)
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
        _files = capture(journal).GetAwaiter().GetResult();
        _files.ProtectAsync(note ?? journal).GetAwaiter().GetResult();
    }

    internal void Restore()
    {
        if (_files == null || _restored) return;
        _files.RestoreAsync(note ?? journal).GetAwaiter().GetResult();
        _restored = true;
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
        return new(journal => UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.ClientServerList, root, backup, UserSaveFiles.LocalMarkers, journal: journal),
            entry => { run.AppendLocal("client", entry); return Task.CompletedTask; });
    }
}
