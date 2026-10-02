# Check NuGet's caches and the SDK's file-based app state before dotnet run restores a file-based script.
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

# The SDK builds a file-based script under a per-user directory that no setting moves (#210): the temporary directory
# on Windows, LocalApplicationData elsewhere. It creates one owner-only directory per script there.
function Test-DirectoryCreate([string]$directory) {
    try {
        if ($IsWindows) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
        else { [IO.Directory]::CreateDirectory($directory, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute') | Out-Null }
        $probe = Join-Path $directory ('.valheimtesting-write-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($probe) | Out-Null
        [IO.Directory]::Delete($probe)
        return $true
    }
    catch [IO.IOException], [UnauthorizedAccessException], [Security.SecurityException] { return $false }
}

# Report it without the home directory, which on Windows the temporary path can hold in its short form.
if ($IsWindows) {
    $runfile = Join-Path ([IO.Path]::GetTempPath()) 'dotnet' 'runfile'
    $shown = '%TEMP%\dotnet\runfile'
}
else {
    $runfile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'dotnet' 'runfile'
    $shown = if ($runfile.StartsWith($HOME.TrimEnd('/') + '/')) { '~' + $runfile.Substring($HOME.TrimEnd('/').Length) } else { $runfile }
}
Push-Location $root
try {
    if (Test-DirectoryCreate $runfile) {
        Write-Host "File-based app state writable: $shown"
        & dotnet restore "scripts/$script" --force
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        & dotnet run "scripts/$script" --no-restore -- @TaskArgs
        exit $LASTEXITCODE
    }

    # Run the script as the project the SDK converts it to: that builds in the workspace instead. The project is replaced
    # only when the conversion changes, so its build is reused across runs.
    $name = [IO.Path]::GetFileNameWithoutExtension($script)
    $project = "artifacts/runfile/$name"
    Write-Host "File-based app state not writable: $shown; running scripts/$script as the project $project"
    New-Item -ItemType Directory -Force $project | Out-Null
    $converted = "$project.new-" + [Guid]::NewGuid().ToString('N')
    try {
        & dotnet project convert "scripts/$script" --output $converted | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not convert scripts/$script to a project. Grant write access to $shown; no SDK setting moves it." }
        foreach ($file in Get-ChildItem $project -File) {
            if (-not (Test-Path (Join-Path $converted $file.Name))) { Remove-Item $file.FullName }
        }
        foreach ($file in Get-ChildItem $converted -File) {
            $target = Join-Path $project $file.Name
            if (-not (Test-Path $target) -or (Get-FileHash $file.FullName).Hash -ne (Get-FileHash $target).Hash) {
                Copy-Item $file.FullName $target -Force
            }
        }
    }
    finally { if (Test-Path $converted) { Remove-Item -Recurse -Force $converted } }
    & dotnet restore "$project/$name.csproj" --force
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet run --project "$project/$name.csproj" --no-restore -- @TaskArgs
    exit $LASTEXITCODE
}
finally { Pop-Location }
