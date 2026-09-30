# sample-value.ps1: sample one static member of the game or of a mod at a fixed
# interval and write the time series as CSV on stdout.
#
# Usage:
#   sample-value.ps1 <[Namespace.]Type.Member> <count> <interval-seconds> [arg ...] > samples.csv
#
#   sample-value.ps1 EnvMan.IsDay 10 5 > day.csv
#   sample-value.ps1 MyMod.Diagnostics.QueueLength 120 0.5 > queue.csv
#   sample-value.ps1 Utils.DistanceXZ 3 1 0,0,0 3,100,4
#
# PowerShell twin of sample-value.sh, for Windows PowerShell 5.1 and PowerShell 7.
# Keep the two in step: same arguments, columns, environment and exit codes. Run it with
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-loop\sample-value.ps1 EnvMan.IsDay 10 5 > day.csv
#
# Each sample is one `cli_call <member> [arg ...]` (see ValheimCLI's
# docs/cli-call.md; the member comes from its Reflection pack).
# Columns: sample,utc,value,error
#   value  the VALUE text exactly as cli_call prints it (a string keeps its
#          quotes, a vector its commas; the field is CSV-quoted), or the item
#          count when the member returns a collection
#   error  the first line of the reply when the call failed
# A failed first sample stops the script with the reply on stderr (the member
# or its arguments are wrong). A later failure is recorded and sampling goes on.
#
# The interval is the pause between calls; each call adds a few milliseconds.
# The pause sets the sampling rate of a time series. It is not a wait for a
# condition: for that, use an async command or `valheim-cli wait --for`.
#
# cli_call is cheat-gated: run `valheim-cli devcommands` in the world first.
#
# Environment:
#   VALHEIM_CLI       path to the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  the game's CLI port (default: 5555)
# A remote game works through an SSH tunnel to its CLI port:
#   ssh -N -L 5555:127.0.0.1:5555 user@game-host
#
# Exit code: 0 all samples written (a later failed sample is a row, not an
# exit code); 1 the first sample failed; 3 valheim-cli not found; 4 usage.
#
# Moved from ValheimCLI (commit d112140, bash only) to ValheimTesting on
# 30 Sep 2026, where this twin was added; valheim-cli itself comes from a
# ValheimCLI release or build, see tools/dev-loop/README.md.

function Get-Setting([string]$Name, [string]$Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    return $value
}

# A field in double quotes, with its own double quotes doubled.
function ConvertTo-CsvField([string]$Text) {
    return '"' + $Text.Replace('"', '""') + '"'
}

if ($args.Count -lt 3) {
    [Console]::Error.WriteLine('usage: sample-value.ps1 <[Namespace.]Type.Member> <count> <interval-seconds> [arg ...]')
    exit 4
}
$member = [string]$args[0]
$count = [string]$args[1]
$interval = [string]$args[2]
$rest = @($args | Select-Object -Skip 3 | ForEach-Object { [string]$_ })

if ($count -cnotmatch '^[1-9][0-9]*$') {
    [Console]::Error.WriteLine("count must be a whole number of samples, got '$count'")
    exit 4
}
if ($interval -cnotmatch '^[0-9]+([.][0-9]+)?$') {
    [Console]::Error.WriteLine("interval must be a number of seconds, got '$interval'")
    exit 4
}
$samples = [int]$count
$pause = [int][Math]::Round([double]::Parse($interval, [Globalization.CultureInfo]::InvariantCulture) * 1000)

$cli = Get-Setting 'VALHEIM_CLI' 'valheim-cli'
$port = Get-Setting 'VALHEIM_CLI_PORT' '5555'
if (-not (Get-Command $cli -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine("valheim-cli not found: '$cli' (set VALHEIM_CLI to its path)")
    exit 3
}

Write-Output 'sample,utc,value,error'
for ($i = 1; $i -le $samples; $i++) {
    $utc = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [Globalization.CultureInfo]::InvariantCulture)
    # Both streams, as the bash twin reads them: an ErrorRecord from stderr becomes its text.
    $reply = @(& $cli --port $port cli_call $member @rest 2>&1 | ForEach-Object { "$_" })
    $status = $LASTEXITCODE
    $value = ''
    $errorLine = ''
    if ($status -eq 0) {
        foreach ($line in $reply) {
            if ($line.StartsWith('VALUE ', [StringComparison]::Ordinal)) { $value = $line.Substring(6) }
            elseif ($line.StartsWith('OK: CALL ', [StringComparison]::Ordinal)) {
                if ($value -eq '' -and $line -cmatch ' items=([0-9?]+)') { $value = $Matches[1] }
            }
        }
    }
    else {
        if ($i -eq 1) {
            [Console]::Error.WriteLine("first sample failed (valheim-cli exit $status):")
            foreach ($line in $reply) { [Console]::Error.WriteLine($line) }
            exit 1
        }
        if ($reply.Count -gt 0) { $errorLine = $reply[0] }
    }
    Write-Output ("{0},{1},{2},{3}" -f $i, $utc, (ConvertTo-CsvField $value), (ConvertTo-CsvField $errorLine))
    if ($i -lt $samples) { Start-Sleep -Milliseconds $pause }
}
exit 0
