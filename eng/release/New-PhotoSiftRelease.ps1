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
$workPath = Join-Path $resolvedOutputRoot '.work'

trap {
    $failureRecord = $_
    try {
        if ([System.IO.Directory]::Exists($evidencePath)) {
            Write-ReleaseUtf8NoBom -LiteralPath (Join-Path $evidencePath 'failure.json') -Content (
                [pscustomobject]@{
                    exceptionType = $failureRecord.Exception.GetType().FullName
                    message = $failureRecord.Exception.Message
                    positionMessage = $failureRecord.InvocationInfo.PositionMessage
                    scriptStackTrace = $failureRecord.ScriptStackTrace
                } | ConvertTo-Json -Depth 4)
        }
    } catch { }
    throw $failureRecord
}

$sourcePrefix = $resolvedRepositoryRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if ($resolvedOutputRoot -ieq $resolvedRepositoryRoot -or
    $resolvedOutputRoot.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output root must be outside the source repository.'
}

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
    }
    else {
        Push-Location -LiteralPath $WorkingDirectory
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            # Resolve while errors are still terminating: a failed launch must
            # never reuse an earlier process's successful exit code.
            $nativeCommand = Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop
            $global:LASTEXITCODE = $null
            # Windows PowerShell turns redirected stderr into ErrorRecords.
            # Capture those as text and judge the process by its exit code.
            # This preference is function-local (including on PowerShell 7).
            $PSNativeCommandUseErrorActionPreference = $false
            $ErrorActionPreference = 'Continue'
            $output = @(& $nativeCommand.Path @Arguments 2>&1)
            $exitCode = $global:LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
            Pop-Location
        }
        if ($null -eq $exitCode) {
            throw "Release command '$Name' did not return a native exit code: $($output -join [Environment]::NewLine)"
        }
        $result = [pscustomobject]@{ ExitCode = $exitCode; Output = $output -join [System.Environment]::NewLine }
    }
    if ($null -eq $result) { throw "Release command '$Name' returned no result." }
    Write-ReleaseUtf8NoBom -LiteralPath (Join-Path $evidencePath ($Name + '-command.json')) -Content (
        [pscustomobject]@{ name = $Name; executable = $FilePath; arguments = $Arguments; workingDirectory = $WorkingDirectory; exitCode = $result.ExitCode; output = $result.Output; injected = ($null -ne $Operations) } | ConvertTo-Json -Depth 5)
    if ([int]$result.ExitCode -ne 0) {
        throw "Release command '$Name' exited $($result.ExitCode): $($result.Output)"
    }
    return $result
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

function Get-RawPublishEntries {
    param([string]$RootPath)

    if ($null -ne $Operations -and $Operations.ContainsKey('GetRawPublishEntries')) {
        return @(& $Operations.GetRawPublishEntries -RootPath $RootPath)
    }
    return @(Get-ChildItem -Force -Recurse -LiteralPath $RootPath)
}

function Assert-RawPublishContentPolicy {
    param([string]$RootPath)

    $resolvedRootPath = [System.IO.Path]::GetFullPath($RootPath)
    $rootItem = Get-Item -Force -LiteralPath $resolvedRootPath
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Raw publish root is a reparse point: $resolvedRootPath"
    }
    $prefix = $resolvedRootPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $seen = [System.Collections.Generic.Dictionary[string, bool]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @(Get-RawPublishEntries -RootPath $resolvedRootPath)) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Raw publish contains a reparse point: $($entry.FullName)"
        }
        $fullName = [System.IO.Path]::GetFullPath([string]$entry.FullName)
        if (-not $fullName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Raw publish entry escaped its root: $fullName"
        }
        $relativePath = $fullName.Substring($prefix.Length).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            $relativePath -cne $relativePath.Trim() -or
            $relativePath.Contains('\') -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            @($relativePath.Split('/') | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..')
                }).Count -gt 0) {
            throw "Raw publish contains a noncanonical relative path: $relativePath"
        }
        if ($seen.ContainsKey($relativePath)) {
            throw "Raw publish contains a duplicate or case-colliding path: $relativePath"
        }
        $seen.Add($relativePath, $true)

        if (-not [bool]$entry.PSIsContainer -and
            [System.IO.Path]::GetFileName($relativePath) -in @(
                'release-manifest.json',
                'release-manifest.sha256')) {
            throw "Raw capture contains forbidden final release metadata: $relativePath"
        }
    }
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

function Copy-CleanReleaseSource {
    param([string]$SourcePath, [string]$DestinationPath)

    $resolvedSourcePath = [System.IO.Path]::GetFullPath($SourcePath)
    $sourcePrefix = $resolvedSourcePath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    foreach ($entry in @(Get-ChildItem -Force -Recurse -LiteralPath $resolvedSourcePath)) {
        $relativePath = $entry.FullName.Substring($sourcePrefix.Length)
        $segments = @($relativePath -split '[\\/]')
        if (@($segments | Where-Object { $_ -in @('.git', '.claude', '.superpowers', '.codex', '.agents', 'bin', 'obj') }).Count -gt 0) {
            continue
        }
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release source contains a reparse point: $($entry.FullName)"
        }
        $destination = Join-Path $DestinationPath $relativePath
        if ($entry.PSIsContainer) {
            [System.IO.Directory]::CreateDirectory($destination) | Out-Null
        }
        else {
            [System.IO.Directory]::CreateDirectory(
                [System.IO.Path]::GetDirectoryName($destination)) | Out-Null
            [System.IO.File]::Copy($entry.FullName, $destination)
        }
    }
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

function Get-NativeSqliteIdentity {
    param([string]$LiteralPath)

    $packagePayload = Join-Path $resolvedPackagesRoot 'sqlitepclraw.lib.e_sqlite3/2.1.12/runtimes/win-x64/native/e_sqlite3.dll'
    $hash = (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
    if (-not [System.IO.File]::Exists($packagePayload) -or
        $hash -cne (Get-FileHash -LiteralPath $packagePayload -Algorithm SHA256).Hash) {
        throw 'Published native SQLite SHA256 differs from the exact pinned package payload.'
    }
    $peVersion = Get-ReleaseFileVersion -LiteralPath $LiteralPath
    $version = [string]$peVersion.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$peVersion.FileVersion }
    $versionSource = 'pe-version-resource'
    $sourceId = $null
    if ([string]::IsNullOrWhiteSpace($version)) {
        # The pinned SQLite PE has no version resource. Inspect its NUL-delimited
        # sqlite3_libversion/sqlite3_sourceid constants without loading the DLL.
        $bytes = [System.IO.File]::ReadAllBytes($LiteralPath)
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        $versions = @([regex]::Matches($text, '(?<=\x00)3\.[0-9]+\.[0-9]+(?=\x00)') | ForEach-Object { $_.Value } | Sort-Object -Unique)
        $sourceIds = @([regex]::Matches($text, '(?<=\x00)20[0-9]{2}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} [a-f0-9]{64}(?=\x00)') | ForEach-Object { $_.Value } | Sort-Object -Unique)
        if ($versions.Count -eq 0 -or $sourceIds.Count -eq 0) { throw 'Missing static native SQLite version/source-id evidence.' }
        if ($versions.Count -ne 1 -or $sourceIds.Count -ne 1) { throw 'Ambiguous static native SQLite version/source-id evidence.' }
        $version = $versions[0]
        $sourceId = $sourceIds[0]
        $versionSource = 'embedded-utf8'
    }
    return [pscustomobject]@{
        version = $version; versionSource = $versionSource; sqliteSourceId = $sourceId; sha256 = $hash
        fileVersion = [string]$peVersion.FileVersion; productVersion = [string]$peVersion.ProductVersion
    }
}

function Get-ReleasePackageMetadata {
    param([string]$PackageId, [string]$Version)

    $packagePath = Join-Path $resolvedPackagesRoot (
        $PackageId.ToLowerInvariant() + '\' + $Version)
    if ($PackageId -ceq 'Microsoft.NETCore.App.Host.win-x64' -and
        -not [System.IO.Directory]::Exists($packagePath)) {
        $hostRelativePath = 'packs/Microsoft.NETCore.App.Host.win-x64/' + $Version + '/runtimes/win-x64/native/apphost.exe'
        $hostPath = Join-Path $resolvedSdkRoot $hostRelativePath
        if (-not [System.IO.File]::Exists($hostPath)) {
            throw "Pinned apphost is absent from both the sealed NuGet cache and SDK packs: $Version"
        }
        $hostLicenses = @(
            foreach ($licenseName in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
                $source = Join-Path $resolvedSdkRoot $licenseName
                if (-not [System.IO.File]::Exists($source)) { throw "SDK apphost license source missing: $source" }
                $destination = Join-Path $evidencePath ('license-sources/' + $PackageId.ToLowerInvariant() + '/' + $Version + '/' + $licenseName)
                [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
                [System.IO.File]::Copy($source, $destination, $true)
                [pscustomobject]@{ path = $licenseName; length = (Get-Item -LiteralPath $source).Length; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
            })
        return [pscustomobject][ordered]@{
            id = $PackageId; version = $Version; sourceKind = 'sdk-pack'
            sourcePath = $hostRelativePath; sourceSha256 = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash
            sdkVersion = $sdkVersion; licenseSources = $hostLicenses
        }
    }
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
        sourceKind = 'nuget'
        licenseType = $licenseType
        licenseValue = $licenseValue
        projectUrl = $projectUrl
        repositoryUrl = $repositoryUrl
        nuspecPath = $nuspecFiles[0].Name
        licenseSources = $licenseCandidates.ToArray()
    }
}

function Assert-FrameworkDependentResolution {
    param([string]$AssetsPath)

    if (-not [System.IO.File]::Exists($AssetsPath)) {
        throw "Locked restore did not produce the application assets file: $AssetsPath"
    }
    $assets = Get-Content -Raw -LiteralPath $AssetsPath | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties | ForEach-Object { $_.Name })
    $requiredPackageLibraries = @(
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
    # SDK 10.0.400 records these exact pinned packs as build-only downloads for
    # a RID restore. They must never become approved application libraries or
    # candidate contents.
    $expectedBuildOnlyDownloads = @(
        'Microsoft.NETCore.App.Runtime.win-x64',
        'Microsoft.WindowsDesktop.App.Runtime.win-x64',
        'Microsoft.AspNetCore.App.Runtime.win-x64')
    $observedBuildOnlyDownloads = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $assets.PSObject.Properties['project'] -and
        $null -ne $assets.project.PSObject.Properties['frameworks']) {
        foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
            if ($null -eq $framework.Value.PSObject.Properties['downloadDependencies']) { continue }
            foreach ($download in $framework.Value.downloadDependencies) {
                $downloadName = [string]$download.name
                if ($expectedBuildOnlyDownloads -cnotcontains $downloadName -or
                    [string]$download.version -cnotmatch '^\[10\.0\.11,\s*10\.0\.11\]$') {
                    throw "Framework-dependent restore contains an unapproved or unpinned download dependency: $($download.name)/$($download.version)"
                }
                $observedBuildOnlyDownloads.Add($downloadName)
            }
        }
    }
    foreach ($expectedDownload in $expectedBuildOnlyDownloads) {
        if (@($observedBuildOnlyDownloads | Where-Object { $_ -ceq $expectedDownload }).Count -ne 1) {
            throw "Framework-dependent restore must contain the exact pinned build-only download dependency once: $expectedDownload/[10.0.11, 10.0.11]"
        }
    }
    if ($observedBuildOnlyDownloads.Count -ne $expectedBuildOnlyDownloads.Count) {
        throw 'Framework-dependent restore contains duplicate or unexpected build-only download dependencies.'
    }

    # A framework-dependent candidate needs only the apphost, never runtime
    # pack libraries. The apphost can be resolved from assets or the SDK/cache.
    $apphostPackId = 'Microsoft.NETCore.App.Host.win-x64'
    $resolvedApphostPacks = @($libraries | Where-Object { ($_ -split '/')[0] -ceq $apphostPackId })
    $sdkApphost = Join-Path $resolvedSdkRoot 'packs/Microsoft.NETCore.App.Host.win-x64/10.0.11/runtimes/win-x64/native/apphost.exe'
    if ($resolvedApphostPacks -cnotcontains 'Microsoft.NETCore.App.Host.win-x64/10.0.11' -and
        -not [System.IO.File]::Exists($sdkApphost) -and
        -not [System.IO.File]::Exists((Join-Path $resolvedPackagesRoot 'microsoft.netcore.app.host.win-x64/10.0.11/runtimes/win-x64/native/apphost.exe'))) {
        throw 'The pinned SDK/NuGet apphost pack is missing.'
    }
    $wrongRuntimePacks = @($libraries | Where-Object {
            $_ -match '^Microsoft\.(?:NETCore\.App\.(?:Runtime|Host)|WindowsDesktop\.App\.Runtime)\.win-x64/' -and
            $_ -notmatch '/10\.0\.11$'
        })
    if ($wrongRuntimePacks.Count -gt 0) {
        throw "Locked restore resolved an unpinned apphost pack: $($wrongRuntimePacks -join ', ')"
    }
    $actualPackageLibraries = @($assets.libraries.PSObject.Properties |
        Where-Object { [string]$_.Value.type -ceq 'package' } |
        ForEach-Object { $_.Name })
    $unexpectedPackageLibraries = @($actualPackageLibraries | Where-Object {
            $requiredPackageLibraries -cnotcontains $_ -and $resolvedApphostPacks -cnotcontains $_
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
        if (@('pending-owner-approval', 'owner-approved') -cnotcontains
            [string]$component.redistributionApproval) {
            throw "Release dependency redistribution approval is not recognized: $($component.name)"
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

    $approvalStates = @($inventoryComponents | ForEach-Object {
            [string]$_.redistributionApproval
        } | Sort-Object -Unique)
    if ($approvalStates.Count -ne 1) {
        throw 'Release dependency redistribution approval must use one uniform state.'
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
        (Join-Path $resolvedRepositoryRoot 'eng\release\assets\README.txt'),
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
if ([string]$globalJson.sdk.rollForward -cne 'disable') {
    throw 'global.json must disable SDK roll-forward for the release workflow.'
}
if ([System.IO.Directory]::Exists($resolvedOutputRoot) -and
    @(Get-ChildItem -Force -LiteralPath $resolvedOutputRoot).Count -gt 0) {
    throw "Release output root must be absent or empty: $resolvedOutputRoot"
}
[System.IO.Directory]::CreateDirectory($evidencePath) | Out-Null
$sdkSelection = Invoke-ReleaseExternal `
    -Name 'sdk-version' `
    -FilePath $dotnetPath `
    -Arguments @('--version') `
    -WorkingDirectory $resolvedRepositoryRoot
$observedSdkVersions = @(
    ([string]$sdkSelection.Output -split '\r?\n') |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($observedSdkVersions.Count -ne 1 -or
    [string]$observedSdkVersions[0] -cne $sdkVersion) {
    throw "Supplied dotnet host selected SDK '$($observedSdkVersions -join ',')'; required exact SDK is $sdkVersion."
}
$observedSdkVersion = [string]$observedSdkVersions[0]

$nativePaths = [string[]]@('e_sqlite3.dll')

$restoreArguments = New-Object 'System.Collections.Generic.List[object]'
$publishArguments = New-Object 'System.Collections.Generic.List[object]'
try {
    for ($index = 0; $index -lt 2; $index++) {
        $cleanSourcePath = Join-Path $workPath ('source-' + ($index + 1))
        Copy-CleanReleaseSource `
            -SourcePath $resolvedRepositoryRoot `
            -DestinationPath $cleanSourcePath

        $currentRestoreArguments = @(
            'restore', 'src\PhotoSift.App\PhotoSift.App.csproj',
            '-r', $runtimeIdentifier,
            '--locked-mode',
            '--configfile', 'NuGet.Config',
            '--packages', $resolvedPackagesRoot,
            '-p:SelfContained=false',
            '-p:RuntimeFrameworkVersion=10.0.11',
            '-p:TargetLatestRuntimePatch=false',
            '-p:NuGetAudit=false')
        $restoreArguments.Add($currentRestoreArguments)
        Invoke-ReleaseExternal `
            -Name ('restore-' + ($index + 1)) `
            -FilePath $dotnetPath `
            -Arguments $currentRestoreArguments `
            -WorkingDirectory $cleanSourcePath | Out-Null
        $restoredAssetsPath = Join-Path $cleanSourcePath 'src\PhotoSift.App\obj\project.assets.json'
        if ([System.IO.File]::Exists($restoredAssetsPath)) {
            [System.IO.File]::Copy($restoredAssetsPath, (Join-Path $evidencePath ('restore-' + ($index + 1) + '-project.assets.json')), $true)
        }
        Assert-FrameworkDependentResolution -AssetsPath $restoredAssetsPath

        [System.IO.Directory]::CreateDirectory($rawPublishPaths[$index]) | Out-Null
        $pathMapSource = $cleanSourcePath.Replace('=', '==').Replace(',', ',,')
        $arguments = @(
            'publish', 'src\PhotoSift.App\PhotoSift.App.csproj',
            '-c', 'Release',
            '-r', $runtimeIdentifier,
            '--self-contained', 'false',
            '--no-restore',
            '-p:RollForward=LatestPatch',
            '-p:PublishSingleFile=false',
            '-p:RuntimeFrameworkVersion=10.0.11',
            '-p:TargetLatestRuntimePatch=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            ('-p:PathMap=' + $pathMapSource + '=C:\PhotoSift\canonical-source'),
            '-o', $rawPublishPaths[$index])
        $publishArguments.Add($arguments)
        Invoke-ReleaseExternal `
            -Name ('publish-' + ($index + 1)) `
            -FilePath $dotnetPath `
            -Arguments $arguments `
            -WorkingDirectory $cleanSourcePath | Out-Null
        foreach ($nativePath in $nativePaths) {
            if (-not [System.IO.File]::Exists((Join-Path $rawPublishPaths[$index] $nativePath))) {
                throw "Published native closure is missing pinned path: $nativePath"
            }
        }
    }
}
finally {
    if ([System.IO.Directory]::Exists($workPath)) {
        Remove-Item -Recurse -Force -LiteralPath $workPath
    }
}

$runtimeBoundaryPath = Join-Path $resolvedRepositoryRoot 'eng\Verify-RuntimeBoundary.ps1'
Import-Module (Join-Path $resolvedRepositoryRoot 'eng\release\PhotoSiftRelease.psm1') -Force
for ($index = 0; $index -lt 2; $index++) {
    Assert-RawPublishContentPolicy -RootPath $rawPublishPaths[$index]
    Assert-PhotoSiftReleaseContentPolicy -PackagePath $rawPublishPaths[$index]
    & $runtimeBoundaryPath `
        -DependencyManifestPath (Join-Path $rawPublishPaths[$index] 'PhotoSift.App.deps.json') `
        -SourceRootPath (Join-Path $resolvedRepositoryRoot 'src') `
        -FrameworkDependentRelease | Out-Null
}

$rawInventories = @(
    @(Get-ReleaseInventory -RootPath $rawPublishPaths[0]),
    @(Get-ReleaseInventory -RootPath $rawPublishPaths[1]))
Assert-ReleaseInventoriesEqual `
    -First $rawInventories[0] `
    -Second $rawInventories[1] `
    -Context 'Raw publish'

$nativePolicy = [pscustomobject][ordered]@{
    schemaVersion = 1
    nativePaths = $nativePaths
}
$nativePolicyJson = ($nativePolicy | ConvertTo-Json -Depth 5) + [System.Environment]::NewLine

$packageSpecifications = @(
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
$nativeSqliteIdentity = Get-NativeSqliteIdentity -LiteralPath (
    Join-Path $rawPublishPaths[0] 'e_sqlite3.dll')

$components = @(
    [pscustomobject][ordered]@{
        name = '.NET Host'
        version = $runtimeVersion
        shippedPaths = @('PhotoSift.App.exe')
        packageId = 'Microsoft.NETCore.App.Host.win-x64'
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
        version = $nativeSqliteIdentity.version
        versionSource = $nativeSqliteIdentity.versionSource
        sqliteSourceId = $nativeSqliteIdentity.sqliteSourceId
        sha256 = $nativeSqliteIdentity.sha256
        shippedPaths = @('e_sqlite3.dll')
        packageId = 'SQLitePCLRaw.lib.e_sqlite3'
        fileVersion = $nativeSqliteIdentity.fileVersion
        productVersion = $nativeSqliteIdentity.productVersion
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
    if (Test-Path -LiteralPath $candidatePath) {
        throw "Capture-only output must not contain a candidate path: $candidatePath"
    }
    Copy-ReleaseDirectoryContents `
        -SourcePath $rawPublishPaths[0] `
        -DestinationPath $provisionalPublishPath
    $captureEvidence = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'needs-license-inventory'
        sourceIdentity = $SourceIdentity
        sdkVersion = $observedSdkVersion
        runtimeIdentifier = $runtimeIdentifier
        netCoreRuntimeVersion = $runtimeVersion
        windowsDesktopRuntimeVersion = $runtimeVersion
        selfContained = $false
        rollForward = 'LatestPatch'
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
    }
}

$releaseDependencyInventory = Assert-ReleaseDependencyInventory `
    -InventoryPath (Join-Path $resolvedRepositoryRoot 'eng\release\release-dependencies.json') `
    -CapturedComponents $components
$releaseApprovalState = [string]$releaseDependencyInventory.components[0].redistributionApproval

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
        (Join-Path $resolvedRepositoryRoot 'eng\release\assets\README.txt'),
        (Join-Path $packagePaths[$index] 'README.txt'))
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
        -FrameworkDependentRelease | Out-Null
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
    sdkVersion = $observedSdkVersion
    runtimeIdentifier = $runtimeIdentifier
    netCoreRuntimeVersion = $runtimeVersion
    windowsDesktopRuntimeVersion = $runtimeVersion
    selfContained = $false
    rollForward = 'LatestPatch'
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
    status = $releaseApprovalState
    outputRoot = $resolvedOutputRoot
    candidatePath = $candidatePath
    candidateManifestSha256 = $candidateManifestSha256
    candidateDigest = $candidateDigest
    inventoriesEqual = $true
    comparedFields = @('path', 'length', 'sha256')
    fileCount = $packageInventories[0].Count
    nativePaths = $nativePaths
}
