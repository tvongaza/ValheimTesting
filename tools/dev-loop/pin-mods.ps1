# Pin the mod builds a game runs, then refuse to work on a game that drifted.
#
#   pin-mods.ps1 snapshot FILE [--with-world]   write FILE from the running game
#   pin-mods.ps1 check FILE [--strict]          strict by default; exit 0 if the game matches FILE,
#                                               6 with the mismatches if not
#   pin-mods.ps1 run FILE COMMAND...            run a console command only if the
#                                               game matches FILE
#
# PowerShell twin of pin-mods.sh, for Windows PowerShell 5.1 and PowerShell 7.
# Keep the two in step: same subcommands, environment and exit codes. Run it with
#
#   powershell -ExecutionPolicy Bypass -File examples\pin-mods.ps1 check pins.txt
#
# snapshot records every loaded BepInEx plugin by GUID and md5 (and with
# --with-world the loaded world's name, uid and seed). The file is plain text:
# edit a line to `any` when that plugin's build does not matter, or add
# `name=absent` for a plugin that must not be loaded. See docs/expectations.md.
#
# Typical use: snapshot once from a known-good setup, commit the file next to
# your tests, and check it at the start of every run:
#
#   pin-mods.ps1 snapshot pins.txt                         # once
#   valheim-cli --expect-strict pins.txt --test plan.yaml  # every run: exit 6,
#                                                          # no step run, on drift
#
# A test script that is not a plan starts with `pin-mods.ps1 check FILE --strict`.
# To make the game itself refuse drifted runs, point [Expectations] File in
# BepInEx\config\valheimCLI.valheimCLI.cfg at the same file.
#
# Environment:
#   VALHEIM_CLI       path to the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  the game's CLI port (default 5555)
#
# The game must be running with valheimCLI loaded. For a game on another
# machine, forward its port first: ssh -N -L 5555:127.0.0.1:5555 host

function Get-Setting([string]$Name, [string]$Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    return $value
}

function Exit-Usage {
    [Console]::Error.WriteLine(@'
pin-mods.ps1 snapshot FILE [--with-world]   write FILE from the running game
pin-mods.ps1 check FILE [--strict]          strict by default; exit 0 if the game matches FILE,
                                            6 with the mismatches if not
pin-mods.ps1 run FILE COMMAND...            run a console command only if the
                                            game matches FILE
'@)
    exit 4
}

# A missing valheim-cli stops the script with exit 1.
trap {
    [Console]::Error.WriteLine("ERROR: $_")
    exit 1
}

$cli = Get-Setting 'VALHEIM_CLI' 'valheim-cli'
$port = Get-Setting 'VALHEIM_CLI_PORT' '5555'

if ($args.Count -lt 2) { Exit-Usage }
$action = [string]$args[0]
$file = [string]$args[1]
$rest = @($args | Select-Object -Skip 2)

switch -CaseSensitive ($action) {
    'snapshot' {
        $worldFlag = @()
        if ($rest.Count -ge 1 -and $rest[0] -ceq '--with-world') { $worldFlag = @('--with-world') }
        & $cli --port $port manifest --write $file @worldFlag
    }
    'check' {
        if (-not ($rest.Count -eq 0 -or ($rest.Count -eq 1 -and $rest[0] -ceq '--strict'))) { Exit-Usage }
        & $cli --port $port --expect-strict $file
    }
    'run' {
        if ($rest.Count -lt 1) { Exit-Usage }
        & $cli --port $port --expect-strict $file @rest
    }
    default {
        Exit-Usage
    }
}
exit $LASTEXITCODE
