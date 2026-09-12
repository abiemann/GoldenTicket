#requires -Version 7.2
<#
.SYNOPSIS
Builds a private, self-contained Windows x64 ZIP from a clean, documented source commit.
.DESCRIPTION
Run only after documentation matches the final implementation and is committed with that source.
No installer, signing, certificate trust, firewall change, or release publication is performed.
Each run creates a new directory below artifacts/release and never deletes earlier output.
.PARAMETER CheckOnly
Checks source, documentation, platform, and SDK preconditions without restoring or publishing.
.PARAMETER OfflineBuild
Restores from the existing package cache and an empty local feed only. Missing packages fail.
NuGet vulnerability queries are disabled in this mode; record a separate connected audit.
#>
[CmdletBinding()]
param(
    [switch] $CheckOnly,
    [switch] $OfflineBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Assert-NoReparsePath([string] $Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        try {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Package source/output cannot use a symbolic link or directory junction: $current"
            }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
    }
}

function Assert-Within([string] $Path, [string] $Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent)) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The package path is outside its expected directory: $full"
    }
    Assert-NoReparsePath $full
    return $full
}

function Invoke-Text([string] $Program, [string[]] $Arguments) {
    $lines = @(& $Program @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE." }
    return ($lines -join "`n").Trim()
}

function Assert-CleanSource([string] $ExpectedCommit) {
    $status = Invoke-Text $script:git @('-C', $script:repository, 'status', '--porcelain=v1', '--untracked-files=all')
    if ($status) {
        throw "Packaging requires a clean committed source tree, including updated README, TODO, DESIGN and affected documentation. Commit the reviewed source first. Ignored artifacts are allowed.`n$status"
    }
    $head = Invoke-Text $script:git @('-C', $script:repository, 'rev-parse', '--verify', 'HEAD')
    if ($ExpectedCommit -and $head -ne $ExpectedCommit) {
        throw 'The source commit changed while packaging. Keep this incomplete output for diagnosis and run again from a stable source commit.'
    }
    return $head
}

function Invoke-Logged([string] $Program, [string[]] $Arguments, [string] $LogPath) {
    & $Program @Arguments *> $LogPath
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Get-Content -LiteralPath $LogPath -Tail 30 | Write-Host
        throw "The build command failed with exit code $code. See $LogPath. No complete package is reported."
    }
}

function Write-Json([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
}

function Copy-Confined([string] $Source, [string] $Destination, [string] $DestinationRoot) {
    Assert-NoReparsePath $Source
    $safeDestination = Assert-Within $Destination $DestinationRoot
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($safeDestination)) | Out-Null
    Assert-NoReparsePath $safeDestination
    [IO.File]::Copy($Source, $safeDestination, $false)
}

function Add-Package([string] $Id, [string] $Version, [string] $Reason) {
    if ($Id -notmatch '^[A-Za-z0-9_.-]+$' -or $Version -notmatch '^[A-Za-z0-9_.+-]+$') {
        throw "A resolved package has an invalid identity: $Id / $Version"
    }
    $key = ($Id + '/' + $Version).ToLowerInvariant()
    if (-not $script:packages.Contains($key)) {
        $script:packages[$key] = [ordered]@{ Id = $Id; Version = $Version; Reason = $Reason }
    } elseif ($Reason -eq 'included runtime') {
        $script:packages[$key].Reason = $Reason
    }
}

function Find-PackageDirectory([string] $Id, [string] $Version) {
    foreach ($folder in $script:packageFolders) {
        $path = Assert-Within (Join-Path $folder ($Id.ToLowerInvariant() + '/' + $Version.ToLowerInvariant())) $folder
        if (Test-Path -LiteralPath $path -PathType Container) { return $path }
    }
    throw "The exact resolved package is unavailable for notice collection: $Id $Version"
}

function Read-Nuspec([string] $Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    } finally { $reader.Dispose() }
}

function Child-Text($Node, [string] $Name) {
    $child = $Node.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $child) { return '' }
    return $child.InnerText
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Assert-NoReparsePath $repository
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Build this package on Windows x64. The application targets Windows 11 x64.'
}
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$actualRoot = Invoke-Text $git @('-C', $repository, 'rev-parse', '--show-toplevel')
if ([IO.Path]::GetFullPath($actualRoot) -ne $repository) { throw 'The script must belong to the root GoldenTicket repository.' }
$sourceCommit = Assert-CleanSource ''
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'A complete Git source commit is required.' }
$requiredDocumentation = @('README.md', 'TODO.md', 'DESIGN.md', 'docs/offline-package.md', 'docs/phone-setup.md')
$requiredPackageLocks = @('GoldenTicket.Domain', 'GoldenTicket.Application', 'GoldenTicket.AI',
    'GoldenTicket.Persistence', 'GoldenTicket.CompanionHost', 'GoldenTicket.Vision', 'GoldenTicket.Desktop') |
    ForEach-Object { 'src/' + $_ + '/packages.win-x64.lock.json' }
foreach ($relative in $requiredDocumentation + $requiredPackageLocks +
    @('tools/Build-OfflinePackage.ps1', 'global.json', 'NuGet.config', 'Directory.Build.props', 'Directory.Packages.props')) {
    $path = Assert-Within (Join-Path $repository $relative) $repository
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required source documentation/configuration is missing: $relative" }
    $null = Invoke-Text $git @('-C', $repository, 'ls-files', '--error-unmatch', '--', $relative)
}
Push-Location -LiteralPath $repository
try {
    $sdkVersion = Invoke-Text $dotnet @('--version')
    $pinnedSdk = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json -AsHashtable).sdk.version
    if ($sdkVersion -ne $pinnedSdk) {
        throw "Packaging requires the exact SDK recorded by global.json ($pinnedSdk), but dotnet selected $sdkVersion. Install that free SDK or deliberately update, validate and commit global.json first."
    }
    if ($CheckOnly) {
        Write-Host "Preflight passed. Source: $sourceCommit; SDK: $sdkVersion. No restore, publish, archive, or Windows configuration change was performed."
        return
    }

    $started = [DateTimeOffset]::UtcNow
    $name = 'GoldenTicket-win-x64-' + $started.ToString('yyyyMMdd-HHmmss') + '-' + $sourceCommit.Substring(0, 8) + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $releaseRoot = Assert-Within (Join-Path $repository 'artifacts/release') $repository
    $runRoot = Assert-Within (Join-Path $releaseRoot $name) $releaseRoot
    if (Test-Path -LiteralPath $runRoot) { throw 'The generated output directory already exists; no output will be replaced.' }
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    $packageRoot = Assert-Within (Join-Path $runRoot 'GoldenTicket') $runRoot
    [IO.Directory]::CreateDirectory($packageRoot) | Out-Null
    $project = Join-Path $repository 'src/GoldenTicket.Desktop/GoldenTicket.Desktop.csproj'
    $restoreArguments = @('restore', $project, '--locked-mode', '--configfile', (Join-Path $repository 'NuGet.config'),
        '--runtime', 'win-x64', '-p:SelfContained=true', '-p:GoldenTicketOfflinePackage=true')
    if ($OfflineBuild) {
        $emptyFeed = Assert-Within (Join-Path $runRoot 'empty-local-feed') $runRoot
        [IO.Directory]::CreateDirectory($emptyFeed) | Out-Null
        $restoreArguments += @('--source', $emptyFeed, '-p:NuGetAudit=false')
    }
    Write-Host "Restoring locked dependencies for source $sourceCommit..."
    Invoke-Logged $dotnet $restoreArguments (Join-Path $runRoot 'restore.log')
    $null = Assert-CleanSource $sourceCommit
    $publishArguments = @('publish', $project, '--configuration', 'Release', '--runtime', 'win-x64',
        '--self-contained', 'true', '--no-restore', '--output', $packageRoot,
        '-p:GoldenTicketOfflinePackage=true',
        '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:PublishReadyToRun=false',
        '-p:ContinuousIntegrationBuild=true')
    Write-Host 'Publishing the application with its .NET, WPF and ASP.NET runtimes...'
    Invoke-Logged $dotnet $publishArguments (Join-Path $runRoot 'publish.log')
    $null = Assert-CleanSource $sourceCommit

    $requiredFiles = @('GoldenTicket.exe', 'GoldenTicket.dll', 'GoldenTicket.runtimeconfig.json', 'GoldenTicket.deps.json',
        'GoldenTicket.CompanionHost.dll', 'GoldenTicket.Persistence.dll', 'GoldenTicket.Vision.dll',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'clrjit.dll', 'System.Private.CoreLib.dll',
        'PresentationFramework.dll', 'WindowsBase.dll', 'Microsoft.AspNetCore.Server.Kestrel.Core.dll',
        'e_sqlite3.dll', 'WinRT.Runtime.dll', 'Microsoft.Windows.SDK.NET.dll', 'data/classic-us/classic-us-v1.json',
        'companion-web/index.html', 'companion-web/app.js', 'companion-web/app.css', 'companion-web/sw.js',
        'companion-web/manifest.webmanifest', 'companion-web/icon-192.png', 'companion-web/icon-512.png')
    foreach ($relative in $requiredFiles) {
        $path = Assert-Within (Join-Path $packageRoot $relative) $packageRoot
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "The self-contained package is incomplete: $relative"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force) {
        Assert-NoReparsePath $file.FullName
        if ($file.Name -match '(?i)(\.pfx|\.p12|\.pem|\.key|\.crt|\.cer|\.db(?:-wal|-shm)?|\.gtphoto)$|^\.env(?:\.|$)') {
            throw "A local secret, trust certificate, or game save must not ship in the package: $($file.Name)"
        }
    }
    # Only the known tracked static web assets may enter the companion bundle. An ignored local
    # file under wwwroot must not hitchhike on MSBuild's recursive content glob.
    $webFiles = @(Invoke-Text $git @('-C', $repository, 'ls-files', '--', 'src/GoldenTicket.CompanionHost/wwwroot')) -split "`n"
    $expectedWebFiles = @($webFiles | Where-Object { $_ } | ForEach-Object { $_.Substring('src/GoldenTicket.CompanionHost/wwwroot/'.Length) })
    foreach ($relative in $expectedWebFiles) {
        $sourcePath = Join-Path $repository ('src/GoldenTicket.CompanionHost/wwwroot/' + $relative)
        $publishedPath = Join-Path $packageRoot ('companion-web/' + $relative)
        if (-not (Test-Path -LiteralPath $publishedPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash) {
            throw "A companion asset is missing or differs from the committed source: $relative"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $packageRoot 'companion-web') -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $packageRoot 'companion-web'), $file.FullName).Replace('\', '/')
        if ($relative -notin $expectedWebFiles) { throw "An untracked companion asset was published: $relative" }
    }

    $runtimeConfiguration = Get-Content -LiteralPath (Join-Path $packageRoot 'GoldenTicket.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtimeOptions = $runtimeConfiguration.runtimeOptions
    if ($runtimeOptions.ContainsKey('framework') -or $runtimeOptions.ContainsKey('frameworks') -or -not $runtimeOptions.ContainsKey('includedFrameworks')) {
        throw 'The generated runtime configuration is framework-dependent, not self-contained.'
    }
    $includedFrameworks = @($runtimeOptions.includedFrameworks)
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App')) {
        if ($framework -notin $includedFrameworks.name) { throw "The package did not include the $framework runtime." }
    }

    Write-Host 'Checking the published executable and its loaded runtimes without opening a window...'
    $diagnosticReport = Join-Path $runRoot 'runtime-diagnostic.json'
    $diagnosticStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $packageRoot 'GoldenTicket.exe'))
    $diagnosticStart.UseShellExecute = $false
    $diagnosticStart.CreateNoWindow = $true
    $diagnosticStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $diagnosticStart.WorkingDirectory = $runRoot
    $diagnosticStart.ArgumentList.Add('--check-package')
    $diagnosticStart.ArgumentList.Add($diagnosticReport)
    $diagnosticProcess = [Diagnostics.Process]::Start($diagnosticStart)
    try {
        if (-not $diagnosticProcess.WaitForExit(45000)) {
            $diagnosticProcess.Kill($true)
            throw 'The package runtime check timed out. No complete package is reported.'
        }
        if ($diagnosticProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $diagnosticReport -PathType Leaf)) {
            throw "The published runtime check failed (exit $($diagnosticProcess.ExitCode)). Inspect $diagnosticReport; no complete package is reported."
        }
    } finally { $diagnosticProcess.Dispose() }
    $diagnostic = Get-Content -LiteralPath $diagnosticReport -Raw | ConvertFrom-Json -AsHashtable
    if (-not $diagnostic.passed -or $diagnostic.checks.Count -ne 8) {
        throw 'The published executable did not pass all eight component checks.'
    }

    $documentation = @(Invoke-Text $git @('-C', $repository, 'ls-files', '--', 'README.md', 'TODO.md', 'DESIGN.md', 'docs')) -split "`n"
    foreach ($relative in $documentation | Where-Object { $_ }) {
        Copy-Confined (Join-Path $repository $relative) (Join-Path $packageRoot $relative) $packageRoot
    }
    $projectNotices = @(Invoke-Text $git @('-C', $repository, 'ls-files', '--', 'LICENSE*', 'NOTICE*', 'COPYING*', 'THIRD-PARTY*')) -split "`n"
    foreach ($relative in $projectNotices | Where-Object { $_ }) {
        Copy-Confined (Join-Path $repository $relative) (Join-Path $packageRoot $relative) $packageRoot
    }

    $assets = Get-Content -LiteralPath (Join-Path $repository 'src/GoldenTicket.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $packageFolders = @($assets.packageFolders.Keys)
    $packages = [ordered]@{}
    foreach ($entry in $assets.libraries.GetEnumerator()) {
        if ($entry.Value.type -eq 'package') {
            $identity = $entry.Key.Split('/')
            Add-Package $identity[0] $identity[1] 'application dependency'
        }
    }
    foreach ($framework in $assets.project.frameworks.Values) {
        if (-not $framework.ContainsKey('downloadDependencies')) { continue }
        foreach ($dependency in $framework.downloadDependencies) {
            if ($dependency.version -notmatch '^\[([^,\]]+),\s*([^\]]+)\]$' -or $Matches[1] -ne $Matches[2]) {
                throw "A downloaded SDK/runtime dependency is not pinned exactly: $($dependency.name) $($dependency.version)"
            }
            Add-Package $dependency.name $Matches[1] 'SDK/runtime dependency'
        }
    }
    foreach ($framework in $includedFrameworks) {
        Add-Package ($framework.name + '.Runtime.win-x64') $framework.version 'included runtime'
    }

    $noticeRows = [Collections.Generic.List[object]]::new()
    $needsNoticeReview = [Collections.Generic.List[string]]::new()
    foreach ($package in $packages.Values) {
        $directory = Find-PackageDirectory $package.Id $package.Version
        $specifications = @(Get-ChildItem -LiteralPath $directory -File -Filter '*.nuspec')
        if ($specifications.Count -ne 1) { throw "Expected one package specification for $($package.Id) $($package.Version)." }
        $metadata = Read-Nuspec $specifications[0].FullName
        if ($null -eq $metadata) { throw "The package specification has no metadata: $($package.Id)" }
        $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
        $license = if ($null -eq $licenseNode) { '' } else { $licenseNode.InnerText }
        $licenseType = if ($null -eq $licenseNode) { '' } else { $licenseNode.GetAttribute('type') }
        $noticeDirectory = Join-Path $packageRoot ('licenses/packages/' + $package.Id + '/' + $package.Version)
        Copy-Confined $specifications[0].FullName (Join-Path $noticeDirectory $specifications[0].Name) $packageRoot
        $copied = [Collections.Generic.List[string]]::new()
        foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse) {
            if ($file.Name -notmatch '(?i)license|licence|notice|copying|copyright' -or
                $file.Extension -notin @('', '.txt', '.md', '.rst', '.html', '.htm')) { continue }
            $relative = [IO.Path]::GetRelativePath($directory, $file.FullName)
            Copy-Confined $file.FullName (Join-Path $noticeDirectory $relative) $packageRoot
            $copied.Add($relative.Replace('\', '/'))
        }
        if ($licenseType -eq 'file') {
            $licenseSource = Assert-Within (Join-Path $directory $license) $directory
            if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) { throw "Declared package license file is missing: $($package.Id) / $license" }
            if ($license.Replace('\', '/') -notin $copied) {
                Copy-Confined $licenseSource (Join-Path $noticeDirectory $license) $packageRoot
                $copied.Add($license.Replace('\', '/'))
            }
        }
        if ($copied.Count -eq 0) {
            $needsNoticeReview.Add($package.Id + '/' + $package.Version)
            if ($package.Reason -eq 'included runtime') { throw "The included runtime lacks its packaged license text: $($package.Id)" }
        }
        $noticeRows.Add([ordered]@{
            id = $package.Id; version = $package.Version; reason = $package.Reason
            authors = (Child-Text $metadata 'authors'); copyright = (Child-Text $metadata 'copyright')
            licenseType = $licenseType; license = $license; licenseUrl = (Child-Text $metadata 'licenseUrl')
            retainedFiles = @($specifications[0].Name) + @($copied)
            licenseTextInPackage = $copied.Count -gt 0
        })
    }
    Write-Json (Join-Path $packageRoot 'licenses/dependencies.json') $noticeRows.ToArray()
    $noticeText = @"
# Third-party notices

This package retains the exact resolved NuGet package specifications, packaged license files,
and notices under licenses/packages, including its .NET, WPF, and ASP.NET runtime packs.
The dependency inventory is licenses/dependencies.json. Build-only SDK dependencies may also be listed.
The application source commit is recorded in package-provenance.json.

Some upstream packages supply a license expression or URL without including the complete license
text. Their original metadata is retained; notice completeness must be reviewed before wider
distribution. This generated inventory is not a completed licensing audit.

Packages needing that notice-text review: $($needsNoticeReview -join ', ')
"@
    [IO.File]::WriteAllText((Join-Path $packageRoot 'PACKAGE-THIRD-PARTY-NOTICES.md'), $noticeText, [Text.UTF8Encoding]::new($false))
    $provenance = [ordered]@{
        formatVersion = 1; sourceCommit = $sourceCommit; sdkVersion = $sdkVersion
        builtAtUtc = $started.ToString('O'); target = 'Windows 11 x64'; runtimeIdentifier = 'win-x64'
        selfContained = $true; trimmed = $false; singleFile = $false; readyToRun = $false
        frameworkVersions = $includedFrameworks; lockedRestore = $true
        dependencyLockSet = 'packages.win-x64.lock.json'; offlineBuild = [bool]$OfflineBuild
        nugetAuditRequestedDuringRestore = -not [bool]$OfflineBuild; packageType = 'portable ZIP'; signed = $false
        documentationCommittedWithSource = $true
        headlessRuntimeChecksPassed = $true
        requiredManualAcceptance = @('Clean Windows 11 x64 without an installed .NET runtime',
            'Internet-disconnected launch and complete manual game', 'Camera permissions and reconnect',
            'Trusted Private LAN and phone certificate/pairing setup', 'Photo save/restart/rebuild',
            'Notice-text completeness review before wider distribution')
        noticeTextReview = @($needsNoticeReview)
    }
    Write-Json (Join-Path $packageRoot 'package-provenance.json') $provenance
    $null = Assert-CleanSource $sourceCommit

    $hashRows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force | Sort-Object FullName) {
        Assert-NoReparsePath $file.FullName
        $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
        $hashRows.Add([ordered]@{ path = $relative; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    # The manifest covers every payload file except itself. The detached ZIP hash covers it too.
    Write-Json (Join-Path $packageRoot 'SHA256-MANIFEST.json') ([ordered]@{ formatVersion = 1; sourceCommit = $sourceCommit; files = $hashRows.ToArray() })
    $zip = Assert-Within (Join-Path $runRoot ($name + '.zip')) $runRoot
    [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip + '.sha256', $zipHash + '  ' + [IO.Path]::GetFileName($zip) + "`n", [Text.UTF8Encoding]::new($false))
    $null = Assert-CleanSource $sourceCommit
    Write-Json (Join-Path $runRoot 'package-result.json') ([ordered]@{
        sourceCommit = $sourceCommit; completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        zipFile = [IO.Path]::GetFileName($zip); sha256 = $zipHash; bytes = (Get-Item -LiteralPath $zip).Length
        payloadFiles = $hashRows.Count + 1; manualAcceptance = 'pending'
    })
    Write-Host "Portable package created: $zip"
    Write-Host "SHA256: $zipHash"
    Write-Host 'Clean-machine, physical-camera, phone, and complete-match acceptance remain manual gates. No installer or release was published.'
} finally { Pop-Location }
