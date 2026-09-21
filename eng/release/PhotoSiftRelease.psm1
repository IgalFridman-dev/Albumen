Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:PhotoSiftReleaseLibraryMode = $true
. (Join-Path $PSScriptRoot 'assets\Verify-PhotoSiftRelease.ps1')
Remove-Variable -Name PhotoSiftReleaseLibraryMode -Scope Script

function Write-PhotoSiftReleaseUtf8NoBom {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($LiteralPath, $Content, $encoding)
}

function New-PhotoSiftReleaseManifest {
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
    Set-PhotoSiftApprovedNativePaths -PackagePath $resolvedPackagePath
    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $requiredOutputPath = Join-Path $resolvedPackagePath 'release-manifest.json'
    if (-not $resolvedOutputPath.Equals(
            $requiredOutputPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release manifest output must be the package-local path: $requiredOutputPath"
    }

    Assert-PhotoSiftReleaseHasNoReparsePoints -PackagePath $resolvedPackagePath
    Assert-PhotoSiftReleaseDirectoriesAllowed -PackagePath $resolvedPackagePath
    $files = @(Get-PhotoSiftReleaseFiles -PackagePath $resolvedPackagePath)
    $requiredPaths = @(
        'PhotoSift.App.exe',
        'release-native-paths.json',
        'Start-PhotoSift.cmd',
        'THIRD-PARTY-NOTICES.md',
        'Verify-PhotoSiftRelease.ps1') + $script:PhotoSiftApprovedNativePaths
    $filePaths = @($files | ForEach-Object { $_.Path })
    foreach ($requiredPath in $requiredPaths) {
        if ($filePaths -cnotcontains $requiredPath) {
            throw "Release package is missing required payload: $requiredPath"
        }
    }

    $payload = @($files | ForEach-Object {
            Assert-PhotoSiftCanonicalReleasePath `
                -Path $_.Path `
                -PackagePath $resolvedPackagePath
            $forbiddenReason = Test-PhotoSiftForbiddenReleasePath -Path $_.Path
            if ($null -ne $forbiddenReason) {
                throw "Release package contains forbidden $forbiddenReason`: $($_.Path)"
            }
            Assert-PhotoSiftReleaseNativePathApproved `
                -Path $_.Path `
                -LiteralPath $_.FullName
            [pscustomobject][ordered]@{
                path = $_.Path
                length = [long]$_.Length
                sha256 = Get-PhotoSiftReleaseSha256 -LiteralPath $_.FullName
                kind = Get-PhotoSiftReleasePayloadKind -Path $_.Path
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
    Write-PhotoSiftReleaseUtf8NoBom -LiteralPath $resolvedOutputPath -Content $manifestJson
    $digestPath = Join-Path $resolvedPackagePath 'release-manifest.sha256'
    Write-PhotoSiftReleaseUtf8NoBom `
        -LiteralPath $digestPath `
        -Content ((Get-PhotoSiftReleaseSha256 -LiteralPath $resolvedOutputPath) + [System.Environment]::NewLine)

    Test-PhotoSiftPackageIntegrity -PackagePath $resolvedPackagePath | Out-Null
    return $manifest
}

function Test-PhotoSiftRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$PackagePath
    )

    return Test-PhotoSiftPackageIntegrity -PackagePath $PackagePath
}

Export-ModuleMember -Function New-PhotoSiftReleaseManifest, Test-PhotoSiftRelease
