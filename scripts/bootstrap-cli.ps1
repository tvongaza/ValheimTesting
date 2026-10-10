# Set writable NuGet caches before dotnet restores the file-based bootstrap app.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Get-DefaultCache([string] $kind) {
    $line = & dotnet nuget locals $kind --list
    if ($LASTEXITCODE -ne 0 -or $line -notmatch '^[^:]+:\s*(.+)$') {
        throw "Could not locate the NuGet $kind cache"
    }
    return $Matches[1]
}

function Test-Writable([string] $path) {
    try {
        [System.IO.Directory]::CreateDirectory($path) | Out-Null
        $probe = Join-Path $path ('.valheimtesting-write-' + [guid]::NewGuid().ToString('N'))
        $stream = [System.IO.File]::Open($probe, [System.IO.FileMode]::CreateNew)
        try { $stream.Dispose() } finally { [System.IO.File]::Delete($probe) }
        return $true
    } catch { return $false }
}

function Select-Cache([string] $name, [string] $candidate, [string] $fallback) {
    if (Test-Writable $candidate) { return $candidate }
    if (Test-Writable $fallback) { return $fallback }
    throw "Cannot write either $name cache ($candidate) or fallback ($fallback)"
}

$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Get-DefaultCache 'global-packages' }
$http = if ($env:NUGET_HTTP_CACHE_PATH) { $env:NUGET_HTTP_CACHE_PATH } else { Get-DefaultCache 'http-cache' }
$env:NUGET_PACKAGES = Select-Cache 'packages' $packages (Join-Path $root 'artifacts/nuget-bootstrap/packages')
$env:NUGET_HTTP_CACHE_PATH = Select-Cache 'http' $http (Join-Path $root 'artifacts/nuget-bootstrap/http')
Write-Output "NuGet packages: $env:NUGET_PACKAGES"
Write-Output "NuGet HTTP cache: $env:NUGET_HTTP_CACHE_PATH"

if ($args.Count -eq 1 -and $args[0] -eq '--check-caches') { exit 0 }
Push-Location $root
try {
    & dotnet run scripts/bootstrap-cli.cs -- @args
    exit $LASTEXITCODE
} finally { Pop-Location }
