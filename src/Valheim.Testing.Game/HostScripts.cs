namespace Valheim.Testing.Game;

// The fixed text a game host runs. Values never appear here: each script reads them from the variables
// ScriptedGameHost.Compose puts in front of it as literals. Every script answers with one verdict line on stdout (or, for a
// fetch, stderr) that the host class requires before it reports success, and exits non-zero on anything unexpected.
// Line endings are normalised because a checkout may have converted this file to CRLF.
internal static class HostScripts
{
    // Started as `bash -c '<this>'` (by the ssh login shell, docker exec or locally), so it holds no single quote and no
    // newline. It reads the script (one base64 line), keeps the rest of stdin as the upload, runs the script with an empty
    // stdin and reports its exit code on its own line of stderr. A script that never arrived intact ends without that report.
    public const string BashWrapper =
        "d=$(mktemp -d) || exit 125; " +
        "if IFS= read -r s && printf %s \"$s\" | base64 -d > \"$d/script\" && cat > \"$d/upload\"; then " +
        "VT_UPLOAD=\"$d/upload\" \"$BASH\" \"$d/script\" < /dev/null; r=$?; rm -rf \"$d\"; " +
        "printf \"\\n[vt-exit] %d\\n\" \"$r\" >&2; exit \"$r\"; fi; " +
        "rm -rf \"$d\"; echo \"vt: the script did not arrive intact\" >&2; exit 125";

    // Sent as -EncodedCommand, so its length (not the script's) meets the command-line limit. Runs under Windows
    // PowerShell 5.1 and PowerShell 7. Output goes through Console.Out as UTF-8, so 5.1 does not re-encode it to the
    // OEM code page, and errors are written as plain text rather than CLIXML.
    public static readonly string PowerShellWrapper = """
        try { [Console]::OutputEncoding = New-Object Text.UTF8Encoding $false } catch { }
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $vtErr = [Console]::Error
        $vtDir = Join-Path ([IO.Path]::GetTempPath()) ('vt-' + [Guid]::NewGuid().ToString('N'))
        try {
            $vtIn = [Console]::OpenStandardInput()
            $vtLine = New-Object IO.MemoryStream
            $vtBuffer = New-Object byte[] 65536
            $vtRest = -1
            while ($vtRest -lt 0) {
                $vtRead = $vtIn.Read($vtBuffer, 0, $vtBuffer.Length)
                if ($vtRead -le 0) { throw 'the script did not arrive intact' }
                $vtAt = [Array]::IndexOf($vtBuffer, [byte]10, 0, $vtRead)
                if ($vtAt -lt 0) { $vtLine.Write($vtBuffer, 0, $vtRead) } else { $vtLine.Write($vtBuffer, 0, $vtAt); $vtRest = $vtAt + 1 }
            }
            [void][IO.Directory]::CreateDirectory($vtDir)
            $vtScript = Join-Path $vtDir 'script.ps1'
            $vtText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Text.Encoding]::ASCII.GetString($vtLine.ToArray())))
            # With a byte order mark, so Windows PowerShell 5.1 reads the file as UTF-8.
            [IO.File]::WriteAllText($vtScript, $vtText, (New-Object Text.UTF8Encoding $true))
            $env:VT_UPLOAD = Join-Path $vtDir 'upload'
            $vtFile = [IO.File]::Create($env:VT_UPLOAD)
            try { $vtFile.Write($vtBuffer, $vtRest, $vtRead - $vtRest); $vtIn.CopyTo($vtFile) } finally { $vtFile.Dispose() }
        } catch {
            $vtErr.WriteLine('vt: ' + $_.Exception.Message)
            Remove-Item -LiteralPath $vtDir -Recurse -Force -ErrorAction SilentlyContinue
            exit 125
        }
        $vtCode = 1
        try {
            $global:LASTEXITCODE = 0
            & $vtScript | Out-String -Stream | ForEach-Object { [Console]::Out.WriteLine($_) }
            $vtCode = $LASTEXITCODE
        } catch {
            $vtErr.WriteLine(($_ | Out-String).Trim())
        } finally {
            Remove-Item -LiteralPath $vtDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        [Console]::Out.Flush()
        $vtErr.WriteLine()
        $vtErr.WriteLine('[vt-exit] ' + $vtCode)
        $vtErr.Flush()
        exit $vtCode
        """.ReplaceLineEndings("\n");

    public static string Lock(HostShellKind kind) => kind == HostShellKind.Bash ? BashLock : PowerShellLock;
    public static string Ship(HostShellKind kind) => kind == HostShellKind.Bash ? BashShip : PowerShellShip;
    public static string Size(HostShellKind kind) => kind == HostShellKind.Bash ? BashSize : PowerShellSize;
    public static string Follow(HostShellKind kind) => kind == HostShellKind.Bash ? BashFollow : PowerShellFollow;
    public static string Fetch(HostShellKind kind) => kind == HostShellKind.Bash ? BashFetch : PowerShellFetch;

    // $action is claim, release or check. The owner file's exclusive creation (noclobber opens with O_EXCL) is the claim.
    // Owners are compared as exact strings; `x` keeps trailing newlines a hand-edited file may have.
    private static readonly string BashLock = """
        set -u
        f="$lock/owner"
        case "$action" in
        claim)
            mkdir -p -- "$lock" || exit 3
            if ( set -C; printf %s "$owner" > "$f" ) 2>/dev/null; then
                date -u +%Y-%m-%dT%H:%M:%SZ > "$lock/since" || exit 3
                echo "VT-LOCK claimed"; exit 0
            fi
            [ -f "$f" ] || exit 3 ;;
        release|check)
            if [ ! -e "$f" ]; then echo "VT-LOCK free"; exit 0; fi ;;
        *) exit 2 ;;
        esac
        if [ ! -s "$f" ]; then echo "VT-LOCK unowned"; exit 0; fi
        current=$(cat -- "$f" && printf x) || exit 3
        current=${current%x}
        if [ "$current" != "$owner" ]; then echo "VT-LOCK held"; printf '%s\n' "$current"; exit 0; fi
        if [ "$action" != release ]; then echo "VT-LOCK yours"; exit 0; fi
        rm -f -- "$lock/since" && rm -f -- "$f" || exit 3
        [ ! -e "$f" ] || exit 3
        echo "VT-LOCK released"
        """.ReplaceLineEndings("\n");

    // New-Item and Directory.CreateDirectory succeed on an existing directory, so the atomic step is FileMode.CreateNew.
    private static readonly string PowerShellLock = """
        $utf8 = New-Object Text.UTF8Encoding $false
        $file = Join-Path $lock 'owner'
        if ($action -ceq 'claim') {
            [void][IO.Directory]::CreateDirectory($lock)
            $stream = $null
            try { $stream = New-Object IO.FileStream -ArgumentList $file, ([IO.FileMode]::CreateNew), ([IO.FileAccess]::Write), ([IO.FileShare]::Read) }
            catch { if (-not [IO.File]::Exists($file)) { throw } }
            if ($null -ne $stream) {
                try { $bytes = $utf8.GetBytes($owner); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
                [IO.File]::WriteAllText((Join-Path $lock 'since'), [DateTime]::UtcNow.ToString('s') + 'Z', $utf8)
                'VT-LOCK claimed'; exit 0
            }
        } elseif ($action -cne 'release' -and $action -cne 'check') { exit 2 }
        elseif (-not [IO.File]::Exists($file)) { 'VT-LOCK free'; exit 0 }
        $current = [IO.File]::ReadAllText($file, $utf8)
        if ($current.Length -eq 0) { 'VT-LOCK unowned'; exit 0 }
        if (-not [string]::Equals($current, $owner, [StringComparison]::Ordinal)) { 'VT-LOCK held'; $current; exit 0 }
        if ($action -cne 'release') { 'VT-LOCK yours'; exit 0 }
        [IO.File]::Delete((Join-Path $lock 'since'))
        [IO.File]::Delete($file)
        if ([IO.File]::Exists($file)) { throw 'the owner file is still there after removal' }
        'VT-LOCK released'
        """.ReplaceLineEndings("\n");

    // $record is SOURCE.txt's text (what was shipped); the archive's hash is appended to it.
    private static readonly string BashShip = """
        set -u
        if [ -e "$dest" ]; then echo "VT-SHIP exists"; exit 0; fi
        if command -v sha256sum > /dev/null 2>&1; then got=$(sha256sum < "$VT_UPLOAD"); else got=$(shasum -a 256 < "$VT_UPLOAD"); fi || exit 3
        got=${got%% *}
        if [ "$got" != "$sha256" ]; then echo "VT-SHIP hash $got"; exit 0; fi
        mkdir -p -- "$(dirname -- "$dest")" && mkdir -- "$dest" || exit 3
        if ! tar -xf "$VT_UPLOAD" -C "$dest"; then rm -rf -- "$dest"; exit 3; fi
        if [ -e "$dest/SOURCE.txt" ]; then rm -rf -- "$dest"; echo "VT-SHIP source-txt"; exit 0; fi
        printf '%ssha256=%s\n' "$record" "$sha256" > "$dest/SOURCE.txt" || exit 3
        echo "VT-SHIP shipped $got"
        """.ReplaceLineEndings("\n");

    // Windows 10 and Server 2019 onwards ship bsdtar as System32\tar.exe; it is preferred over any other tar on PATH.
    private const string PowerShellTar = """
        $system = [Environment]::SystemDirectory
        $tar = 'tar'
        if ($system -and [IO.File]::Exists((Join-Path $system 'tar.exe'))) { $tar = Join-Path $system 'tar.exe' }
        """;

    // Get-FileHash is a script function of the Utility module, which Windows PowerShell 5.1 cannot load when it inherits
    // PowerShell 7's PSModulePath (for example when pwsh is the SSH login shell), so the hash is computed with .NET.
    private const string PowerShellSha256 = """
        function Get-VtSha256([string]$path) {
            $stream = [IO.File]::OpenRead($path)
            $sha = [Security.Cryptography.SHA256]::Create()
            try { ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
        }
        """;

    private static readonly string PowerShellShip = (PowerShellSha256 + """

        if (Test-Path -LiteralPath $dest) { 'VT-SHIP exists'; exit 0 }
        $got = Get-VtSha256 $env:VT_UPLOAD
        if ($got -cne $sha256) { 'VT-SHIP hash ' + $got; exit 0 }
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $dest))
        [void](New-Item -ItemType Directory -Path $dest)

        """ + PowerShellTar + """

        & $tar -xf $env:VT_UPLOAD -C $dest
        if ($LASTEXITCODE -ne 0) { Remove-Item -LiteralPath $dest -Recurse -Force; throw ('tar exited with ' + $LASTEXITCODE) }
        if (Test-Path -LiteralPath (Join-Path $dest 'SOURCE.txt')) { Remove-Item -LiteralPath $dest -Recurse -Force; 'VT-SHIP source-txt'; exit 0 }
        [IO.File]::WriteAllText((Join-Path $dest 'SOURCE.txt'), $record + 'sha256=' + $sha256 + "`n", (New-Object Text.UTF8Encoding $false))
        'VT-SHIP shipped ' + $got
        """).ReplaceLineEndings("\n");

    private static readonly string BashSize = """
        set -u
        if [ -e "$log" ]; then n=$(wc -c < "$log") || exit 3; echo "VT-SIZE $((n))"; else echo "VT-SIZE 0"; fi
        """.ReplaceLineEndings("\n");

    private static readonly string PowerShellSize = """
        if ([IO.File]::Exists($log)) { 'VT-SIZE ' + (New-Object IO.FileInfo -ArgumentList $log).Length } else { 'VT-SIZE 0' }
        """.ReplaceLineEndings("\n");

    // tail -F follows the name (a log that is replaced, or truncated, is read again from its start) and is event driven on
    // Linux (inotify). Not every tail can follow a name that does not exist yet, so a missing log is looked for once a
    // second first. The watchdog ends tail a little after the controller's own deadline even if the controller's ssh was
    // stopped; its output goes nowhere so it never holds the session open.
    private static readonly string BashFollow = """
        set -u
        while [ ! -e "$log" ]; do
            if [ "$SECONDS" -ge "$seconds" ]; then exit 0; fi
            sleep 1
        done
        tail -c "+$((offset + 1))" -F -- "$log" 2> /dev/null &
        t=$!
        ( sleep "$((seconds - SECONDS + 1))"; kill "$t" ) > /dev/null 2>&1 &
        w=$!
        wait "$t"
        kill "$w" 2> /dev/null
        exit 0
        """.ReplaceLineEndings("\n");

    // Reads complete lines from the offset and sleeps on a FileSystemWatcher between reads. A log shorter than the
    // position read so far was truncated or replaced and is read from its start again. The watcher can miss events
    // (network filesystems), so it also re-reads every 2 s. Without the log's directory there is nothing to watch yet,
    // and the directory is looked for once a second.
    private static readonly string PowerShellFollow = """
        $deadline = [DateTime]::UtcNow.AddSeconds([double]$seconds)
        $utf8 = New-Object Text.UTF8Encoding $false
        $out = [Console]::Out
        $dir = [IO.Path]::GetDirectoryName($log)
        $name = [IO.Path]::GetFileName($log)
        $position = [long]$offset
        $pending = New-Object IO.MemoryStream
        $buffer = New-Object byte[] 65536
        $watcher = $null
        while ($true) {
            if ($null -eq $watcher -and [IO.Directory]::Exists($dir)) {
                $watcher = New-Object IO.FileSystemWatcher -ArgumentList $dir, $name
                $watcher.NotifyFilter = [IO.NotifyFilters]'FileName, LastWrite, Size, CreationTime'
            }
            if ([IO.File]::Exists($log)) {
                $stream = New-Object IO.FileStream -ArgumentList $log, ([IO.FileMode]::Open), ([IO.FileAccess]::Read), ([IO.FileShare]'ReadWrite, Delete')
                try {
                    if ($stream.Length -lt $position) { $position = 0; $pending.SetLength(0) }
                    [void]$stream.Seek($position, [IO.SeekOrigin]::Begin)
                    while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $start = 0
                        while ($start -lt $read) {
                            $newline = [Array]::IndexOf($buffer, [byte]10, $start, $read - $start)
                            if ($newline -lt 0) { $pending.Write($buffer, $start, $read - $start); break }
                            $pending.Write($buffer, $start, $newline - $start)
                            $out.WriteLine($utf8.GetString($pending.ToArray()))
                            $pending.SetLength(0)
                            $start = $newline + 1
                        }
                        $position += $read
                    }
                    $out.Flush()
                } finally { $stream.Dispose() }
            }
            $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
            if ($left -le 0) { exit 0 }
            if ($null -ne $watcher) { [void]$watcher.WaitForChanged([IO.WatcherChangeTypes]::All, [Math]::Min($left, 2000)) }
            else { Start-Sleep -Milliseconds ([Math]::Min($left, 1000)) }
        }
        """.ReplaceLineEndings("\n");

    // The archive is made in a temporary file first so its hash and size can be reported (on stderr; stdout carries only
    // the archive). COPYFILE_DISABLE keeps macOS tar from adding ._* AppleDouble files.
    private static readonly string BashFetch = """
        set -u
        if [ ! -d "$dir" ]; then echo "vt: not a directory: $dir" >&2; exit 3; fi
        t=$(mktemp) || exit 3
        if ! COPYFILE_DISABLE=1 tar -cf "$t" -C "$dir" .; then rm -f -- "$t"; exit 3; fi
        if command -v sha256sum > /dev/null 2>&1; then h=$(sha256sum < "$t"); else h=$(shasum -a 256 < "$t"); fi || { rm -f -- "$t"; exit 3; }
        n=$(wc -c < "$t") || { rm -f -- "$t"; exit 3; }
        echo "VT-FETCH ${h%% *} $((n))" >&2
        cat -- "$t"; r=$?
        rm -f -- "$t"
        exit "$r"
        """.ReplaceLineEndings("\n");

    // Written to the raw stdout stream: PowerShell's own pipeline would re-encode the bytes as text.
    private static readonly string PowerShellFetch = (PowerShellSha256 + """

        if (-not [IO.Directory]::Exists($dir)) { throw ('not a directory: ' + $dir) }
        $env:COPYFILE_DISABLE = '1'

        """ + PowerShellTar + """

        $archive = [IO.Path]::GetTempFileName()
        try {
            & $tar -cf $archive -C $dir .
            if ($LASTEXITCODE -ne 0) { throw ('tar exited with ' + $LASTEXITCODE) }
            $hash = Get-VtSha256 $archive
            [Console]::Error.WriteLine('VT-FETCH ' + $hash + ' ' + (New-Object IO.FileInfo -ArgumentList $archive).Length)
            [Console]::Error.Flush()
            $stdout = [Console]::OpenStandardOutput()
            $in = [IO.File]::OpenRead($archive)
            try { $in.CopyTo($stdout); $stdout.Flush() } finally { $in.Dispose() }
        } finally { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
        """).ReplaceLineEndings("\n");
}
