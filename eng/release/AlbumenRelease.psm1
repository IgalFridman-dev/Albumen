Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:AlbumenReleaseLibraryMode = $true
. (Join-Path $PSScriptRoot 'assets\Verify-AlbumenRelease.ps1')
Remove-Variable -Name AlbumenReleaseLibraryMode -Scope Script

function Write-AlbumenReleaseUtf8NoBom {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($LiteralPath, $Content, $encoding)
}

function Assert-AlbumenReleaseContentPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$PackagePath
    )

    $resolvedPackagePath = [System.IO.Path]::GetFullPath($PackagePath)
    if (-not [System.IO.Directory]::Exists($resolvedPackagePath)) {
        throw "Release content directory does not exist: $resolvedPackagePath"
    }
    Assert-AlbumenReleaseHasNoReparsePoints -PackagePath $resolvedPackagePath
    Assert-AlbumenReleaseDirectoriesAllowed -PackagePath $resolvedPackagePath
    foreach ($file in @(Get-AlbumenReleaseFiles -PackagePath $resolvedPackagePath)) {
        Assert-AlbumenCanonicalReleasePath `
            -Path $file.Path `
            -PackagePath $resolvedPackagePath
        $forbiddenReason = Test-AlbumenForbiddenReleasePath -Path $file.Path
        if ($null -ne $forbiddenReason) {
            throw "Release content contains forbidden $forbiddenReason`: $($file.Path)"
        }
    }
}

function New-AlbumenReleaseManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$OutputPath
    )

    $resolvedPackagePath = [System.IO.Path]::GetFullPath($PackagePath)
    if (-not [System.IO.Directory]::Exists($resolvedPackagePath)) {
        throw "Release package directory does not exist: $resolvedPackagePath"
    }
    Assert-AlbumenReleaseHasNoReparsePoints -PackagePath $resolvedPackagePath
    Set-AlbumenApprovedNativePaths -PackagePath $resolvedPackagePath
    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $requiredOutputPath = Join-Path $resolvedPackagePath 'release-manifest.json'
    if (-not $resolvedOutputPath.Equals(
            $requiredOutputPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release manifest output must be the package-local path: $requiredOutputPath"
    }

    Assert-AlbumenReleaseDirectoriesAllowed -PackagePath $resolvedPackagePath
    $files = @(Get-AlbumenReleaseFiles -PackagePath $resolvedPackagePath)
    $requiredPaths = @(
        'Albumen.App.exe',
        'release-native-paths.json',
        'Start-Albumen.cmd',
        'THIRD-PARTY-NOTICES.md',
        'Verify-AlbumenRelease.ps1') + $script:AlbumenApprovedNativePaths
    $filePaths = @($files | ForEach-Object { $_.Path })
    foreach ($requiredPath in $requiredPaths) {
        if ($filePaths -cnotcontains $requiredPath) {
            throw "Release package is missing required payload: $requiredPath"
        }
    }

    $payload = @($files | ForEach-Object {
            Assert-AlbumenCanonicalReleasePath `
                -Path $_.Path `
                -PackagePath $resolvedPackagePath
            $forbiddenReason = Test-AlbumenForbiddenReleasePath -Path $_.Path
            if ($null -ne $forbiddenReason) {
                throw "Release package contains forbidden $forbiddenReason`: $($_.Path)"
            }
            Assert-AlbumenReleaseNativePathApproved `
                -Path $_.Path `
                -LiteralPath $_.FullName
            [pscustomobject][ordered]@{
                path = $_.Path
                length = [long]$_.Length
                sha256 = Get-AlbumenReleaseSha256 -LiteralPath $_.FullName
                kind = Get-AlbumenReleasePayloadKind -Path $_.Path
            }
        })
    $payloadByPath = @{}
    foreach ($entry in $payload) {
        $payloadByPath[$entry.path] = $entry
    }
    $sortedPayloadPaths = [string[]]@($payload | ForEach-Object { $_.path })
    [System.Array]::Sort($sortedPayloadPaths, [System.StringComparer]::Ordinal)
    $payload = [object[]]@($sortedPayloadPaths | ForEach-Object { $payloadByPath[$_] })
    $nativePaths = [string[]]@($payload |
        Where-Object { $_.kind -ceq 'native-runtime' } |
        ForEach-Object { $_.path })

    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        payload = $payload
        nativePaths = $nativePaths
    }
    $manifestJson = ($manifest | ConvertTo-Json -Depth 6) + [System.Environment]::NewLine
    Write-AlbumenReleaseUtf8NoBom -LiteralPath $resolvedOutputPath -Content $manifestJson
    $digestPath = Join-Path $resolvedPackagePath 'release-manifest.sha256'
    Write-AlbumenReleaseUtf8NoBom `
        -LiteralPath $digestPath `
        -Content ((Get-AlbumenReleaseSha256 -LiteralPath $resolvedOutputPath) + [System.Environment]::NewLine)

    Test-AlbumenPackageIntegrity -PackagePath $resolvedPackagePath | Out-Null
    return $manifest
}

function Test-AlbumenRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$PackagePath
    )

    return Test-AlbumenPackageIntegrity -PackagePath $PackagePath
}

Export-ModuleMember -Function `
    Assert-AlbumenReleaseContentPolicy, `
    New-AlbumenReleaseManifest, `
    Test-AlbumenRelease
