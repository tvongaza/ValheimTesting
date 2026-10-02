# Check NuGet's caches before dotnet run restores a file-based script.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('bootstrap', 'validate')]
    [string]$Task,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$TaskArgs
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script = if ($Task -eq 'bootstrap') { 'bootstrap-cli.cs' } else { 'validate.cs' }

function Get-CachePath([string]$variable, [string]$kind) {
    $explicit = [Environment]::GetEnvironmentVariable($variable)
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return [IO.Path]::GetFullPath($explicit, $root) }
    $output = & dotnet nuget locals $kind --list
    if ($LASTEXITCODE -ne 0 -or $output -notmatch '^[^:]+:\s*(.+)$') {
        throw "Could not query NuGet $kind cache path: $output"
    }
    return $Matches[1].Trim()
}

function Test-CacheWrite([string]$directory) {
    try {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $probe = Join-Path $directory ('.valheimtesting-write-' + [Guid]::NewGuid().ToString('N'))
        $file = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $file.WriteByte(1) } finally { $file.Dispose(); [IO.File]::Delete($probe) }
        return $true
    }
    catch [IO.IOException], [UnauthorizedAccessException], [Security.SecurityException] { return $false }
}

$packages = Get-CachePath 'NUGET_PACKAGES' 'global-packages'
$http = Get-CachePath 'NUGET_HTTP_CACHE_PATH' 'http-cache'
if ((Test-CacheWrite $packages) -and (Test-CacheWrite $http)) {
    Write-Host "NuGet caches writable before dotnet run: packages=$packages; HTTP=$http"
}
else {
    $bytes = [Text.Encoding]::UTF8.GetBytes($root)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 12).ToLowerInvariant()
    $fallback = Join-Path ([IO.Path]::GetTempPath()) "valheimtesting-nuget-$hash"
    $packages = Join-Path $fallback 'packages'
    $http = Join-Path $fallback 'http-cache'
    if (-not (Test-CacheWrite $packages) -or -not (Test-CacheWrite $http)) {
        throw 'NuGet caches and fallback are not writable; set NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH to writable directories.'
    }
    Write-Host "NuGet caches blocked before dotnet run; using packages=$packages; HTTP=$http"
}
$env:NUGET_PACKAGES = $packages
$env:NUGET_HTTP_CACHE_PATH = $http
Push-Location $root
try {
    & dotnet restore "scripts/$script" --force
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet run "scripts/$script" --no-restore -- @TaskArgs
    exit $LASTEXITCODE
}
finally { Pop-Location }
