# Hash a Valheim world's save folder the way cli_world does, to prove which
# saved state a game loaded (its `files=` field and the `worldfiles` key).
#
#   world-hash.ps1 WORLD              hash VALHEIM_SAVES\WORLD
#   world-hash.ps1 C:\path\to\folder  hash that folder
#   world-hash.ps1 WORLD --compare    also ask the running game (cli_world) and
#                                     exit 1 when the two differ
#
# PowerShell twin of world-hash.sh, for Windows PowerShell 5.1 and PowerShell 7.
# Keep the two in step: same arguments, environment, output and exit codes. Run it with
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-loop\world-hash.ps1 Dev --compare
#
# Recipe (ValheimCLI's docs/expectations.md, "World files hash"): for every
# file under the folder, recursively, the line "relative/path:md5" with `/`
# as the separator; lines sorted in ordinal (byte) order, joined by \n with no
# trailing newline; the md5 of that text (UTF-8), all in lower-case hex.
#
# Use it on a copy you restore before each run: hash the copy, then compare
# with the game after it loads. The game hashes the folder just before it
# loads the world, so any later save does not change its answer; this script
# hashes the folder as it is now.
#
# Environment:
#   VALHEIM_SAVES     the worlds_local folder (default: the Steam save folder,
#                     %USERPROFILE%\AppData\LocalLow\IronGate\Valheim\worlds_local;
#                     a dedicated server started with -savedir keeps its worlds
#                     under that folder instead)
#   VALHEIM_CLI       path to the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  the game's CLI port (default 5555), for --compare
#
# --compare needs the game running as the server or host (a client has no
# world files). For a game on another machine, forward its port first:
# ssh -N -L 5555:127.0.0.1:5555 host
#
# Exit code: 0 hashed (and with --compare, the game agrees); 1 the game
# reported another hash or none; valheim-cli's own code when the game could
# not be asked (3: no connection); 4 usage, or no folder to hash.
#
# Moved from ValheimCLI (commit d112140, bash only) to ValheimTesting on
# 30 Sep 2026, where this twin was added; valheim-cli itself comes from a
# ValheimCLI release or build, see tools/dev-loop/README.md.

function Get-Setting([string]$Name, [string]$Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    return $value
}

function Exit-Usage {
    [Console]::Error.WriteLine(@'
world-hash.ps1 WORLD              hash VALHEIM_SAVES\WORLD
world-hash.ps1 C:\path\to\folder  hash that folder
world-hash.ps1 WORLD --compare    also ask the running game (cli_world) and
                                  exit 1 when the two differ
'@)
    exit 4
}

# A missing valheim-cli or an unreadable file stops the script with exit 1.
trap {
    [Console]::Error.WriteLine("ERROR: $_")
    exit 1
}

# Lower-case hex of an md5 digest.
function ConvertTo-LowerHex([byte[]]$Bytes) {
    return ([BitConverter]::ToString($Bytes) -replace '-', '').ToLowerInvariant()
}

$saves = Get-Setting 'VALHEIM_SAVES' (Join-Path $env:USERPROFILE 'AppData\LocalLow\IronGate\Valheim\worlds_local')
$cli = Get-Setting 'VALHEIM_CLI' 'valheim-cli'
$port = Get-Setting 'VALHEIM_CLI_PORT' '5555'

if ($args.Count -lt 1 -or $args.Count -gt 2) { Exit-Usage }
$target = [string]$args[0]
$compare = $false
if ($args.Count -eq 2) {
    if ([string]$args[1] -cne '--compare') { Exit-Usage }
    $compare = $true
}

if (Test-Path -LiteralPath $target -PathType Container) { $dir = $target }
else { $dir = Join-Path $saves $target }
if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
    [Console]::Error.WriteLine("ERROR: no world folder at $dir")
    exit 4
}

$root = (Resolve-Path -LiteralPath $dir).ProviderPath.TrimEnd('\', '/')
$md5 = [System.Security.Cryptography.MD5]::Create()
try {
    $lines = New-Object 'System.Collections.Generic.List[string]'
    # -Force includes hidden and system files, as the game's Directory.GetFiles does.
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try { $lines.Add($relative + ':' + (ConvertTo-LowerHex ($md5.ComputeHash($stream)))) }
        finally { $stream.Dispose() }
    }
    $lines.Sort([StringComparer]::Ordinal)
    $text = [string]::Join("`n", $lines.ToArray())
    $hash = ConvertTo-LowerHex ($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)))
}
finally {
    $md5.Dispose()
}
Write-Output "$hash  $dir"

if ($compare) {
    $reply = @(& $cli --port $port cli_world)
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        foreach ($line in $reply) { [Console]::Error.WriteLine($line) }
        exit $code
    }
    $game = ''
    foreach ($line in $reply) {
        if ([string]$line -cmatch '^WORLD .* files=([^ ]*) ') { $game = $Matches[1] }
    }
    if ($game -eq '' -or $game -eq '-') {
        [Console]::Error.WriteLine('ERROR: the game reported no load-time hash (not a server or host, or no world loaded)')
        exit 1
    }
    if ($game -ceq $hash) {
        Write-Output 'OK: the game loaded this saved state'
    }
    else {
        [Console]::Error.WriteLine("MISMATCH: the game loaded $game")
        exit 1
    }
}
exit 0
