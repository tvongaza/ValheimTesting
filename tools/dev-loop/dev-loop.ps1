# One round of a mod's edit-build-test loop on Windows: build, install,
# launch, run a test plan, and summarise the log.
#
#   dev-loop.ps1 <MyMod.csproj> [test-plan.yaml]
#
# PowerShell twin of dev-loop.sh, for Windows PowerShell 5.1 and PowerShell 7.
# Keep the two in step: same steps, environment and exit codes. Run it with
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-loop\dev-loop.ps1 MyMod.csproj tools\dev-loop\smoke-plan.yaml
#
# smoke-plan.yaml, next to this script, is a strict plan to start from.
#
#   1. dotnet build -c Release (stops on a failed build)
#   2. with a plan: writes a temporary copy of VALHEIM_EXPECTATIONS whose pin
#      for this mod is the md5 of the DLL just built (every other line kept)
#   3. copies the built DLL (and its .pdb) into BepInEx\plugins
#   4. launches Valheim and runs the plan with valheim-cli --expect-strict
#      <derived pins> --test ... --launch (without a plan: launches and waits
#      until the console is ready)
#   5. prints the warnings and errors in BepInEx\LogOutput.log by level and
#      source (a short form of log-summary.sh): a plan that passed while the
#      mod logged errors is worth a look
#
# Every wait prints a heartbeat (WAIT: elapsed, state, load phase, what
# changed) every PROGRESS, so a slow load and a stuck one look different.
# A plan's waitFor step also fails early when nothing changes for STALL, or
# at once when the game sits in a state that cannot reach the target (a
# world when the plan waits for the main menu).
#
# The game must not be running: a running game keeps the old build and locks
# the file. Quit it first, or reload without a restart via BepInEx
# ScriptEngine where your mod supports it.
#
# Exit code: the build's if it failed, otherwise the plan's (or the launch's);
# 1 no built or installed DLL, 3 no BepInEx\plugins, 4 usage or pins file,
# 5 the game is running or its state is unknown.
#
# Environment:
#   VALHEIM_CLI       valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_EXPECTATIONS  required pins file when running a test plan; strict mode.
#                     It must pin this mod exactly once; that pin is replaced
#                     by the fresh build's md5, never by what the game reports
#   VALHEIM_PLUGIN_KEY  the key that pins this mod in that file: its GUID, name
#                     or DLL name (default: the built DLL's name without .dll)
#   VALHEIM_CLI_PORT  valheimCLI port (default 5555)
#   VALHEIM_PATH      game folder that contains BepInEx
#                     (default: C:\Program Files (x86)\Steam\steamapps\common\Valheim)
#   VALHEIM_LOG       log to summarise instead of <VALHEIM_PATH>\BepInEx\LogOutput.log
#   CONFIGURATION     build configuration (default Release)
#   STOP_AFTER=1      quit the game after a plan that passed
#   PROGRESS          heartbeat interval, e.g. 30s (default 15s; 0 disables)
#   STALL             end a plan's wait after this long without change
#                     (default 120s; 0 disables; a step's own stall: wins)
#
# Moved from ValheimCLI (commit ee4cd23) to ValheimTesting on 28 Sep 2026.
# valheim-cli itself comes from a ValheimCLI release or build; see
# tools/dev-loop/README.md.

$derived = ''

# ${NAME:-default}: the variable, or the default when it is unset or empty.
function Get-Setting([string]$Name, [string]$Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    return $value
}

function Write-Problem([string]$Message) { [Console]::Error.WriteLine($Message) }

# A path relative to the current location, as a full path .NET methods accept.
function Get-FullPath([string]$Path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Get-Md5([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm MD5 -ErrorAction Stop).Hash.ToLowerInvariant()
}

# The pins file with the one line whose key is $PinKey (case-insensitive)
# replaced by $PinKey=$Hash, or $null unless exactly one line pins that key.
function Get-DerivedPins([string]$Source, [string]$PinKey, [string]$Hash) {
    $lines = New-Object System.Collections.Generic.List[string]
    $replaced = 0
    foreach ($line in [IO.File]::ReadAllLines($Source)) {
        $eq = $line.IndexOf('=')
        if ($eq -ge 0 -and [string]::Equals($line.Substring(0, $eq).Trim(" `t".ToCharArray()), $PinKey, [StringComparison]::OrdinalIgnoreCase)) {
            $lines.Add($PinKey + '=' + $Hash + '   # derived by dev-loop.ps1 from the build it deployed')
            $replaced++
            continue
        }
        $lines.Add($line)
    }
    if ($replaced -ne 1) { return $null }
    return ($lines -join "`n") + "`n"
}

function Get-DefaultGamePath {
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ([string]::IsNullOrEmpty($programFiles)) { $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles') }
    return [IO.Path]::Combine([string]$programFiles, 'Steam', 'steamapps', 'common', 'Valheim')
}

function Remove-Derived {
    if ($derived -ne '' -and (Test-Path -LiteralPath $derived)) { Remove-Item -LiteralPath $derived -Force -ErrorAction SilentlyContinue }
}

# Warning, Error and Fatal lines of a BepInEx log, counted by level and source.
function Write-LogSummary([string]$Log) {
    if (-not (Test-Path -LiteralPath $Log -PathType Leaf)) { Write-Problem "ERROR: no log at $Log"; return }
    $rows = @{}
    $total = @{ Fatal = 0; Error = 0; Warning = 0 }
    # The game may still be writing the log.
    $reader = New-Object System.IO.StreamReader([IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite'))
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            $m = [regex]::Match($line, '^\[(Warning|Error|Fatal) *: *([^\]]*)\] ')
            if (-not $m.Success) { continue }
            $level = $m.Groups[1].Value
            $rowKey = $level + "`t" + $m.Groups[2].Value.TrimEnd()
            $rows[$rowKey] = 1 + [int]$rows[$rowKey]
            $total[$level] = 1 + [int]$total[$level]
        }
    } finally {
        $reader.Dispose()
    }
    Write-Output ('{0,-8} {1,6}  {2}' -f 'LEVEL', 'COUNT', 'SOURCE')
    foreach ($row in ($rows.GetEnumerator() | Sort-Object -Property Value -Descending)) {
        $parts = $row.Key -split "`t", 2
        Write-Output ('{0,-8} {1,6}  {2}' -f $parts[0], $row.Value, $parts[1])
    }
    Write-Output ''
    Write-Output ('total: fatal={0} error={1} warning={2}' -f $total['Fatal'], $total['Error'], $total['Warning'])
}

# Any other failure (a copy, a hash, a missing tool) stops the loop with exit 1.
trap {
    Write-Problem "ERROR: $_"
    Remove-Derived
    exit 1
}

if ($args.Count -lt 1) {
    Write-Problem 'usage: dev-loop.ps1 <MyMod.csproj> [test-plan.yaml]'
    exit 4
}
$project = [string]$args[0]
$plan = ''
if ($args.Count -ge 2) { $plan = [string]$args[1] }
$cli = Get-Setting 'VALHEIM_CLI' 'valheim-cli'
$port = Get-Setting 'VALHEIM_CLI_PORT' '5555'
$config = Get-Setting 'CONFIGURATION' 'Release'
$pins = Get-Setting 'VALHEIM_EXPECTATIONS' ''
$pinsPath = ''
if ($pins -ne '') { $pinsPath = Get-FullPath $pins }
if ($plan -ne '' -and ($pinsPath -eq '' -or -not [IO.File]::Exists($pinsPath) -or (New-Object IO.FileInfo $pinsPath).Length -eq 0)) {
    Write-Problem 'ERROR: set VALHEIM_EXPECTATIONS to a non-empty pins file before running a test plan'
    exit 4
}

$game = Get-FullPath (Get-Setting 'VALHEIM_PATH' (Get-DefaultGamePath))
$plugins = [IO.Path]::Combine($game, 'BepInEx', 'plugins')
if (-not (Test-Path -LiteralPath $plugins -PathType Container)) {
    Write-Problem "ERROR: no BepInEx\plugins under $game (set VALHEIM_PATH)"
    exit 3
}

# --status exits nonzero when the plugin is unavailable, even when its
# independent local-process check found the game. Inspect that evidence
# separately; an absent/unreadable status is not permission to deploy.
$statusText = ''
try {
    $statusText = (& $cli --port $port --status 2>$null) -join "`n"
} catch {
    $statusText = ''
}
if ($statusText -cmatch '(^|\s)local_process=true(\s|$)') {
    Write-Problem 'ERROR: Valheim is running; quit it first so the new build is the one that loads'
    exit 5
}
if ($statusText -cnotmatch '(^|\s)local_process=false(\s|$)') {
    Write-Problem 'ERROR: cannot establish that Valheim is stopped; check --status before deploying'
    exit 5
}

Write-Output "== build ($config)"
& dotnet build $project -c $config -nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$dll = ((& dotnet msbuild $project '-getProperty:TargetPath' "-p:Configuration=$config") -join "`n").Trim()
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($dll -eq '' -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    Write-Problem "ERROR: the build reported $dll, which does not exist"
    exit 1
}
$dllName = [IO.Path]::GetFileName($dll)

$md5 = ''
if ($plan -ne '') {
    # Pin the artifact this script deploys, not whatever the game reports.
    $md5 = Get-Md5 $dll
    $key = Get-Setting 'VALHEIM_PLUGIN_KEY' ([IO.Path]::GetFileNameWithoutExtension($dll))
    $derived = [IO.Path]::GetTempFileName()
    $derivedText = Get-DerivedPins $pinsPath $key $md5
    if ($null -eq $derivedText) {
        Write-Problem "ERROR: $pins must pin $key exactly once; set VALHEIM_PLUGIN_KEY to the GUID, name or DLL name it uses"
        Remove-Derived
        exit 4
    }
    [IO.File]::WriteAllText($derived, $derivedText, (New-Object System.Text.UTF8Encoding $false))
    Write-Output "== pins: $key=$md5 (derived copy of $pins)"
}

Write-Output "== install $dllName -> $plugins"
$installed = [IO.Path]::Combine($plugins, $dllName)
Copy-Item -LiteralPath $dll -Destination $installed -Force -ErrorAction Stop
$pdb = [IO.Path]::ChangeExtension($dll, '.pdb')
if (Test-Path -LiteralPath $pdb -PathType Leaf) {
    Copy-Item -LiteralPath $pdb -Destination ([IO.Path]::Combine($plugins, [IO.Path]::GetFileName($pdb))) -Force -ErrorAction Stop
}
if ($plan -ne '' -and (Get-Md5 $installed) -ne $md5) {
    Write-Problem "ERROR: the installed $dllName differs from the build"
    Remove-Derived
    exit 1
}

$waits = @()
$progress = Get-Setting 'PROGRESS' ''
if ($progress -ne '') { $waits += @('--progress', $progress) }
$stall = Get-Setting 'STALL' ''
if ($stall -ne '') { $waits += @('--stall', $stall) }

$status = 0
if ($plan -ne '') {
    Write-Output "== launch and run $plan"
    $stop = @()
    if ((Get-Setting 'STOP_AFTER' '0') -eq '1') { $stop = @('--stop-after') }
    & $cli --port $port --expect-strict $derived --test $plan --launch @stop @waits
    $status = $LASTEXITCODE
} else {
    Write-Output '== launch'
    & $cli --port $port --launch --timeout '300s' @waits
    $status = $LASTEXITCODE
}

Write-Output '== log'
try {
    Write-LogSummary (Get-FullPath (Get-Setting 'VALHEIM_LOG' ([IO.Path]::Combine($game, 'BepInEx', 'LogOutput.log'))))
} catch {
    Write-Problem "ERROR: cannot summarise the log: $_"
}
Remove-Derived
exit $status
