using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>What a run did to one actor's source install, as <see cref="ProfileSourceCheck.SettleAsync"/> found and left it.</summary>
internal enum SourceVerdictKind
{
    /// <summary>The install lists as it did before the launch.</summary>
    Unchanged,
    /// <summary>The game added files or folders there; they were moved out (files into the run's evidence) and the install lists as before. Passes with a warning.</summary>
    Restored,
    /// <summary>A file changed or went missing, or an added one could not be moved: the source is not repaired. Fails the cleanup step.</summary>
    Failed,
    /// <summary>Not checked: a process launched from the profile was not proven stopped. Fails the cleanup step.</summary>
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

/// <summary>A path the settlement left in the install, and why.</summary>
[ResultShape]
internal sealed record SourceNotMoved(string Path, string Reason);

/// <summary>
/// The one source-install verdict of an actor's run (#612), written to <c>source-changes/&lt;actor&gt;/source-changes.json</c> (in a
/// recovery, <c>recovered-&lt;actor&gt;/source-changes.json</c> on the host). <see cref="Record"/> is the only writer of the
/// report's <c>sourceIntegrity.&lt;actor&gt;</c> and of the step that names it, for every launch path.
/// </summary>
[ResultShape]
internal sealed record SourceVerdict(string Actor, string Host, string Root, string Mode, SourceVerdictKind Verdict,
    IReadOnlyList<SourceAddedFile> Added, IReadOnlyList<SourceChangedFile> Changed, IReadOnlyList<string> Missing,
    IReadOnlyList<SourceNotMoved> NotMoved, IReadOnlyList<string> RemovedDirectories, string? Reason = null)
{
    internal const string ContentMode = "content hashes", MetadataMode = "size and modified time";
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The verdict for an actor that runs its install in place.</summary>
    internal static SourceVerdict NotApplicable(string actor) => new(actor, "", "", "", SourceVerdictKind.NotApplicable, [], [], [], [], []);

    /// <summary>The verdict when a process launched from the actor's profile was not proven stopped: nothing was listed or moved.</summary>
    internal static SourceVerdict Unknown(string actor, string reason) => new(actor, "", "", "", SourceVerdictKind.Unknown, [], [], [], [], [], reason);

    /// <summary>Whether the verdict fails the cleanup step.</summary>
    [JsonIgnore] internal bool Fails => Verdict is SourceVerdictKind.Failed or SourceVerdictKind.Unknown;

    /// <summary>The cleanup step's name: one wording for every launch path.</summary>
    [JsonIgnore] internal string StepName => $"verify {Actor}'s source install after the run ({Verdict switch
    {
        SourceVerdictKind.NotApplicable => "not applicable: runs in place",
        SourceVerdictKind.Unknown => "unknown: stop not proven",
        _ => Mode,
    }})";

    /// <summary>The report's <c>sourceIntegrity.&lt;actor&gt;</c> value.</summary>
    [JsonIgnore] internal string Text => Verdict switch
    {
        SourceVerdictKind.Unchanged => "unchanged",
        SourceVerdictKind.Restored => "restored with a warning: " + Warning,
        SourceVerdictKind.Failed => "failed: " + Problems + ". The source is not repaired.",
        SourceVerdictKind.Unknown => "unknown: stop not proven: " + Reason,
        _ => "not applicable: runs in place",
    };

    // Every path the game added, by name: what a person checks before trusting the install again.
    private string Warning => string.Join("; ", new[]
    {
        Added.Count == 0 ? null : $"the game added {Added.Count} file(s) to {Root} on {Host}, moved into the run's evidence: " +
            string.Join(", ", Added.Select(file => $"{file.Path} ({file.KeptAs})")),
        RemovedDirectories.Count == 0 ? null : "removed the empty folder(s) it made: " + string.Join(", ", RemovedDirectories),
    }.OfType<string>());

    private string Problems
    {
        get
        {
            var parts = new List<string>();
            if (Changed.Count != 0) parts.Add((Mode == ContentMode ? "different content: " : "different size or modified time: ") + string.Join(", ", Changed.Select(file => file.Path)));
            if (Missing.Count != 0) parts.Add("missing: " + string.Join(", ", Missing));
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

    /// <summary>
    /// Records the verdict as its <see cref="StepPhase.Cleanup"/> step and <c>sourceIntegrity.&lt;actor&gt;</c>, and prints a
    /// restored install's warning naming every path. A failed or unknown verdict fails the step and throws, so the caller's
    /// cleanup goes on and reports it; without a report (an actor made outside a session) it only throws.
    /// </summary>
    internal void Record(ScenarioReport? report)
    {
        if (report != null) lock (report.Provenance) report.Provenance["sourceIntegrity." + Actor] = Text;
        if (Verdict == SourceVerdictKind.Restored) Console.Error.WriteLine($"WARNING: {Actor}'s source install was restored: {Warning}.");
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
/// whose parent folder on the host holds the persisted baseline and the moved files, and either the run's local
/// <see cref="Output"/> (they are fetched into <c>source-changes/&lt;actor&gt;/</c> there) or, in a recovery, the host folder
/// <see cref="RecoveredIn"/> (<c>&lt;runs&gt;/&lt;run&gt;/recovered-&lt;actor&gt;</c>) they are moved into.
/// </summary>
internal sealed record SourceSettlement(string Actor, string Runtime, string? Output, string? RecoveredIn)
{
    internal static SourceSettlement Run(string actor, string runtime, string output) => new(actor, runtime, output, null);
    internal static SourceSettlement Recovery(string actor, string runtime, string recoveredIn) => new(actor, runtime, null, recoveredIn);
    internal string Parent => HostPath.Parent(Runtime);
}

/// <summary>The host scripts of the source settlement: one per shell for each step, through the toolkit's one link rule.</summary>
internal static class SourceSettleScripts
{
    // Variables: root (the source install), keep (where added files go: <parent>/source-added), files and folders (base64 relative
    // paths, one per line; folders deepest first). Each added regular file is moved to keep/<relative> (a rename on one volume, a
    // copy and delete across two) and reported as "VT-MOVED <b64> <bytes> <sha256>", or left with "VT-NOTMOVED <b64> <reason>":
    // a path outside the root or through a link (never followed), anything but a regular file, a name keep already holds, or a
    // move the system refused. Then each added folder is removed when it is empty ("VT-RMDIR <b64>"). A path already gone is
    // "VT-GONE <b64>", so a repeated settlement carries on. The root's listing, not this reply, decides the verdict.
    internal static readonly string Bash = "set -u\n" + ProtectedPaths.BashInside + ProtectedPaths.BashUnlinked + """
        [ -n "$root" ] && [ -n "$keep" ] || exit 3
        if [ ! -d "$root" ] || [ -L "$root" ]; then echo 'VT-SETTLE-REFUSED the source install is not a folder'; exit 0; fi
        vt_inside "$root" "$keep"; case $? in 1) ;; *) echo 'VT-SETTLE-REFUSED the keep folder is not outside the source install'; exit 0;; esac
        if command -v sha256sum > /dev/null 2>&1; then hasher=(sha256sum); else hasher=(shasum -a 256); fi
        vt_relative() {
            rel=$(printf '%s' "$1" | base64 -d 2>/dev/null || printf '%s' "$1" | base64 -D) || return 1
            case "/$rel/" in //|/*//*|*/../*|*/./*) return 1;; esac
            case "$rel" in /*) return 1;; esac
        }
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            if ! vt_relative "$line"; then printf 'VT-NOTMOVED %s outside\n' "$line"; continue; fi
            src="$root/$rel"; target="$keep/$rel"
            if ! vt_unlinked "$root" "$src"; then printf 'VT-NOTMOVED %s link\n' "$line"; continue; fi
            if [ ! -e "$src" ]; then printf 'VT-GONE %s\n' "$line"; continue; fi
            if [ ! -f "$src" ]; then printf 'VT-NOTMOVED %s not-a-file\n' "$line"; continue; fi
            if [ -e "$target" ] || [ -L "$target" ]; then printf 'VT-NOTMOVED %s kept-already\n' "$line"; continue; fi
            if [ "$vt_darwin" = 1 ]; then size=$(stat -f %z -- "$src") || exit 3; else size=$(stat -c %s -- "$src") || exit 3; fi
            sum=$("${hasher[@]}" < "$src") || { printf 'VT-NOTMOVED %s move-failed\n' "$line"; continue; }
            if mkdir -p -- "$(dirname -- "$target")" 2> /dev/null && mv -- "$src" "$target" 2> /dev/null; then
                printf 'VT-MOVED %s %s %s\n' "$line" "$size" "${sum%% *}"
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

    // The bash settlement's PowerShell 7 counterpart, with the same variables and replies.
    internal static readonly string PowerShell = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellUnlinked + """
        if (-not $root -or -not $keep) { exit 3 }
        if (-not [IO.Directory]::Exists($root) -or ([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint)) { 'VT-SETTLE-REFUSED the source install is not a folder'; exit 0 }
        $inside = $true
        try { $inside = Test-VtInside $root $keep } catch { }
        if ($inside) { 'VT-SETTLE-REFUSED the keep folder is not outside the source install'; exit 0 }
        function Get-VtRelative([string]$line) {
            try { $rel = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line)) } catch { return $null }
            if (-not $rel -or [IO.Path]::IsPathRooted($rel)) { return $null }
            foreach ($part in $rel.Split([char[]]'\/')) { if ($part -eq '' -or $part -eq '.' -or $part -eq '..') { return $null } }
            return $rel.Replace('/', '\')
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            foreach ($line in ($files -split "`n")) {
                if (-not $line) { continue }
                $rel = Get-VtRelative $line
                if (-not $rel) { 'VT-NOTMOVED ' + $line + ' outside'; continue }
                $src = Join-Path $root $rel
                $target = Join-Path $keep $rel
                if (-not (Test-VtUnlinked $root $src)) { 'VT-NOTMOVED ' + $line + ' link'; continue }
                if (-not [IO.File]::Exists($src)) { if ([IO.Directory]::Exists($src)) { 'VT-NOTMOVED ' + $line + ' not-a-file' } else { 'VT-GONE ' + $line }; continue }
                if ([IO.File]::Exists($target) -or [IO.Directory]::Exists($target)) { 'VT-NOTMOVED ' + $line + ' kept-already'; continue }
                try {
                    $size = (New-Object IO.FileInfo $src).Length
                    $stream = [IO.File]::OpenRead($src)
                    try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose() }
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                    [IO.File]::Move($src, $target)
                    'VT-MOVED ' + $line + ' ' + $size + ' ' + $hash
                } catch { 'VT-NOTMOVED ' + $line + ' move-failed' }
            }
        } finally { $sha.Dispose() }
        foreach ($line in ($folders -split "`n")) {
            if (-not $line) { continue }
            $rel = Get-VtRelative $line
            if (-not $rel) { 'VT-NOTMOVED ' + $line + ' outside'; continue }
            $dir = Join-Path $root $rel
            if (-not (Test-VtUnlinked $root $dir)) { 'VT-NOTMOVED ' + $line + ' link'; continue }
            if (-not [IO.Directory]::Exists($dir)) { 'VT-GONE ' + $line; continue }
            $removed = $false
            try { if ([IO.Directory]::GetFileSystemEntries($dir).Length -eq 0) { [IO.Directory]::Delete($dir); $removed = $true } } catch { }
            if ($removed) { 'VT-RMDIR ' + $line } else { 'VT-NOTMOVED ' + $line + ' not-empty' }
        }
        'VT-SETTLED'
        """.ReplaceLineEndings("\n");

    // Variables: path, content (base64). Writes the file whole (a temporary name, then a rename), replacing an earlier one.
    internal static readonly string BashWrite = """
        set -u
        mkdir -p -- "$(dirname -- "$path")" || exit 3
        { printf '%s' "$content" | base64 -d 2>/dev/null || printf '%s' "$content" | base64 -D; } > "$path.vt-tmp" || exit 3
        mv -f -- "$path.vt-tmp" "$path" || exit 3
        echo 'VT-SOURCE-WRITTEN'
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellWrite = """
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllBytes($path + '.vt-tmp', [Convert]::FromBase64String($content))
        [IO.File]::Move($path + '.vt-tmp', $path, $true)
        'VT-SOURCE-WRITTEN'
        """.ReplaceLineEndings("\n");

    // Variables: parent (the profile's folder), added (1: remove its fetched source-added folder too), recovered (a recovery's
    // recovered-<actor> folder: source-added is moved into it, never removed). The baseline goes last, once the moved files are
    // settled; every delete stays below the parent (ProtectedPaths).
    internal static readonly string BashEvidence = "set -u\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + """
        [ -n "$parent" ] || exit 3
        from="$parent/source-added"
        if [ -n "$recovered" ]; then
            if [ -L "$from" ]; then echo 'VT-SOURCE-REFUSED the moved files folder is a link'; exit 0; fi
            if [ -d "$from" ]; then
                to="$recovered/source-added"
                if [ -e "$to" ] || [ -L "$to" ]; then echo "VT-SOURCE-REFUSED $to already exists"; exit 0; fi
                mkdir -p -- "$recovered" && mv -- "$from" "$to" || exit 3
            fi
        elif [ "${added:-}" = 1 ]; then vt_remove "$from" "$parent" || exit 3
        fi
        vt_remove "$parent/source-baseline.json" "$parent" || exit 3
        echo 'VT-SOURCE-SETTLED'
        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellEvidence = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + """
        if (-not $parent) { exit 3 }
        $from = Join-Path $parent 'source-added'
        if ($recovered) {
            if (Test-VtLink $from) { 'VT-SOURCE-REFUSED the moved files folder is a link'; exit 0 }
            if ([IO.Directory]::Exists($from)) {
                $to = Join-Path $recovered 'source-added'
                if ([IO.Directory]::Exists($to) -or [IO.File]::Exists($to)) { 'VT-SOURCE-REFUSED ' + $to + ' already exists'; exit 0 }
                [void][IO.Directory]::CreateDirectory($recovered)
                [IO.Directory]::Move($from, $to)
            }
        }
        elseif ($added -eq '1') { Remove-VtTree $from $parent }
        Remove-VtTree (Join-Path $parent 'source-baseline.json') $parent
        'VT-SOURCE-SETTLED'
        """.ReplaceLineEndings("\n");
}
