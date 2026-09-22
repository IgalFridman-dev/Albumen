[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SdkRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagesRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceIdentity,

    [Parameter()]
    [switch]$CaptureOnly,

    [Parameter()]
    [hashtable]$Operations
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sdkVersion = '10.0.400'
$runtimeVersion = '10.0.11'
$runtimeIdentifier = 'win-x64'
$resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$resolvedSdkRoot = [System.IO.Path]::GetFullPath($SdkRoot)
$resolvedPackagesRoot = [System.IO.Path]::GetFullPath($PackagesRoot)
$projectPath = Join-Path $resolvedRepositoryRoot 'src\PhotoSift.App\PhotoSift.App.csproj'
$dotnetPath = Join-Path $resolvedSdkRoot 'dotnet.exe'
$evidencePath = Join-Path $resolvedOutputRoot 'evidence'
$rawPublishPaths = @(
    (Join-Path $resolvedOutputRoot 'publish-1-raw'),
    (Join-Path $resolvedOutputRoot 'publish-2-raw'))
$packagePaths = @(
    (Join-Path $resolvedOutputRoot 'publish-1-package'),
    (Join-Path $resolvedOutputRoot 'publish-2-package'))
$candidatePath = Join-Path $resolvedOutputRoot 'candidate'

function Write-ReleaseUtf8NoBom {
    param([string]$LiteralPath, [string]$Content)

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($LiteralPath, $Content, $encoding)
}

function Invoke-ReleaseExternal {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    if ($null -ne $Operations) {
        if (-not $Operations.ContainsKey('InvokeExternal')) {
            throw 'Injected release operations must provide InvokeExternal.'
        }
        $result = & $Operations.InvokeExternal `
            -Name $Name `
            -FilePath $FilePath `
            -Arguments $Arguments `
            -WorkingDirectory $WorkingDirectory
        if ($null -eq $result -or [int]$result.ExitCode -ne 0) {
            throw "Release command '$Name' failed through the injected operation."
        }
        return $result
    }

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($exitCode -ne 0) {
        throw "Release command '$Name' exited $exitCode`: $($output -join [System.Environment]::NewLine)"
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output -join [System.Environment]::NewLine
    }
}

function Get-ReleaseInventory {
    param([string]$RootPath)

    $resolvedRootPath = [System.IO.Path]::GetFullPath($RootPath)
    $prefix = $resolvedRootPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    return @(
        Get-ChildItem -Force -File -Recurse -LiteralPath $resolvedRootPath |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    path = $_.FullName.Substring($prefix.Length).Replace('\', '/')
                    length = [long]$_.Length
                    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
                }
            } |
            Sort-Object path)
}

function Assert-ReleaseInventoriesEqual {
    param($First, $Second, [string]$Context)

    $firstJson = $First | ConvertTo-Json -Depth 5 -Compress
    $secondJson = $Second | ConvertTo-Json -Depth 5 -Compress
    if ($firstJson -cne $secondJson) {
        $firstPaths = @($First | ForEach-Object { "$($_.path)|$($_.length)|$($_.sha256)" })
        $secondPaths = @($Second | ForEach-Object { "$($_.path)|$($_.length)|$($_.sha256)" })
        $difference = @(Compare-Object -CaseSensitive $firstPaths $secondPaths |
            ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
        throw "$Context inventories differ by canonical path, length, or SHA-256: $($difference -join '; ')"
    }
}

function Copy-ReleaseDirectoryContents {
    param([string]$SourcePath, [string]$DestinationPath)

    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    foreach ($item in @(Get-ChildItem -Force -LiteralPath $SourcePath)) {
        Copy-Item -Force -Recurse -LiteralPath $item.FullName -Destination $DestinationPath
    }
}

function Read-RuntimePackList {
    param([string]$PackageId, [string]$ExpectedFrameworkName)

    $packagePath = Join-Path $resolvedPackagesRoot ($PackageId + '\' + $runtimeVersion)
    $runtimeListPath = Join-Path $packagePath 'data\RuntimeList.xml'
    if (-not [System.IO.File]::Exists($runtimeListPath)) {
        throw "Pinned runtime pack list does not exist: $runtimeListPath"
    }
    [xml]$runtimeList = Get-Content -Raw -LiteralPath $runtimeListPath
    if ([string]$runtimeList.FileList.Name -cne $ExpectedFrameworkName -or
        [string]$runtimeList.FileList.RuntimeIdentifier -cne $runtimeIdentifier) {
        throw "Runtime pack list does not match $ExpectedFrameworkName $runtimeIdentifier $runtimeVersion`: $runtimeListPath"
    }

    return @($runtimeList.FileList.File | ForEach-Object {
            $path = ([string]$_.Path).Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($path) -or
                [System.IO.Path]::IsPathRooted($path) -or
                $path.Contains('..')) {
                throw "Runtime pack contains a noncanonical path: $path"
            }
            [pscustomobject]@{
                Path = $path
                Type = [string]$_.Type
                Framework = $ExpectedFrameworkName
            }
        })
}

function Get-ReleaseFileVersion {
    param([string]$LiteralPath)

    if ($null -ne $Operations -and $Operations.ContainsKey('GetFileVersion')) {
        return & $Operations.GetFileVersion -LiteralPath $LiteralPath
    }
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($LiteralPath)
    return [pscustomobject]@{
        FileVersion = [string]$version.FileVersion
        ProductVersion = [string]$version.ProductVersion
    }
}

function Get-NuspecElementText {
    param($Metadata, [string]$Name)

    $node = $Metadata.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $node) {
        return $null
    }
    return [string]$node.InnerText
}

function Get-ReleasePackageMetadata {
    param([string]$PackageId, [string]$Version)

    $packagePath = Join-Path $resolvedPackagesRoot (
        $PackageId.ToLowerInvariant() + '\' + $Version)
    if (-not [System.IO.Directory]::Exists($packagePath)) {
        throw "Required sealed NuGet package does not exist: $PackageId/$Version"
    }
    $nuspecFiles = @(Get-ChildItem -Force -File -Filter '*.nuspec' -LiteralPath $packagePath)
    if ($nuspecFiles.Count -ne 1) {
        throw "Expected exactly one nuspec for $PackageId/$Version under $packagePath."
    }
    [xml]$nuspec = Get-Content -Raw -LiteralPath $nuspecFiles[0].FullName
    $metadata = $nuspec.SelectSingleNode("//*[local-name()='metadata']")
    if ($null -eq $metadata -or
        (Get-NuspecElementText -Metadata $metadata -Name 'id') -cne $PackageId -or
        (Get-NuspecElementText -Metadata $metadata -Name 'version') -cne $Version) {
        throw "NuGet metadata identity differs from the required package: $PackageId/$Version"
    }
    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    $licenseType = $null
    $licenseValue = $null
    if ($null -ne $licenseNode) {
        $licenseType = [string]$licenseNode.GetAttribute('type')
        $licenseValue = [string]$licenseNode.InnerText
    }
    $repositoryUrl = $null
    if ($null -ne $repositoryNode) {
        $repositoryUrl = [string]$repositoryNode.GetAttribute('url')
    }
    $projectUrl = Get-NuspecElementText -Metadata $metadata -Name 'projectUrl'

    $licenseCandidates = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in @(Get-ChildItem -Force -File -Recurse -LiteralPath $packagePath)) {
        $isNamedLicenseSource = $file.Name -match '(?i)^(?:LICENSE|NOTICE|THIRD[-_. ]PARTY[-_. ]NOTICES)(?:\.|$)'
        $isDeclaredLicenseFile = $licenseType -ceq 'file' -and
            $file.FullName.EndsWith(
                $licenseValue.Replace('/', [System.IO.Path]::DirectorySeparatorChar),
                [System.StringComparison]::OrdinalIgnoreCase)
        if (-not $isNamedLicenseSource -and -not $isDeclaredLicenseFile) {
            continue
        }
        $relativePath = $file.FullName.Substring($packagePath.Length).TrimStart('\', '/').Replace('\', '/')
        $destinationPath = Join-Path $evidencePath (
            'license-sources\' + $PackageId.ToLowerInvariant() + '\' + $Version + '\' +
            $relativePath.Replace('/', '\'))
        [System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
        [System.IO.File]::Copy($file.FullName, $destinationPath, $true)
        $licenseCandidates.Add([pscustomobject][ordered]@{
                path = $relativePath
                length = [long]$file.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
            })
    }

    return [pscustomobject][ordered]@{
        id = $PackageId
        version = $Version
        licenseType = $licenseType
        licenseValue = $licenseValue
        projectUrl = $projectUrl
        repositoryUrl = $repositoryUrl
        nuspecPath = $nuspecFiles[0].Name
        licenseSources = $licenseCandidates.ToArray()
    }
}

function Assert-RuntimePackResolution {
    param([string]$AssetsPath)

    if (-not [System.IO.File]::Exists($AssetsPath)) {
        throw "Locked restore did not produce the application assets file: $AssetsPath"
    }
    $assets = Get-Content -Raw -LiteralPath $AssetsPath | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties | ForEach-Object { $_.Name })
    $requiredPackageLibraries = @(
        'Microsoft.NETCore.App.Runtime.win-x64/10.0.11',
        'Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.11',
        'Microsoft.NETCore.App.Host.win-x64/10.0.11',
        'Microsoft.Data.Sqlite/10.0.11',
        'Microsoft.Data.Sqlite.Core/10.0.11',
        'SQLitePCLRaw.bundle_e_sqlite3/2.1.12',
        'SQLitePCLRaw.core/2.1.12',
        'SQLitePCLRaw.lib.e_sqlite3/2.1.12',
        'SQLitePCLRaw.provider.e_sqlite3/2.1.12')
    foreach ($requiredLibrary in $requiredPackageLibraries) {
        if ($libraries -cnotcontains $requiredLibrary) {
            throw "Locked restore did not resolve the exact approved package: $requiredLibrary"
        }
    }
    $wrongRuntimePacks = @($libraries | Where-Object {
            $_ -match '^Microsoft\.(?:NETCore\.App\.(?:Runtime|Host)|WindowsDesktop\.App\.Runtime)\.win-x64/' -and
            $_ -notmatch '/10\.0\.11$'
        })
    if ($wrongRuntimePacks.Count -gt 0) {
        throw "Locked restore resolved an unpinned self-contained runtime pack: $($wrongRuntimePacks -join ', ')"
    }
    $actualPackageLibraries = @($assets.libraries.PSObject.Properties |
        Where-Object { [string]$_.Value.type -ceq 'package' } |
        ForEach-Object { $_.Name })
    $unexpectedPackageLibraries = @($actualPackageLibraries | Where-Object {
            $requiredPackageLibraries -cnotcontains $_
        })
    if ($unexpectedPackageLibraries.Count -gt 0) {
        throw "Locked restore contains a package outside the exact approved production dependency closure: $($unexpectedPackageLibraries -join ', ')"
    }
}

function Assert-ReleaseDependencyInventory {
    param(
        [string]$InventoryPath,
        [object[]]$CapturedComponents
    )

    if (-not [System.IO.File]::Exists($InventoryPath)) {
        throw "Versioned release dependency inventory does not exist: $InventoryPath"
    }
    $inventory = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
    if ($inventory.schemaVersion -ne 1 -or $inventory.components -isnot [System.Array]) {
        throw 'Release dependency inventory must use schemaVersion 1 and a components array.'
    }
    $inventoryComponents = @($inventory.components)
    if ($inventoryComponents.Count -ne $CapturedComponents.Count) {
        throw 'Release dependency inventory component count differs from the captured publish closure.'
    }
    $inventoryByName = @{}
    foreach ($component in $inventoryComponents) {
        $propertyNames = @($component.PSObject.Properties | ForEach-Object { $_.Name } | Sort-Object)
        $expectedProperties = @(
            'licenseIdentifier',
            'name',
            'officialSourceUrl',
            'packagedLicensePaths',
            'redistributionApproval',
            'redistributionTermUrl',
            'shippedPaths',
            'version')
        if ([string]::Join('|', $propertyNames) -cne
            [string]::Join('|', $expectedProperties)) {
            throw "Release dependency inventory component has unexpected or missing fields: $($component.name)"
        }
        if ([string]::IsNullOrWhiteSpace([string]$component.name) -or
            $inventoryByName.ContainsKey([string]$component.name)) {
            throw "Release dependency inventory contains a missing or duplicate component name: $($component.name)"
        }
        if ($component.shippedPaths -isnot [System.Array] -or
            $component.packagedLicensePaths -isnot [System.Array]) {
            throw "Release dependency inventory paths must be JSON arrays: $($component.name)"
        }
        if ([string]::IsNullOrWhiteSpace([string]$component.licenseIdentifier)) {
            throw "Release dependency inventory omits a license identifier: $($component.name)"
        }
        if ([string]$component.redistributionApproval -cne 'pending-owner-approval') {
            throw "Release dependency redistribution approval must remain pending-owner-approval at the provisional gate: $($component.name)"
        }
        foreach ($urlProperty in 'officialSourceUrl', 'redistributionTermUrl') {
            $uri = $null
            if (-not [System.Uri]::TryCreate(
                    [string]$component.$urlProperty,
                    [System.UriKind]::Absolute,
                    [ref]$uri) -or
                $uri.Scheme -cne 'https') {
                throw "Release dependency inventory requires an absolute HTTPS $urlProperty`: $($component.name)"
            }
        }
        foreach ($licensePath in @($component.packagedLicensePaths)) {
            if ([string]::IsNullOrWhiteSpace([string]$licensePath) -or
                -not ([string]$licensePath).StartsWith(
                    'licenses/', [System.StringComparison]::Ordinal)) {
                throw "Release dependency inventory contains a noncanonical packaged license path: $($component.name)"
            }
            $literalPath = Join-Path (
                Join-Path $resolvedRepositoryRoot 'eng\release') (
                ([string]$licensePath).Replace('/', '\'))
            if (-not [System.IO.File]::Exists($literalPath)) {
                throw "Release dependency inventory names a missing packaged license or notice: $licensePath"
            }
        }
        $inventoryByName.Add([string]$component.name, $component)
    }

    foreach ($captured in $CapturedComponents) {
        if (-not $inventoryByName.ContainsKey([string]$captured.name)) {
            throw "Release dependency inventory omits captured component: $($captured.name)"
        }
        $component = $inventoryByName[[string]$captured.name]
        if ([string]$component.version -cne [string]$captured.version) {
            throw "Release dependency inventory version differs from capture: $($captured.name)"
        }
        $inventoryPaths = [string[]]@($component.shippedPaths)
        $capturedPaths = [string[]]@($captured.shippedPaths)
        [System.Array]::Sort($inventoryPaths, [System.StringComparer]::Ordinal)
        [System.Array]::Sort($capturedPaths, [System.StringComparer]::Ordinal)
        if ([string]::Join('|', $inventoryPaths) -cne
            [string]::Join('|', $capturedPaths)) {
            throw "Release dependency inventory shipped paths differ from capture: $($captured.name)"
        }
    }

    return $inventory
}

foreach ($requiredFile in @(
        $projectPath,
        $dotnetPath,
        (Join-Path $resolvedRepositoryRoot 'global.json'),
        (Join-Path $resolvedRepositoryRoot 'NuGet.Config'),
        (Join-Path $resolvedRepositoryRoot 'eng\release\PhotoSiftRelease.psm1'),
        (Join-Path $resolvedRepositoryRoot 'eng\release\assets\Start-PhotoSift.cmd'),
        (Join-Path $resolvedRepositoryRoot 'eng\release\assets\Verify-PhotoSiftRelease.ps1'))) {
    if (-not [System.IO.File]::Exists($requiredFile)) {
        throw "Required release input does not exist: $requiredFile"
    }
}
$licensesPath = Join-Path $resolvedRepositoryRoot 'eng\release\licenses'
if (-not $CaptureOnly) {
    $noticesPath = Join-Path $resolvedRepositoryRoot 'THIRD-PARTY-NOTICES.md'
    if (-not [System.IO.File]::Exists($noticesPath)) {
        throw "Required release notices do not exist: $noticesPath"
    }
    if (-not [System.IO.Directory]::Exists($licensesPath) -or
        @(Get-ChildItem -Force -File -Recurse -LiteralPath $licensesPath).Count -eq 0) {
        throw "Release license directory is missing or empty: $licensesPath"
    }
}
if (-not [System.IO.Directory]::Exists((Join-Path $resolvedSdkRoot 'sdk\10.0.400'))) {
    throw "Pinned SDK 10.0.400 is not present under the supplied SDK root: $resolvedSdkRoot"
}
$globalJson = Get-Content -Raw -LiteralPath (
    Join-Path $resolvedRepositoryRoot 'global.json') | ConvertFrom-Json
if ([string]$globalJson.sdk.version -cne $sdkVersion) {
    throw "global.json must pin SDK $sdkVersion."
}
if ([System.IO.Directory]::Exists($resolvedOutputRoot) -and
    @(Get-ChildItem -Force -LiteralPath $resolvedOutputRoot).Count -gt 0) {
    throw "Release output root must be absent or empty: $resolvedOutputRoot"
}
[System.IO.Directory]::CreateDirectory($resolvedOutputRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($evidencePath) | Out-Null

$netCoreFiles = @(Read-RuntimePackList `
    -PackageId 'microsoft.netcore.app.runtime.win-x64' `
    -ExpectedFrameworkName 'Microsoft.NETCore.App')
$windowsDesktopFiles = @(Read-RuntimePackList `
    -PackageId 'microsoft.windowsdesktop.app.runtime.win-x64' `
    -ExpectedFrameworkName 'Microsoft.WindowsDesktop.App')
$runtimePackFiles = @($netCoreFiles + $windowsDesktopFiles)
$frameworkPaths = [string[]]@($runtimePackFiles | ForEach-Object { $_.Path })
[System.Array]::Sort($frameworkPaths, [System.StringComparer]::Ordinal)
$frameworkPathSet = [System.Collections.Generic.Dictionary[string, bool]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($frameworkPath in $frameworkPaths) {
    if ($frameworkPathSet.ContainsKey($frameworkPath)) {
        throw "Pinned runtime packs contain a duplicate or case-colliding publish path: $frameworkPath"
    }
    $frameworkPathSet.Add($frameworkPath, $true)
}
$nativePaths = [string[]]@(
    @($runtimePackFiles | Where-Object { $_.Type -ceq 'Native' } |
        ForEach-Object { $_.Path }) + @('e_sqlite3.dll'))
$nativePathSet = [System.Collections.Generic.Dictionary[string, bool]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($nativePath in $nativePaths) {
    if ($nativePathSet.ContainsKey($nativePath)) {
        throw "Pinned native closure contains a duplicate or case-colliding path: $nativePath"
    }
    $nativePathSet.Add($nativePath, $true)
}
[System.Array]::Sort($nativePaths, [System.StringComparer]::Ordinal)

$restoreArguments = @(
    'restore', 'src\PhotoSift.App\PhotoSift.App.csproj',
    '-r', $runtimeIdentifier,
    '--locked-mode',
    '--configfile', 'NuGet.Config',
    '--packages', $resolvedPackagesRoot,
    '-p:RuntimeFrameworkVersion=10.0.11',
    '-p:TargetLatestRuntimePatch=false',
    '-p:NuGetAudit=false')
Invoke-ReleaseExternal `
    -Name 'restore' `
    -FilePath $dotnetPath `
    -Arguments $restoreArguments `
    -WorkingDirectory $resolvedRepositoryRoot | Out-Null
Assert-RuntimePackResolution -AssetsPath (
    Join-Path $resolvedRepositoryRoot 'src\PhotoSift.App\obj\project.assets.json')

$publishArguments = New-Object 'System.Collections.Generic.List[object]'
for ($index = 0; $index -lt 2; $index++) {
    [System.IO.Directory]::CreateDirectory($rawPublishPaths[$index]) | Out-Null
    $arguments = @(
        'publish', 'src\PhotoSift.App\PhotoSift.App.csproj',
        '-c', 'Release',
        '-r', $runtimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '-p:PublishSingleFile=false',
        '-p:RuntimeFrameworkVersion=10.0.11',
        '-p:TargetLatestRuntimePatch=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $rawPublishPaths[$index])
    $publishArguments.Add($arguments)
    Invoke-ReleaseExternal `
        -Name ('publish-' + ($index + 1)) `
        -FilePath $dotnetPath `
        -Arguments $arguments `
        -WorkingDirectory $resolvedRepositoryRoot | Out-Null
    foreach ($frameworkPath in $frameworkPaths) {
        if (-not [System.IO.File]::Exists((Join-Path $rawPublishPaths[$index] $frameworkPath))) {
            throw "Published framework closure is missing pinned path: $frameworkPath"
        }
    }
    foreach ($nativePath in $nativePaths) {
        if (-not [System.IO.File]::Exists((Join-Path $rawPublishPaths[$index] $nativePath))) {
            throw "Published native closure is missing pinned path: $nativePath"
        }
    }
}

$rawInventories = @(
    @(Get-ReleaseInventory -RootPath $rawPublishPaths[0]),
    @(Get-ReleaseInventory -RootPath $rawPublishPaths[1]))
Assert-ReleaseInventoriesEqual `
    -First $rawInventories[0] `
    -Second $rawInventories[1] `
    -Context 'Raw publish'

$frameworkManifest = [pscustomobject][ordered]@{
    schemaVersion = 1
    runtimeIdentifier = $runtimeIdentifier
    netCoreRuntimeVersion = $runtimeVersion
    windowsDesktopRuntimeVersion = $runtimeVersion
    frameworkPaths = $frameworkPaths
}
$frameworkManifestPath = Join-Path $evidencePath 'framework-closure.json'
Write-ReleaseUtf8NoBom `
    -LiteralPath $frameworkManifestPath `
    -Content (($frameworkManifest | ConvertTo-Json -Depth 6) + [System.Environment]::NewLine)
$nativePolicy = [pscustomobject][ordered]@{
    schemaVersion = 1
    nativePaths = $nativePaths
}
$nativePolicyJson = ($nativePolicy | ConvertTo-Json -Depth 5) + [System.Environment]::NewLine

$packageSpecifications = @(
    @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Version = '10.0.11' },
    @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Version = '10.0.11' },
    @{ Id = 'Microsoft.NETCore.App.Host.win-x64'; Version = '10.0.11' },
    @{ Id = 'Microsoft.Data.Sqlite'; Version = '10.0.11' },
    @{ Id = 'Microsoft.Data.Sqlite.Core'; Version = '10.0.11' },
    @{ Id = 'SQLitePCLRaw.bundle_e_sqlite3'; Version = '2.1.12' },
    @{ Id = 'SQLitePCLRaw.core'; Version = '2.1.12' },
    @{ Id = 'SQLitePCLRaw.lib.e_sqlite3'; Version = '2.1.12' },
    @{ Id = 'SQLitePCLRaw.provider.e_sqlite3'; Version = '2.1.12' })
$packageCaptures = @($packageSpecifications | ForEach-Object {
        Get-ReleasePackageMetadata -PackageId $_.Id -Version $_.Version
    })
$nativeSqliteVersion = Get-ReleaseFileVersion -LiteralPath (
    Join-Path $rawPublishPaths[0] 'e_sqlite3.dll')
$nativeSqliteEngineVersion = [string]$nativeSqliteVersion.ProductVersion
if ([string]::IsNullOrWhiteSpace($nativeSqliteEngineVersion)) {
    $nativeSqliteEngineVersion = [string]$nativeSqliteVersion.FileVersion
}
if ([string]::IsNullOrWhiteSpace($nativeSqliteEngineVersion)) {
    throw 'Could not identify the native SQLite engine version from the shipped e_sqlite3.dll.'
}

$components = @(
    [pscustomobject][ordered]@{
        name = '.NET Host'
        version = $runtimeVersion
        shippedPaths = @('PhotoSift.App.exe')
        packageId = 'Microsoft.NETCore.App.Host.win-x64'
    },
    [pscustomobject][ordered]@{
        name = '.NET Runtime'
        version = $runtimeVersion
        shippedPaths = @($netCoreFiles | ForEach-Object { $_.Path })
        packageId = 'Microsoft.NETCore.App.Runtime.win-x64'
    },
    [pscustomobject][ordered]@{
        name = 'Microsoft.Data.Sqlite'
        version = '10.0.11'
        shippedPaths = @()
        packageId = 'Microsoft.Data.Sqlite'
    },
    [pscustomobject][ordered]@{
        name = 'Microsoft.Data.Sqlite.Core'
        version = '10.0.11'
        shippedPaths = @('Microsoft.Data.Sqlite.dll')
        packageId = 'Microsoft.Data.Sqlite.Core'
    },
    [pscustomobject][ordered]@{
        name = 'Native SQLite'
        version = $nativeSqliteEngineVersion
        shippedPaths = @('e_sqlite3.dll')
        packageId = 'SQLitePCLRaw.lib.e_sqlite3'
        fileVersion = [string]$nativeSqliteVersion.FileVersion
        productVersion = [string]$nativeSqliteVersion.ProductVersion
    },
    [pscustomobject][ordered]@{
        name = 'SQLitePCLRaw.bundle_e_sqlite3'
        version = '2.1.12'
        shippedPaths = @('SQLitePCLRaw.batteries_v2.dll')
        packageId = 'SQLitePCLRaw.bundle_e_sqlite3'
    },
    [pscustomobject][ordered]@{
        name = 'SQLitePCLRaw.core'
        version = '2.1.12'
        shippedPaths = @('SQLitePCLRaw.core.dll')
        packageId = 'SQLitePCLRaw.core'
    },
    [pscustomobject][ordered]@{
        name = 'SQLitePCLRaw.lib.e_sqlite3'
        version = '2.1.12'
        shippedPaths = @('e_sqlite3.dll')
        packageId = 'SQLitePCLRaw.lib.e_sqlite3'
    },
    [pscustomobject][ordered]@{
        name = 'SQLitePCLRaw.provider.e_sqlite3'
        version = '2.1.12'
        shippedPaths = @('SQLitePCLRaw.provider.e_sqlite3.dll')
        packageId = 'SQLitePCLRaw.provider.e_sqlite3'
    },
    [pscustomobject][ordered]@{
        name = 'Windows Desktop / WPF'
        version = $runtimeVersion
        shippedPaths = @($windowsDesktopFiles | ForEach-Object { $_.Path })
        packageId = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
    })
$rawPathSet = [System.Collections.Generic.Dictionary[string, bool]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $rawInventories[0]) {
    $rawPathSet[$entry.path] = $true
}
foreach ($component in $components) {
    foreach ($shippedPath in @($component.shippedPaths)) {
        if (-not $rawPathSet.ContainsKey($shippedPath)) {
            throw "Dependency capture maps '$($component.name)' to a missing shipped path: $shippedPath"
        }
    }
}
$dependencyCapture = [pscustomobject][ordered]@{
    schemaVersion = 1
    sourceIdentity = $SourceIdentity
    runtimeIdentifier = $runtimeIdentifier
    components = $components
    packages = $packageCaptures
}
Write-ReleaseUtf8NoBom `
    -LiteralPath (Join-Path $evidencePath 'dependency-capture.json') `
    -Content (($dependencyCapture | ConvertTo-Json -Depth 10) + [System.Environment]::NewLine)
for ($index = 0; $index -lt 2; $index++) {
    Write-ReleaseUtf8NoBom `
        -LiteralPath (Join-Path $evidencePath ('raw-publish-' + ($index + 1) + '-inventory.json')) `
        -Content (($rawInventories[$index] | ConvertTo-Json -Depth 6) + [System.Environment]::NewLine)
}

if ($CaptureOnly) {
    $provisionalPublishPath = Join-Path $resolvedOutputRoot 'provisional-publish'
    Copy-ReleaseDirectoryContents `
        -SourcePath $rawPublishPaths[0] `
        -DestinationPath $provisionalPublishPath
    $captureEvidence = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'needs-license-inventory'
        sourceIdentity = $SourceIdentity
        sdkVersion = $sdkVersion
        runtimeIdentifier = $runtimeIdentifier
        netCoreRuntimeVersion = $runtimeVersion
        windowsDesktopRuntimeVersion = $runtimeVersion
        selfContained = $true
        publishSingleFile = $false
        targetLatestRuntimePatch = $false
        restoreArguments = $restoreArguments
        publishArguments = $publishArguments
        comparedFields = @('path', 'length', 'sha256')
        rawFileCount = $rawInventories[0].Count
    }
    Write-ReleaseUtf8NoBom `
        -LiteralPath (Join-Path $evidencePath 'capture-evidence.json') `
        -Content (($captureEvidence | ConvertTo-Json -Depth 8) + [System.Environment]::NewLine)
    return [pscustomobject]@{
        status = 'needs-license-inventory'
        outputRoot = $resolvedOutputRoot
        provisionalPublishPath = $provisionalPublishPath
        dependencyCapturePath = Join-Path $evidencePath 'dependency-capture.json'
        inventoriesEqual = $true
        comparedFields = @('path', 'length', 'sha256')
        nativePaths = $nativePaths
        frameworkClosurePath = $frameworkManifestPath
    }
}

Assert-ReleaseDependencyInventory `
    -InventoryPath (Join-Path $resolvedRepositoryRoot 'eng\release\release-dependencies.json') `
    -CapturedComponents $components | Out-Null

Import-Module (Join-Path $resolvedRepositoryRoot 'eng\release\PhotoSiftRelease.psm1') -Force
for ($index = 0; $index -lt 2; $index++) {
    Copy-ReleaseDirectoryContents `
        -SourcePath $rawPublishPaths[$index] `
        -DestinationPath $packagePaths[$index]
    [System.IO.File]::Copy(
        (Join-Path $resolvedRepositoryRoot 'eng\release\assets\Start-PhotoSift.cmd'),
        (Join-Path $packagePaths[$index] 'Start-PhotoSift.cmd'))
    [System.IO.File]::Copy(
        (Join-Path $resolvedRepositoryRoot 'eng\release\assets\Verify-PhotoSiftRelease.ps1'),
        (Join-Path $packagePaths[$index] 'Verify-PhotoSiftRelease.ps1'))
    [System.IO.File]::Copy(
        (Join-Path $resolvedRepositoryRoot 'THIRD-PARTY-NOTICES.md'),
        (Join-Path $packagePaths[$index] 'THIRD-PARTY-NOTICES.md'))
    Copy-Item `
        -Force `
        -Recurse `
        -LiteralPath $licensesPath `
        -Destination (Join-Path $packagePaths[$index] 'licenses')
    Write-ReleaseUtf8NoBom `
        -LiteralPath (Join-Path $packagePaths[$index] 'release-native-paths.json') `
        -Content $nativePolicyJson
    & (Join-Path $resolvedRepositoryRoot 'eng\Verify-RuntimeBoundary.ps1') `
        -DependencyManifestPath (Join-Path $packagePaths[$index] 'PhotoSift.App.deps.json') `
        -SourceRootPath (Join-Path $resolvedRepositoryRoot 'src') `
        -FrameworkClosureManifestPath $frameworkManifestPath | Out-Null
    New-PhotoSiftReleaseManifest `
        -PackagePath $packagePaths[$index] `
        -OutputPath (Join-Path $packagePaths[$index] 'release-manifest.json') | Out-Null
}

$packageInventories = @(
    @(Get-ReleaseInventory -RootPath $packagePaths[0]),
    @(Get-ReleaseInventory -RootPath $packagePaths[1]))
Assert-ReleaseInventoriesEqual `
    -First $packageInventories[0] `
    -Second $packageInventories[1] `
    -Context 'Packaged publish'

Copy-ReleaseDirectoryContents -SourcePath $packagePaths[0] -DestinationPath $candidatePath
$candidateManifestSha256 = (Get-FileHash `
    -Algorithm SHA256 `
    -LiteralPath (Join-Path $candidatePath 'release-manifest.json')).Hash
$candidateDigest = [System.IO.File]::ReadAllText(
    (Join-Path $candidatePath 'release-manifest.sha256')).Trim()

for ($index = 0; $index -lt 2; $index++) {
    Write-ReleaseUtf8NoBom `
        -LiteralPath (Join-Path $evidencePath ('publish-' + ($index + 1) + '-inventory.json')) `
        -Content (($packageInventories[$index] | ConvertTo-Json -Depth 6) + [System.Environment]::NewLine)
}
$equalityReport = [pscustomobject][ordered]@{
    schemaVersion = 1
    inventoriesEqual = $true
    comparedFields = @('path', 'length', 'sha256')
    fileCount = $packageInventories[0].Count
}
Write-ReleaseUtf8NoBom `
    -LiteralPath (Join-Path $evidencePath 'publish-equality.json') `
    -Content (($equalityReport | ConvertTo-Json -Depth 5) + [System.Environment]::NewLine)
$buildEvidence = [pscustomobject][ordered]@{
    schemaVersion = 1
    sourceIdentity = $SourceIdentity
    sdkVersion = $sdkVersion
    runtimeIdentifier = $runtimeIdentifier
    netCoreRuntimeVersion = $runtimeVersion
    windowsDesktopRuntimeVersion = $runtimeVersion
    selfContained = $true
    publishSingleFile = $false
    targetLatestRuntimePatch = $false
    restoreArguments = $restoreArguments
    publishArguments = $publishArguments
    candidateManifestSha256 = $candidateManifestSha256
    candidateDigest = $candidateDigest
}
Write-ReleaseUtf8NoBom `
    -LiteralPath (Join-Path $evidencePath 'build-evidence.json') `
    -Content (($buildEvidence | ConvertTo-Json -Depth 8) + [System.Environment]::NewLine)

return [pscustomobject]@{
    status = 'pending-owner-approval'
    outputRoot = $resolvedOutputRoot
    candidatePath = $candidatePath
    candidateManifestSha256 = $candidateManifestSha256
    candidateDigest = $candidateDigest
    inventoriesEqual = $true
    comparedFields = @('path', 'length', 'sha256')
    fileCount = $packageInventories[0].Count
    nativePaths = $nativePaths
    frameworkClosurePath = $frameworkManifestPath
}
