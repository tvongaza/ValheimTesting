namespace Valheim.Testing.Game;

/// <summary>
/// The one owner of "is this path inside that protected folder". Every output, install, source and fixture check asks here:
/// on this machine through <see cref="Contains"/> and <see cref="Resolve"/>, and on a game host through the script fragments
/// <see cref="BashInside"/> and <see cref="PowerShellInside"/>, which apply the same rule there, resolving links on the host.
/// </summary>
internal static class ProtectedPaths
{
    private const int MaxLinkHops = 40;

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies under it, after both are resolved to their physical locations.</summary>
    internal static bool Contains(string root, string path)
    {
        // The usual Windows and macOS volumes ignore case. Refuse a possible overlap even on a case-sensitive Mac volume.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Resolve(root));
        path = Path.TrimEndingDirectorySeparator(Resolve(path));
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// The physical location of a path, walked component by component: each existing link (symlink or junction) is replaced
    /// by its target and the walk continues there, so <c>/tmp</c> becomes <c>/private/tmp</c>, a symlinked folder inside an install is
    /// the install, and a <c>..</c> in the given path after a link goes up from the link's target (one inside a link's own target text is collapsed as written). The final path need not exist yet. More than
    /// <see cref="MaxLinkHops"/> links in one path, which includes any link loop, throws <see cref="IOException"/>.
    /// </summary>
    internal static string Resolve(string path)
    {
        // GetFullPath would collapse "link/.." lexically, before the link is followed.
        string raw = Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);
        string root = Path.GetPathRoot(raw)!;
        string current = Path.GetFullPath(root); // "C:/" becomes "C:\" on Windows
        var pending = new Stack<string>(Parts(raw[root.Length..]).Reverse());
        int hops = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".") continue;
            if (part == "..") { current = Directory.GetParent(current)?.FullName ?? current; continue; }
            // The part holds no dot segments now, so GetFullPath only normalizes what Windows does: trailing dots and spaces.
            string next = Path.GetFullPath(Path.Combine(current, part));
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            // A dangling link is still a link: its target may be a protected folder that is created later.
            if (!info.Exists && info.LinkTarget == null) { current = next; continue; }
            FileSystemInfo? target;
            try { target = info.ResolveLinkTarget(returnFinalTarget: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Cannot read the symlink or junction {next} in {path}.", error);
            }
            if (target == null) { current = next; continue; }
            if (++hops > MaxLinkHops)
                throw new IOException($"The path {path} passes through more than {MaxLinkHops} symlinks or junctions; it may be a link loop.");
            current = Path.GetPathRoot(target.FullName)!;
            foreach (string again in Parts(target.FullName[current.Length..]).Reverse()) pending.Push(again);
        }
        return current;
    }

    /// <summary>
    /// Proves a write stays under its trusted lexical root without traversing a link/junction in that root or below it.
    /// Ancestors above the boundary may include ordinary system aliases such as macOS /var -> /private/var.
    /// </summary>
    internal static void RequireUnlinkedPath(string root, string path)
    {
        root = Path.GetFullPath(root);
        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || Parts(relative).Any(part => part == ".."))
            throw new IOException($"The path {path} is outside its trusted root {root}.");
        string current = root;
        FileHash.RefuseLink(current);
        foreach (string part in Parts(relative).Where(part => part != "."))
        {
            current = Path.Combine(current, part);
            FileHash.RefuseLink(current);
        }
    }

    // Host counterparts of RequireUnlinkedPath. These fragments own the same rooted ancestry rule.
    internal static readonly string BashUnlinked = """
        vt_unlinked() {
            local root=${1%/} path=$2 relative current part
            case "$path" in "$root"|"$root"/*) ;; *) return 1 ;; esac
            [ ! -L "$root" ] || return 1
            relative=${path#"$root"}; relative=${relative#/}; current=$root
            while [ -n "$relative" ]; do
                part=${relative%%/*}
                case "$part" in .|..) return 1 ;; esac
                current="$current/$part"; [ ! -L "$current" ] || return 1
                [ "$relative" != "$part" ] || break
                relative=${relative#*/}
            done
        }

        """.ReplaceLineEndings("\n");

    internal static readonly string PowerShellUnlinked = """
        function Test-VtUnlinked([string]$root, [string]$path) {
            $current = [IO.Path]::GetFullPath($root)
            $relative = [IO.Path]::GetRelativePath($current, [IO.Path]::GetFullPath($path))
            $parts = @($relative -split '[\\/]')
            if ([IO.Path]::IsPathRooted($relative) -or $parts -contains '..') { return $false }
            foreach ($part in @('') + $parts) {
                if ($part -and $part -ne '.') { $current = [IO.Path]::Combine($current, $part) }
                try { $attributes = [IO.File]::GetAttributes($current) }
                catch [IO.FileNotFoundException] { continue }
                catch [IO.DirectoryNotFoundException] { continue }
                if ($attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
            }
            return $true
        }

        """.ReplaceLineEndings("\n");

    private static string[] Parts(string relative) =>
        relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The host side of <see cref="Contains"/> for a bash host, put in front of a host script that needs it. <c>vt_physical PATH</c>
    /// prints PATH's physical location on the host: each link on the way (one to a file or a directory, dangling or not) is
    /// followed to its target, an existing directory is what <c>cd -P</c> and <c>pwd -P</c> find (so <c>/tmp</c> is
    /// <c>/private/tmp</c> on macOS), and a part that does not exist yet is kept as written. It fails on a relative path, a
    /// <c>.</c> or <c>..</c> in the part that does not exist, a link it cannot read and more than 40 links. <c>vt_inside ROOT
    /// PATH [fold]</c> succeeds when PATH is ROOT or lies under it once both are resolved; it returns 2 when either cannot be
    /// resolved, which a caller treats as a refusal. With <c>fold</c> it ignores case on macOS as <see cref="Contains"/> does,
    /// which errs toward a refusal; a caller that allows something when the path is inside (keeping or removing a file) leaves it
    /// out, so a case-sensitive volume's other folder is never taken for the copy.
    /// </summary>
    internal static readonly string BashInside = """
        vt_darwin=0; if [ "$(uname -s)" = Darwin ]; then vt_darwin=1; fi
        vt_physical() {
            local path=$1 rest="" hops=0 target target_parent
            case "$path" in /*) ;; *) return 1;; esac
            while [ "$path" != / ] && [ "${path%/}" != "$path" ]; do path=${path%/}; done
            while :; do
                if [ -L "$path" ]; then
                    hops=$((hops + 1)); [ "$hops" -le 40 ] || return 1
                    target=$(readlink -- "$path") || return 1
                    # dirname of /tmp is /: join without doubling the slash (//private/tmp is not /private/tmp to a string compare).
                    case "$target" in /*) path=$target;; *) target_parent=$(dirname -- "$path"); path=${target_parent%/}/$target;; esac
                    while [ "$path" != / ] && [ "${path%/}" != "$path" ]; do path=${path%/}; done
                    continue
                fi
                if [ -d "$path" ]; then path=$(cd -P -- "$path" 2> /dev/null && pwd -P) || return 1; break; fi
                case "${path##*/}" in .|..) return 1;; esac
                rest=/${path##*/}$rest
                path=$(dirname -- "$path")
            done
            if [ "$path" = / ]; then printf '%s\n' "${rest:-/}"; else printf '%s\n' "$path$rest"; fi
        }
        vt_inside() {
            local root path verdict=1 folded=0 fold=${3:-}
            root=$(vt_physical "$1") && path=$(vt_physical "$2") || return 2
            [ "$root" != / ] || return 0
            shopt -q nocasematch && folded=1
            if [ "$vt_darwin" = 1 ] && [ "$fold" = fold ]; then shopt -s nocasematch; fi
            case "$path" in "$root"|"$root"/*) verdict=0;; esac
            [ "$folded" = 1 ] || shopt -u nocasematch
            return "$verdict"
        }

        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Removal under a root, for a bash host script that already holds <see cref="BashInside"/> (it uses <c>vt_physical</c>).
    /// <c>vt_remove PATH ROOT [check]</c> deletes PATH only when it lies strictly below ROOT once links are resolved, and returns 3
    /// and deletes nothing when it does not, when either cannot be resolved or when PATH is not absolute (or 4 when the delete itself
    /// failed). With <c>check</c> it only decides, so a script can check every path before it deletes any. A PATH that is itself a
    /// link (to a directory or a file, dangling or not) is unlinked and its target is never entered or emptied; the link only has to
    /// stand inside ROOT. A PATH that does not exist is already gone. A trailing slash on PATH is dropped first: BSD <c>rm</c> follows
    /// <c>link/</c>. A folder below PATH that the user cannot write or enter (a read-only extract, mode 555) is made writable first, because
    /// <c>rm -rf</c> cannot empty it; <c>find</c> does not follow links and <c>-type d</c> never matches one. This is the delete of a runtime copy and its staging folder (cleanup and retire) that a host script was handed by name.
    /// </summary>
    internal static readonly string BashRemove = """
        vt_remove() {
            local path=$1 root=$2 mode=${3:-} base real
            case "$path" in /*) ;; *) return 3;; esac
            while [ "$path" != / ] && [ "${path%/}" != "$path" ]; do path=${path%/}; done
            base=$(vt_physical "$root") || return 3
            [ "$base" != / ] || return 3
            if [ -L "$path" ]; then
                real=$(vt_physical "$(dirname -- "$path")") || return 3
                case "$real" in "$base"|"$base"/*) ;; *) return 3;; esac
                [ "$mode" = check ] && return 0
                rm -f -- "$path" || return 4
                return 0
            fi
            [ -e "$path" ] || return 0
            real=$(vt_physical "$path") || return 3
            case "$real" in "$base"/*) ;; *) return 3;; esac
            [ "$mode" = check ] && return 0
            # A read-only folder keeps rm from emptying it. find -P never follows a link, and -type d never matches one.
            find -P "$path" -type d ! -perm -700 -exec chmod u+rwx {} \; 2> /dev/null || :
            rm -rf -- "$path" || return 4
        }

        """.ReplaceLineEndings("\n");

    /// <summary>
    /// <see cref="BashRemove"/> for a Windows host's PowerShell 7, put after <see cref="PowerShellInside"/>. <c>Remove-VtTree $path $root</c>
    /// unlinks a symlink or junction without entering it, deletes a folder or file only when it lies strictly below the root once
    /// links are resolved, and throws (deleting nothing) otherwise; with <c>-Check</c> it only decides. A path that does not exist is
    /// already gone. A reparse point that is not a symlink or junction (a cloud placeholder) is an ordinary folder, as <c>Get-VtPhysical</c> reads it.
    /// A delete that Windows refuses (a read-only file, folder or link, which <c>Remove-Item -Force</c> used to delete) is repeated entry by entry:
    /// each read-only attribute is cleared just before its entry goes (except on a file with another hard link, which shares the attribute: that
    /// delete fails as it did), and a link is unlinked without being entered, its target left as it is. The path is already strictly inside the
    /// root, so the walk stays inside it.
    /// </summary>
    internal static readonly string PowerShellRemove = """
        function Test-VtLink([string]$path) {
            $attributes = $null
            try { $attributes = [IO.File]::GetAttributes($path) } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
            if ($null -eq $attributes -or -not ($attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
            $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
            return ($item.LinkType -eq 'SymbolicLink' -or $item.LinkType -eq 'Junction')
        }
        # Entry by entry, bottom up: a link is unlinked and never entered, a read-only file or folder is made writable just before it goes.
        function Remove-VtWalk([string]$path) {
            $attributes = [IO.File]::GetAttributes($path)
            $isDirectory = [bool]($attributes -band [IO.FileAttributes]::Directory)
            if ($isDirectory -and -not (Test-VtLink $path)) {
                foreach ($entry in @([IO.Directory]::EnumerateFileSystemEntries($path))) { Remove-VtWalk $entry }
            }
            if ($attributes -band [IO.FileAttributes]::ReadOnly) {
                # Hard links share their attributes: clearing the bit here would also change the other name (a game file in the install).
                if (-not $isDirectory -and (Get-Item -LiteralPath $path -Force).LinkType -eq 'HardLink') { throw ('Refusing to clear the read-only attribute of a file with another hard link: ' + $path) }
                [IO.File]::SetAttributes($path, [IO.FileAttributes]($attributes -bxor [IO.FileAttributes]::ReadOnly))
            }
            if ($isDirectory) { [IO.Directory]::Delete($path) } else { [IO.File]::Delete($path) }
        }
        function Remove-VtEntry([string]$path, [bool]$isDirectory, [bool]$tree) {
            # The system's own delete first: the walk costs a call per entry, and most trees hold nothing it would refuse.
            try { if ($isDirectory) { [IO.Directory]::Delete($path, $tree) } else { [IO.File]::Delete($path) } }
            catch { Remove-VtWalk $path }
        }
        function Remove-VtTree([string]$path, [string]$root, [switch]$check) {
            $sep = [IO.Path]::DirectorySeparatorChar
            $comparison = [StringComparison]::OrdinalIgnoreCase
            $path = $path.TrimEnd([char[]]@('\', '/'))
            # 'C:' (a drive root trimmed) names the current directory on that drive, never a folder to remove.
            if (-not $path -or $path.EndsWith(':')) { throw ('Refusing to remove a volume root: ' + $path) }
            $physicalRoot = Get-VtPhysical $root
            $base = $physicalRoot.TrimEnd($sep)
            # A volume root trimmed ('C:') is the root's own path root.
            if (-not $base -or [string]::Equals($base, [IO.Path]::GetPathRoot($physicalRoot).TrimEnd($sep), $comparison)) { throw ('Refusing to remove below a volume root: ' + $path) }
            $attributes = $null
            try { $attributes = [IO.File]::GetAttributes($path) } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
            if ($null -eq $attributes) { return }
            $isDirectory = [bool]($attributes -band [IO.FileAttributes]::Directory)
            if (Test-VtLink $path) {
                $real = (Get-VtPhysical ([IO.Path]::GetDirectoryName($path))).TrimEnd($sep)
                if (-not ([string]::Equals($real, $base, $comparison) -or $real.StartsWith($base + $sep, $comparison))) { throw ('Refusing to remove a link outside ' + $root + ': ' + $path) }
                if ($check) { return }
                Remove-VtEntry $path $isDirectory $false
                return
            }
            $real = (Get-VtPhysical $path).TrimEnd($sep)
            if (-not $real.StartsWith($base + $sep, $comparison)) { throw ('Refusing to remove outside ' + $root + ': ' + $path) }
            if ($check) { return }
            Remove-VtEntry $path $isDirectory $true
        }

        """.ReplaceLineEndings("\n");

    /// <summary>
    /// The host side of <see cref="Contains"/> for a Windows host's PowerShell 7 (pwsh).
    /// <c>Get-VtPhysical $path</c> returns the path's physical location, walked from the root down as <see cref="Resolve"/> walks:
    /// each symlink or junction met is replaced by its target (a relative one joined to the link's physical parent) and the walk
    /// continues there, and a part that does not exist yet is kept as written. It
    /// throws on a relative path, a link whose target it cannot read or that names a volume rather than a path, and more than
    /// 40 links. <c>Test-VtInside $root $path</c> is true when the path is the root or lies under it once both are resolved,
    /// compared ignoring case as Windows does.
    /// </summary>
    internal static readonly string PowerShellInside = """
        function Get-VtPhysical([string]$path) {
            if (-not [IO.Path]::IsPathRooted($path)) { throw ('Not an absolute path: ' + $path) }
            $separators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $pending = New-Object 'Collections.Generic.Stack[string]'
            function Push-VtParts([string]$full) {
                $parts = @($full.Substring([IO.Path]::GetPathRoot($full).Length).Split($separators, [StringSplitOptions]::RemoveEmptyEntries))
                for ($i = $parts.Count - 1; $i -ge 0; $i--) { $pending.Push($parts[$i]) }
            }
            # Windows collapses .. as text before any link is followed, so GetFullPath matches what the system opens.
            # Walked from the root down, as ProtectedPaths.Resolve walks: a link's relative target is joined to its physical parent.
            $full = [IO.Path]::GetFullPath($path)
            $current = [IO.Path]::GetPathRoot($full)
            Push-VtParts $full
            $hops = 0
            while ($pending.Count -gt 0) {
                $part = $pending.Pop()
                if ($part -eq '.') { continue }
                if ($part -eq '..') { $up = [IO.Path]::GetDirectoryName($current); if ($up) { $current = $up }; continue }
                $next = [IO.Path]::Combine($current, $part)
                $attributes = $null
                try { $attributes = [IO.File]::GetAttributes($next) } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
                if ($null -eq $attributes -or -not ($attributes -band [IO.FileAttributes]::ReparsePoint)) { $current = $next; continue }
                $item = Get-Item -LiteralPath $next -Force -ErrorAction Stop
                if ($item.LinkType -ne 'SymbolicLink' -and $item.LinkType -ne 'Junction') { $current = $next; continue }
                $target = @($item.Target)[0]
                if (-not $target -or $target.StartsWith('\\?\') -or $target.StartsWith('Volume{')) { throw ('Cannot follow the link ' + $next) }
                # A link to a share can read as UNC\server\share once \??\ is stripped.
                if ($target.StartsWith('UNC\')) { $target = '\' + $target.Substring(3) }
                $hops++
                if ($hops -gt 40) { throw ('More than 40 links in ' + $path + '; it may be a link loop') }
                $full = [IO.Path]::GetFullPath([IO.Path]::Combine($current, $target))
                $current = [IO.Path]::GetPathRoot($full)
                Push-VtParts $full
            }
            return $current
        }
        function Test-VtInside([string]$root, [string]$path) {
            $sep = [IO.Path]::DirectorySeparatorChar
            $comparison = [StringComparison]::OrdinalIgnoreCase
            $r = (Get-VtPhysical $root).TrimEnd($sep)
            $p = (Get-VtPhysical $path).TrimEnd($sep)
            if (-not $r) { return $true }
            return ([string]::Equals($p, $r, $comparison) -or $p.StartsWith($r + $sep, $comparison))
        }

        """.ReplaceLineEndings("\n");
}
