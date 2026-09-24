#requires -Version 7.2
<#
.SYNOPSIS
Wraps a verified, self-contained GoldenTicket package in a per-user Windows installer.
.DESCRIPTION
Run after Build-OfflinePackage.ps1 has completed from the same clean source commit.
Inno Setup 7.1.0 is a build dependency only; it is not shipped with GoldenTicket.
The installer leaves the application's separate LocalAppData save directory alone.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $PackageResultPath,

    [string] $InnoCompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Read-Json([string] $Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
}

function Assert-ChildPath([string] $Path, [string] $Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent)) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the release directory: $full"
    }
    return $full
}

function Write-Json([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20) + "`n", [Text.UTF8Encoding]::new($false))
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $repository 'artifacts/release'
$resultPath = Assert-ChildPath $PackageResultPath $releaseRoot
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf) -or [IO.Path]::GetFileName($resultPath) -ne 'package-result.json') {
    throw 'PackageResultPath must name a completed package-result.json under artifacts/release.'
}
$runRoot = [IO.Path]::GetDirectoryName($resultPath)
$packageRoot = Join-Path $runRoot 'GoldenTicket'
$scriptPath = Join-Path $repository 'tools/GoldenTicket.Installer.iss'

$status = @(& git -C $repository status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) {
    throw "Installer packaging requires a clean, committed source tree. $($status -join '; ')"
}
$sourceCommit = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Unable to identify the source commit.' }

$result = Read-Json $resultPath
$provenance = Read-Json (Join-Path $packageRoot 'package-provenance.json')
$manifest = Read-Json (Join-Path $packageRoot 'SHA256-MANIFEST.json')
if ($result.sourceCommit -ne $sourceCommit -or $provenance.sourceCommit -ne $sourceCommit -or
    $manifest.sourceCommit -ne $sourceCommit -or -not $provenance.selfContained -or
    -not $provenance.headlessRuntimeChecksPassed) {
    throw 'The package does not match this committed, verified self-contained source.'
}
$zip = Assert-ChildPath (Join-Path $runRoot $result.zipFile) $runRoot
if (-not (Test-Path -LiteralPath $zip -PathType Leaf) -or
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $result.sha256) {
    throw 'The package ZIP no longer matches package-result.json.'
}
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.GetEntry('GoldenTicket/SHA256-MANIFEST.json')
    if ($null -eq $entry) { throw 'The verified ZIP has no package manifest.' }
    $stream = $entry.Open()
    $buffer = [IO.MemoryStream]::new()
    try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
    $archiveManifestHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buffer.ToArray()))
    $buffer.Dispose()
} finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath (Join-Path $packageRoot 'SHA256-MANIFEST.json') -Algorithm SHA256).Hash -ne $archiveManifestHash) {
    throw 'The expanded payload manifest differs from the verified ZIP.'
}

$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ($entry.path -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($entry.path)) {
        throw "Unsafe package manifest path: $($entry.path)"
    }
    $file = Assert-ChildPath (Join-Path $packageRoot $entry.path) $packageRoot
    if (-not $expected.Add($file) -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Missing or duplicate package file: $($entry.path)"
    }
    $item = Get-Item -LiteralPath $file
    if ($item.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "The packaged file changed after verification: $($entry.path)"
    }
}
$null = $expected.Add((Join-Path $packageRoot 'SHA256-MANIFEST.json'))
foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force) {
    if (-not $expected.Contains($file.FullName)) { throw "Unexpected file in the installer payload: $($file.FullName)" }
}

$project = Get-Content -LiteralPath (Join-Path $repository 'src/GoldenTicket.Desktop/GoldenTicket.Desktop.csproj') -Raw
if ($project -notmatch "<Version>$([Regex]::Escape($Version))</Version>") {
    throw "The desktop application's committed version does not match installer version $Version."
}

if (-not $InnoCompilerPath) {
    $candidate = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($candidate) { $InnoCompilerPath = $candidate.Source }
}
if (-not $InnoCompilerPath -or -not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) {
    throw 'Install the verified Inno Setup 7.1.0 compiler and pass -InnoCompilerPath.'
}
$compiler = (Resolve-Path -LiteralPath $InnoCompilerPath).Path
$compilerVersion = (& $compiler --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -ne '7.1.0') {
    throw "Expected Inno Setup 7.1.0, found $compilerVersion."
}

$installer = Join-Path $runRoot "GoldenTicket-Setup-$Version-win-x64.exe"
if (Test-Path -LiteralPath $installer) { throw "Installer output already exists: $installer" }
$compileLog = Join-Path $runRoot 'installer-compile.log'
& $compiler "-dPackageDir=$packageRoot" "-dReleaseVersion=$Version" "-o$runRoot" $scriptPath *> $compileLog
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    Get-Content -LiteralPath $compileLog -Tail 30 | Write-Host
    throw "Inno Setup compilation failed. See $compileLog."
}
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($installer + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($installer) + "`n", [Text.UTF8Encoding]::new($false))
Write-Json (Join-Path $runRoot 'installer-result.json') ([ordered]@{
    version = $Version; sourceCommit = $sourceCommit; packageSha256 = $result.sha256
    compilerVersion = $compilerVersion; installerFile = [IO.Path]::GetFileName($installer)
    sha256 = $hash; bytes = (Get-Item -LiteralPath $installer).Length
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); signed = $false
    cleanMachineAcceptance = 'pending'; noticeTextReview = @($provenance.noticeTextReview)
})
Write-Host "Windows installer created: $installer"
Write-Host "SHA256: $hash"
