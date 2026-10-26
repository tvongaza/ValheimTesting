using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>What a run did to one actor's source install, as <see cref="ProfileSourceCheck.SettleAsync"/> found and left it.</summary>
internal enum SourceVerdictKind
{
    /// <summary>The install lists as it did before the launch.</summary>
    Unchanged,
    /// <summary>The game, run in place, added files or folders there; they were moved out (files into the run's evidence) and the install lists as before. Passes with a warning.</summary>
    Restored,
    /// <summary>Files or folders appeared in the install of a linked profile, which the game cannot write: something else added them. Named, left in place; passes with a warning.</summary>
    AddedElsewhere,
    /// <summary>A file changed or went missing, an added one could not be moved, or the settlement did not finish: the source is not repaired. Fails the cleanup step.</summary>
    Failed,
    /// <summary>Not compared: a process launched from the profile was not proven stopped, or a recovery found the baseline gone. Fails the cleanup step.</summary>
    Unknown,
    /// <summary>The actor runs its install in place, so there is no source apart from what it runs.</summary>
    NotApplicable,
}

/// <summary>A file the game added to the source install, moved out: <see cref="KeptAs"/> is where it is now (relative to the actor's evidence folder, or a host path).</summary>
[ResultShape]
internal sealed record SourceAddedFile(string Path, long Bytes, string Sha256, string KeptAs);

/// <summary>A source file whose size and modified time (or content) differ after the run.</summary>
[ResultShape]
internal sealed record SourceChangedFile(string Path, string Before, string After);

/// <summary>A path the settlement left in the install (a folder ends in <c>/</c>), and why.</summary>
[ResultShape]
internal sealed record SourceNotMoved(string Path, string Reason);

/// <summary>
/// The one source-install verdict of an actor's run (#612), written to <c>source-changes/&lt;actor&gt;/source-changes.json</c> in
/// the run's evidence (in a recovery, beside that profile's adopted files in its journalled attempt folder on the host). <see cref="Missing"/> names
/// files, <see cref="MissingDirectories"/> folders. <see cref="Record"/> is the only writer of the report's
/// <c>sourceIntegrity.&lt;actor&gt;</c> and of the step that names it, for every launch path; <see cref="KeepsProfile"/> is the one
/// answer to whether the profile may be retired.
/// </summary>
[ResultShape]
internal sealed record SourceVerdict(string Actor, string Host, string Root, string Mode, SourceVerdictKind Verdict,
    IReadOnlyList<SourceAddedFile> Added, IReadOnlyList<SourceChangedFile> Changed, IReadOnlyList<string> Missing,
    IReadOnlyList<string> MissingDirectories, IReadOnlyList<SourceNotMoved> NotMoved, IReadOnlyList<string> RemovedDirectories,
    string? Reason = null)
{
    internal const string ContentMode = "content hashes", MetadataMode = "size and modified time";
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// False when the settlement did not finish for a reason that can pass (a host that failed or timed out, an abandoned cleanup,
    /// an I/O error on a move, a journal that refused its line): the profile, its baseline and its moved files stay, journalled
    /// kept, for a recovery to settle again. A refusal that cannot change is a finished <see cref="SourceVerdictKind.Failed"/>.
    /// </summary>
    public bool Settled { get; init; } = true;

    /// <summary>The profile the verdict is about (its journalled runtime), for the one kept-profile message.</summary>
    [JsonIgnore] internal string? Profile { get; init; }
    /// <summary>Whether a recovery settled it: its kept profile stays open in the journal for the next <c>env recover</c>, not journalled kept.</summary>
    [JsonIgnore] internal bool InRecovery { get; init; }
    /// <summary>Whether it did not finish because the install or the profile's folder does not answer (offline, or gone).</summary>
    [JsonIgnore] internal bool Unreachable { get; init; }
    /// <summary>The moved-files folders this settlement journalled kept, for a teardown to remove in the same pass.</summary>
    [JsonIgnore] internal IReadOnlyList<string> KeptFolders { get; init; } = [];

    /// <summary>Whether the caller must keep the profile instead of retiring it: a settlement that did not finish (<see cref="Settled"/>).</summary>
    [JsonIgnore] internal bool KeepsProfile => !Settled;

    /// <summary>
    /// The one answer to a caller about to retire the profile: nothing when it may, else the owner's kept-profile refusal (the
    /// settlement journalled it kept already), which the caller reports instead of retiring.
    /// </summary>
    internal void RequireRetirable(Exception? cause = null)
    {
        if (KeepsProfile) throw new SourceProfileKeptException($"{Actor}'s profile {Profile} is kept: its source settlement did not finish ({Reason}). " +
            (Unreachable ? "The install or the profile's folder is not reachable, or gone: reconnect it (an offline NAS, an unplugged volume), or run env teardown. "
                : InRecovery ? "Retry env recover, or run env teardown. " : "") +
            (InRecovery ? "The profile stays open in the journal." : "It is journalled kept: valheim-test env status names it, and env teardown settles the install again and removes it."), cause);
    }

    /// <summary>The verdict for an actor that runs its install in place.</summary>
    internal static SourceVerdict NotApplicable(string actor) => new(actor, "", "", "", SourceVerdictKind.NotApplicable, [], [], [], [], [], []);

    /// <summary>The verdict when a process launched from the actor's profile was not proven stopped: nothing was listed or moved.</summary>
    internal static SourceVerdict Unknown(string actor, string reason) => NotCompared(actor, StopNotProven + ": " + reason);
    private const string StopNotProven = "stop not proven";

    /// <summary>The verdict when the install could not be compared, and why.</summary>
    internal static SourceVerdict NotCompared(string actor, string reason) => new(actor, "", "", "", SourceVerdictKind.Unknown, [], [], [], [], [], [], reason);

    /// <summary>Whether the verdict fails the cleanup step.</summary>
    [JsonIgnore] internal bool Fails => Verdict is SourceVerdictKind.Failed or SourceVerdictKind.Unknown;

    /// <summary>The cleanup step's name: one wording for every launch path.</summary>
    [JsonIgnore] internal string StepName => $"verify {Actor}'s source install after the run ({Verdict switch
    {
        SourceVerdictKind.NotApplicable => "not applicable: runs in place",
        SourceVerdictKind.Unknown => (Reason ?? "").StartsWith(StopNotProven, StringComparison.Ordinal) ? "unknown: " + StopNotProven : "unknown: not compared",
        _ => Mode,
    }})";

    /// <summary>The report's <c>sourceIntegrity.&lt;actor&gt;</c> value.</summary>
    [JsonIgnore] internal string Text => Verdict switch
    {
        SourceVerdictKind.Unchanged => "unchanged",
        SourceVerdictKind.Restored => "restored with a warning: " + Warning,
        SourceVerdictKind.AddedElsewhere => "unchanged by the run, with a warning: " + Warning,
        SourceVerdictKind.Failed => "failed: " + Problems + ". The source is not repaired.",
        SourceVerdictKind.Unknown => "unknown: " + Reason,
        _ => "not applicable: runs in place",
    };

    // Every path, by name: what a person checks before trusting the install again.
    private string Warning => string.Join("; ", new[]
    {
        Added.Count == 0 ? null : $"the game added {Added.Count} file(s) to {Root} on {Host}, moved into the run's evidence: " +
            string.Join(", ", Added.Select(file => $"{file.Path} ({file.KeptAs})")),
        RemovedDirectories.Count == 0 ? null : "removed the empty folder(s) it made: " + string.Join(", ", RemovedDirectories),
        Verdict != SourceVerdictKind.AddedElsewhere ? null : $"{NotMoved.Count} path(s) were added to {Root} on {Host} during the run by something " +
            "other than the game's profile, and are left in place: " + string.Join(", ", NotMoved.Select(file => file.Path)),
    }.OfType<string>());

    private string Problems
    {
        get
        {
            var parts = new List<string>();
            if (Changed.Count != 0) parts.Add((Mode == ContentMode ? "different content: " : "different size or modified time: ") + string.Join(", ", Changed.Select(file => file.Path)));
            if (Missing.Count != 0) parts.Add("missing: " + string.Join(", ", Missing));
            if (MissingDirectories.Count != 0) parts.Add("missing folders: " + string.Join(", ", MissingDirectories));
            if (NotMoved.Count != 0) parts.Add("added and not moved: " + string.Join(", ", NotMoved.Select(file => $"{file.Path} ({file.Reason})")));
            if (Reason != null) parts.Add(Reason);
            if (Added.Count != 0 || RemovedDirectories.Count != 0) parts.Add(Warning);
            return string.Join("; ", parts);
        }
    }

    /// <summary>One line for a recovery step's outcome.</summary>
    internal string Describe() => Text;

    /// <summary>The verdict as <c>source-changes.json</c> holds it.</summary>
    internal string ToJson() => JsonSerializer.Serialize(this, s_json) + "\n";

    internal static SourceVerdict FromJson(string json) => JsonSerializer.Deserialize<SourceVerdict>(json, s_json)
        ?? throw new InvalidDataException("The unfinished source recovery report is empty.");

    /// <summary>
    /// Records the verdict as its <see cref="StepPhase.Cleanup"/> step and <c>sourceIntegrity.&lt;actor&gt;</c>, and prints a
    /// warning naming every path the run moved out or found added. A failed or unknown verdict fails the step and throws, so the
    /// caller's cleanup goes on and reports it; without a report (an actor made outside a session) it only throws.
    /// </summary>
    internal void Record(ScenarioReport? report)
    {
        if (report != null) lock (report.Provenance) report.Provenance["sourceIntegrity." + Actor] = Text;
        if (Verdict is SourceVerdictKind.Restored or SourceVerdictKind.AddedElsewhere) Console.Error.WriteLine($"WARNING: {Actor}'s source install: {Warning}.");
        void Check()
        {
            if (Verdict == SourceVerdictKind.Unknown) throw new IOException($"Source integrity of {Actor}'s install is unknown: {Reason}.");
            if (Verdict == SourceVerdictKind.Failed) throw new IOException($"Source integrity failed after the run ({Problems}). The source is not repaired.");
        }
        if (report != null) report.Step(StepPhase.Cleanup, StepName, Check);
        else Check();
    }
}

/// <summary>
/// Where one profile's source settlement keeps what the game added to the install: the copy's <see cref="Runtime"/> (as journalled),
/// whose parent folder on the host holds the persisted baseline and the moved files, and either the run's evidence folder
/// <see cref="Output"/> (they are fetched into <c>source-changes/&lt;actor&gt;/</c> there; without one they stay on the host,
/// journalled kept) or, in a recovery, the host attempt folder <see cref="RecoveredIn"/> below <c>&lt;runs&gt;/&lt;run&gt;/recovered-&lt;actor&gt;</c>
/// they are moved into. <see cref="Actor"/> is the one name of the verdict and its evidence.
/// </summary>
internal sealed record SourceSettlement(string Actor, string Runtime, string? Output, string? RecoveredIn)
{
    /// <summary>In a recovery: whether a moved-files folder has its own kept journal entry, so it is left to <c>env teardown</c>.</summary>
    internal Func<string, bool>? KeptApart { get; init; }
    /// <summary>Under <c>env teardown</c>: an install that is not there is gone for good, a finished failure, not one to wait for.</summary>
    internal bool Teardown { get; init; }
    internal static SourceSettlement Run(string actor, string runtime, string? output) => new(actor, runtime, output, null);
    internal static SourceSettlement Recovery(string actor, string runtime, string recoveredIn) => new(actor, runtime, null, recoveredIn);
    internal string Parent => HostPath.Parent(Runtime);
}

/// <summary>The host scripts of the source settlement: one per shell for each step, through the toolkit's one link rule.</summary>
internal static class SourceSettleScripts
{
    /// <summary>The words for a settlement script's reason token (<c>VT-NOTMOVED &lt;b64&gt; &lt;reason&gt;</c>).</summary>
    internal static string Reason(string token) => token switch
    {
        "outside" => "outside the install",
        "link" => "a link on the way, never followed",
        "not-a-file" => "not a regular file",
        "kept-already" => "the moved files already hold that name",
        "not-empty" => "the folder is not empty",
        _ => "the move failed",
    };

    // Variables: root (the source install), keep (where added files go: the profile's moved-files folder), files and folders (base64
    // relative paths, one per line; folders deepest first). Each added regular file is moved to keep/<relative> (a rename on one
    // volume, a copy and delete across two) and reported as "VT-MOVED <b64>", or left with "VT-NOTMOVED <b64> <reason>": a path
    // outside the root or through a link (never followed), anything but a regular file, a name keep already holds, or a move the
    // system refused. Then each added folder is removed when it is empty ("VT-RMDIR <b64>"). A path already gone is "VT-GONE <b64>",
    // so a repeated settlement carries on. The root's listing, not this reply, decides the verdict. A decoded name keeps a trailing
    // line break (the printf x guard); the keep folder is compared with the root ignoring case on macOS, which errs toward a refusal.
    internal static readonly string Bash = "set -u\n" + ProtectedPaths.BashInside + ProtectedPaths.BashUnlinked + """
        if [ -z "$root" ] || [ -z "$keep" ]; then echo 'VT-SETTLE-REFUSED no source install or keep folder was named'; exit 0; fi
        if [ ! -d "$root" ] || [ -L "$root" ]; then echo 'VT-SETTLE-REFUSED the source install is not a folder'; exit 0; fi
        vt_inside "$root" "$keep" fold; case $? in 1) ;; *) echo 'VT-SETTLE-REFUSED the keep folder is not outside the source install'; exit 0;; esac
        vt_relative() {
            rel=$({ printf '%s' "$1" | base64 -d 2>/dev/null || printf '%s' "$1" | base64 -D; } && printf x) || return 1
            rel=${rel%x}
            case "/$rel/" in //|/*//*|*/../*|*/./*) return 1;; esac
            case "$rel" in /*) return 1;; esac
        }
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            if ! vt_relative "$line"; then printf 'VT-NOTMOVED %s outside\n' "$line"; continue; fi
            src="$root/$rel"; target="$keep/$rel"
            if [ -n "${keepRoot:-}" ] && ! vt_unlinked "$keepRoot" "$target"; then printf 'VT-NOTMOVED %s link\n' "$line"; continue; fi
            if ! vt_unlinked "$root" "$src"; then printf 'VT-NOTMOVED %s link\n' "$line"; continue; fi
            if [ ! -e "$src" ]; then printf 'VT-GONE %s\n' "$line"; continue; fi
            if [ ! -f "$src" ]; then printf 'VT-NOTMOVED %s not-a-file\n' "$line"; continue; fi
            if [ -e "$target" ] || [ -L "$target" ]; then printf 'VT-NOTMOVED %s kept-already\n' "$line"; continue; fi
            # dirname's output, with the x guard, keeps a folder name's trailing line break.
            folder=$(dirname -- "$target"; printf x); folder=${folder%x}; folder=${folder%$'\n'}
            if mkdir -p -- "$folder" 2> /dev/null && mv -- "$src" "$target" 2> /dev/null; then printf 'VT-MOVED %s\n' "$line"
            else printf 'VT-NOTMOVED %s move-failed\n' "$line"; fi
        done <<< "$files"
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            if ! vt_relative "$line"; then printf 'VT-NOTMOVED %s outside\n' "$line"; continue; fi
            dir="$root/$rel"
            if ! vt_unlinked "$root" "$dir"; then printf 'VT-NOTMOVED %s link\n' "$line"; continue; fi
            if [ ! -d "$dir" ]; then printf 'VT-GONE %s\n' "$line"; continue; fi
            if rmdir -- "$dir" 2> /dev/null; then printf 'VT-RMDIR %s\n' "$line"; else printf 'VT-NOTMOVED %s not-empty\n' "$line"; fi
        done <<< "$folders"
        echo 'VT-SETTLED'
        """.ReplaceLineEndings("\n");

    // The bash settlement's PowerShell 7 counterpart, with the same variables and replies. Every file operation uses the \\?\ form
    // of the path, so a name Windows would trim (a trailing dot or space) is never read as another file.
    internal static readonly string PowerShell = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellUnlinked + """
        if (-not $root -or -not $keep) { 'VT-SETTLE-REFUSED no source install or keep folder was named'; exit 0 }
        $root = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
        if (-not [IO.Directory]::Exists($root) -or ([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint)) { 'VT-SETTLE-REFUSED the source install is not a folder'; exit 0 }
        $inside = $true
        try { $inside = Test-VtInside $root $keep } catch { }
        if ($inside) { 'VT-SETTLE-REFUSED the keep folder is not outside the source install'; exit 0 }
        $keep = [IO.Path]::GetFullPath($keep).TrimEnd('\', '/')
        function Get-VtRelative([string]$line) {
            try { $rel = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line)) } catch { return $null }
            if (-not $rel -or [IO.Path]::IsPathRooted($rel)) { return $null }
            foreach ($part in $rel.Split([char[]]'\/')) { if ($part -eq '' -or $part -eq '.' -or $part -eq '..') { return $null } }
            return $rel.Replace('/', '\')
        }
        function Get-VtExact([string]$path) { if ($path.StartsWith('\\')) { return '\\?\UNC\' + $path.Substring(2) } else { return '\\?\' + $path } }
        foreach ($line in ($files -split "`n")) {
            if (-not $line) { continue }
            $rel = Get-VtRelative $line
            if (-not $rel) { 'VT-NOTMOVED ' + $line + ' outside'; continue }
            $src = $root + '\' + $rel
            $target = $keep + '\' + $rel
            if ($keepRoot -and -not (Test-VtUnlinked $keepRoot $target)) { 'VT-NOTMOVED ' + $line + ' link'; continue }
            if (-not (Test-VtUnlinked $root $src)) { 'VT-NOTMOVED ' + $line + ' link'; continue }
            $exactSource = Get-VtExact $src
            $exactTarget = Get-VtExact $target
            if (-not [IO.File]::Exists($exactSource)) { if ([IO.Directory]::Exists($exactSource)) { 'VT-NOTMOVED ' + $line + ' not-a-file' } else { 'VT-GONE ' + $line }; continue }
            if ([IO.File]::Exists($exactTarget) -or [IO.Directory]::Exists($exactTarget)) { 'VT-NOTMOVED ' + $line + ' kept-already'; continue }
            try {
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($exactTarget))
                [IO.File]::Move($exactSource, $exactTarget)
                'VT-MOVED ' + $line
            } catch { 'VT-NOTMOVED ' + $line + ' move-failed' }
        }
        foreach ($line in ($folders -split "`n")) {
            if (-not $line) { continue }
            $rel = Get-VtRelative $line
            if (-not $rel) { 'VT-NOTMOVED ' + $line + ' outside'; continue }
            $dir = $root + '\' + $rel
            if (-not (Test-VtUnlinked $root $dir)) { 'VT-NOTMOVED ' + $line + ' link'; continue }
            $exact = Get-VtExact $dir
            if (-not [IO.Directory]::Exists($exact)) { 'VT-GONE ' + $line; continue }
            $removed = $false
            try { if ([IO.Directory]::GetFileSystemEntries($exact).Length -eq 0) { [IO.Directory]::Delete($exact); $removed = $true } } catch { }
            if ($removed) { 'VT-RMDIR ' + $line } else { 'VT-NOTMOVED ' + $line + ' not-empty' }
        }
        'VT-SETTLED'
        """.ReplaceLineEndings("\n");

    // Variables: root, parent. Whether both folders answer on the host: "VT-REACHABLE yes", or "VT-REACHABLE no <path>" for the first
    // that does not (an offline NAS, an unplugged volume, a folder that is gone).
    internal static readonly string BashReachable = """
        for path in "$root" "$parent"; do
            if [ ! -d "$path" ]; then printf 'VT-REACHABLE no %s\n' "$path"; exit 0; fi
        done
        echo 'VT-REACHABLE yes'
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellReachable = """
        foreach ($path in @($root, $parent)) { if (-not [IO.Directory]::Exists($path)) { 'VT-REACHABLE no ' + $path; exit 0 } }
        'VT-REACHABLE yes'
        """.ReplaceLineEndings("\n");

    // Variables: path, content (base64), optional root (unlinked write boundary), exclusive (never replace a report).
    // Writes the file whole through a temporary name; ordinary baseline writes replace their previous version.
    internal static readonly string BashWrite = ProtectedPaths.BashUnlinked + """
        set -u
        if [ -n "${root:-}" ]; then vt_unlinked "$root" "$path" && vt_unlinked "$root" "$path.vt-tmp" || exit 3; fi
        if [ -n "${exclusive:-}" ] && { [ -e "$path" ] || [ -L "$path" ]; }; then exit 3; fi
        mkdir -p -- "$(dirname -- "$path")" || exit 3
        temporary="$path.vt-tmp"
        if [ -n "${exclusive:-}" ]; then temporary=$(mktemp "$path.vt-tmp.XXXXXX") || exit 3; fi
        if ! { printf '%s' "$content" | base64 -d 2>/dev/null || printf '%s' "$content" | base64 -D; } > "$temporary"; then
            [ -z "${exclusive:-}" ] || rm -f -- "$temporary"
            exit 3
        fi
        if [ -n "${exclusive:-}" ]; then
            # POSIX link publishes this exact destination atomically, never replacing it or treating a directory as a container.
            if ! link "$temporary" "$path"; then rm -f -- "$temporary"; exit 3; fi
            rm -f -- "$temporary" || exit 3
        else mv -f -- "$temporary" "$path" || exit 3
        fi
        echo 'VT-SOURCE-WRITTEN'
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellWrite = ProtectedPaths.PowerShellUnlinked + """
        if ($root -and (-not (Test-VtUnlinked $root $path) -or -not (Test-VtUnlinked $root ($path + '.vt-tmp')))) { throw 'The source report destination passes through a link' }
        if ($exclusive -and (Test-Path -LiteralPath $path)) { throw 'The source report already exists' }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        $bytes = [Convert]::FromBase64String($content)
        if ($exclusive) {
            $temporary = $path + '.vt-tmp.' + [Guid]::NewGuid().ToString('N')
            $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
                [IO.File]::Move($temporary, $path, $false)
            } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        } else {
            [IO.File]::WriteAllBytes($path + '.vt-tmp', $bytes)
            [IO.File]::Move($path + '.vt-tmp', $path, $true)
        }
        'VT-SOURCE-WRITTEN'
        """.ReplaceLineEndings("\n");

    // Variables: parent (the profile's folder), keep (its moved-files folder, a source-added-* folder directly in parent), added (1:
    // remove keep, fetched or holding no file), recovered (a recovery's per-profile folder in recovered-<actor>: keep is moved into it
    // as added, never removed), baseline (1: remove the baseline, last). Every delete stays below the parent (ProtectedPaths). A path that breaks
    // these rules is refused ("VT-SOURCE-REFUSED <why>"), never retried: it cannot change.
    internal static readonly string BashEvidence = "set -u\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + ProtectedPaths.BashUnlinked + """
        if [ -z "$parent" ] || [ -z "$keep" ]; then echo 'VT-SOURCE-REFUSED no profile folder or moved files folder was named'; exit 0; fi
        if [ "$(dirname -- "$keep")" != "$parent" ]; then echo "VT-SOURCE-REFUSED $keep is not directly in $parent"; exit 0; fi
        case "$(basename -- "$keep")" in source-added|source-added-*) ;; *) echo "VT-SOURCE-REFUSED $keep is not a moved files folder"; exit 0;; esac
        if [ -n "$recovered" ]; then
            if [ -n "${recoveredRoot:-}" ] && ! vt_unlinked "$recoveredRoot" "$recovered/added"; then echo 'VT-SOURCE-REFUSED the recovered destination passes through a link'; exit 0; fi
            if [ -L "$keep" ]; then echo 'VT-SOURCE-REFUSED the moved files folder is a link'; exit 0; fi
            if [ -d "$keep" ]; then
                to="$recovered/added"
                if [ -e "$to" ] || [ -L "$to" ]; then echo "VT-SOURCE-REFUSED $to already exists"; exit 0; fi
                mkdir -p -- "$recovered" && mv -- "$keep" "$to" || exit 3
            fi
        elif [ "${added:-}" = 1 ]; then vt_remove "$keep" "$parent" || exit 3
        fi
        if [ "${baseline:-}" = 1 ]; then vt_remove "$parent/source-baseline.json" "$parent" || exit 3; fi
        echo 'VT-SOURCE-SETTLED'
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellEvidence = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + ProtectedPaths.PowerShellUnlinked + """
        if (-not $parent -or -not $keep) { 'VT-SOURCE-REFUSED no profile folder or moved files folder was named'; exit 0 }
        # Both sides as full paths, one separator: a runtime written with / is the same folder (GetDirectoryName rewrites / to \).
        function Get-VtFull([string]$path) { return [IO.Path]::GetFullPath($path).Replace('/', '\').TrimEnd('\') }
        if (-not [string]::Equals((Get-VtFull ([IO.Path]::GetDirectoryName((Get-VtFull $keep)))), (Get-VtFull $parent), [StringComparison]::OrdinalIgnoreCase)) {
            'VT-SOURCE-REFUSED ' + $keep + ' is not directly in ' + $parent; exit 0
        }
        if (-not ([IO.Path]::GetFileName((Get-VtFull $keep)) -like 'source-added*')) { 'VT-SOURCE-REFUSED ' + $keep + ' is not a moved files folder'; exit 0 }
        if ($recovered) {
            if ($recoveredRoot -and -not (Test-VtUnlinked $recoveredRoot (Join-Path $recovered 'added'))) { 'VT-SOURCE-REFUSED the recovered destination passes through a link'; exit 0 }
            if (Test-VtLink $keep) { 'VT-SOURCE-REFUSED the moved files folder is a link'; exit 0 }
            if ([IO.Directory]::Exists($keep)) {
                $to = Join-Path $recovered 'added'
                if ([IO.Directory]::Exists($to) -or [IO.File]::Exists($to)) { 'VT-SOURCE-REFUSED ' + $to + ' already exists'; exit 0 }
                [void][IO.Directory]::CreateDirectory($recovered)
                [IO.Directory]::Move($keep, $to)
            }
        }
        elseif ($added -eq '1') { Remove-VtTree $keep $parent }
        if ($baseline -eq '1') { Remove-VtTree (Join-Path $parent 'source-baseline.json') $parent }
        'VT-SOURCE-SETTLED'
        """.ReplaceLineEndings("\n");
}

/// <summary>A profile kept because its source settlement did not finish (<see cref="SourceVerdict.RequireRetirable"/>): the owner's one message.</summary>
internal sealed class SourceProfileKeptException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// A settlement step refused on the host for a reason that cannot change (a source install that is not a folder, a keep folder
/// inside it, a moved-files folder that is not the profile's, a recovered folder already there): the verdict is a finished
/// <see cref="SourceVerdictKind.Failed"/> naming it, never a retry.
/// </summary>
internal sealed class SourceRefusedException(string message) : IOException(message);

/// <summary>The install a settlement lists does not answer on its host: an offline volume, or one that is gone. It may pass, so the profile stays.</summary>
internal sealed class SourceUnreachableException(string message) : IOException(message);
