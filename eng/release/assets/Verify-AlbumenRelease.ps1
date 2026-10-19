[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$ApplicationArgument = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:AlbumenApprovedNativePaths = [string[]]@('coreclr.dll')

function Get-AlbumenReleaseSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $stream = [System.IO.File]::OpenRead($LiteralPath)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-AlbumenReleaseRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $packagePrefix = $PackagePath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $LiteralPath.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path escapes the package: $LiteralPath"
    }

    return $LiteralPath.Substring($packagePrefix.Length).Replace('\', '/')
}

function Assert-AlbumenReleaseHasNoReparsePoints {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $root = Get-Item -Force -LiteralPath $PackagePath
    if (($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release package root is a reparse point: $PackagePath"
    }

    foreach ($item in @(Get-ChildItem -Force -Recurse -LiteralPath $PackagePath)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release package contains a reparse point: $($item.FullName)"
        }
    }
}

function Assert-AlbumenCanonicalReleasePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -cne $Path.Trim()) {
        throw 'Release manifest path must be a nonempty canonical relative path.'
    }
    if ($Path.Contains('\')) {
        throw "Release manifest path uses an alternate separator: $Path"
    }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        throw "Release manifest path must not be rooted: $Path"
    }
    if ($Path.IndexOfAny([char[]]'<>:"|?*') -ge 0) {
        throw "Release manifest path contains an invalid character: $Path"
    }

    $segments = @($Path.Split('/'))
    if ($segments.Count -eq 0 -or @($segments | Where-Object {
                [string]::IsNullOrEmpty($_) -or $_ -ceq '.' -or $_ -ceq '..'
            }).Count -gt 0) {
        throw "Release manifest path contains a noncanonical segment: $Path"
    }

    $candidatePath = [System.IO.Path]::GetFullPath(
        (Join-Path $PackagePath ($Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
    $packagePrefix = $PackagePath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidatePath.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release manifest path escapes the package: $Path"
    }
}

function Set-AlbumenApprovedNativePaths {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $policyPath = Join-Path $PackagePath 'release-native-paths.json'
    if (-not [System.IO.File]::Exists($policyPath)) {
        throw 'Release package is missing release-native-paths.json.'
    }
    try {
        $policy = [System.IO.File]::ReadAllText($policyPath) | ConvertFrom-Json
    }
    catch {
        throw "Release native-path policy is not valid JSON: $($_.Exception.Message)"
    }
    Assert-AlbumenReleaseProperties `
        -Value $policy `
        -Expected @('schemaVersion', 'nativePaths') `
        -Context 'Release native-path policy'
    if (($policy.schemaVersion -isnot [int] -and
            $policy.schemaVersion -isnot [long]) -or
        $policy.schemaVersion -ne 1) {
        throw 'Release native-path policy schemaVersion must be the integer 1.'
    }
    if ($policy.nativePaths -isnot [System.Array]) {
        throw 'Release native-path policy nativePaths must be a JSON array.'
    }
    $paths = [string[]]@($policy.nativePaths)
    if ($paths.Count -eq 0) {
        throw 'Release native-path policy must contain the exact nonempty native closure.'
    }
    foreach ($path in $paths) {
        Assert-AlbumenCanonicalReleasePath -Path $path -PackagePath $PackagePath
    }
    Assert-AlbumenReleaseSortedUniquePaths `
        -Paths $paths `
        -Context 'Release native-path policy'
    $script:AlbumenApprovedNativePaths = $paths
}

function Test-AlbumenForbiddenReleasePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $normalized = '/' + $Path.Replace('\', '/').ToLowerInvariant() + '/'
    $leaf = [System.IO.Path]::GetFileName($Path).ToLowerInvariant()
    $extension = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
    $isApprovedLauncher = $Path -ceq 'Start-Albumen.cmd' -or
        $Path -ceq 'Verify-AlbumenRelease.ps1'
    $developmentExtensions = @(
        '.bat', '.c', '.cmd', '.cpp', '.cs', '.csproj', '.fs', '.fsproj',
        '.h', '.props', '.ps1', '.psd1', '.psm1', '.sh', '.sln', '.targets',
        '.vb', '.vbproj')

    if ($normalized -match '/models?/' -or
        $leaf -match '(^|[._-])(model|weights?|tokenizer)([._-]|$)' -or
        $extension -in @('.onnx', '.ort', '.safetensors', '.tflite')) {
        return 'model artifact'
    }
    if ($normalized -match '/(tests?|testresults?)/' -or
        $leaf -match '(^|[._-])(tests?|testhost)([._-]|$)') {
        return 'test artifact'
    }
    if ($normalized -match '/(corpus|corpora)/') {
        return 'test corpus'
    }
    if ($normalized -match '/(__pycache__|python|scripts?|tools?|exports?)/' -or
        $extension -in @('.py', '.pyc', '.pyo', '.whl') -or
        $leaf -match '^python(?:w|[0-9]+)?(?:[._-].*)?$' -or
        $leaf -match '^pyvenv(?:[._-].*)?$' -or
        $leaf -match '^pip(?:[0-9]+)?(?:[._-].*)?$' -or
        $leaf -match '^export(?:er|tool)?(?:[._-].*)?$' -or
        (-not $isApprovedLauncher -and $extension -in $developmentExtensions)) {
        return 'Python or development tooling artifact'
    }
    if ($normalized -match '/(cache|caches|\.cache|obj|bin)/') {
        return 'cache or development output'
    }
    if ($leaf -eq '.env' -or
        $leaf -match '(secret|credential)' -or
        $extension -in @('.pfx', '.p12', '.pem', '.key', '.snk', '.pdb')) {
        return 'development secret or debug artifact'
    }

    return $null
}

function Get-AlbumenReleasePayloadKind {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ($Path -ieq 'Albumen.App.exe') {
        return 'application'
    }
    if ($Path -ieq 'Start-Albumen.cmd' -or $Path -ieq 'Verify-AlbumenRelease.ps1') {
        return 'launcher'
    }
    if ($Path -ieq 'THIRD-PARTY-NOTICES.md' -or
        $Path.StartsWith('licenses/', [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($Path) -match '^(LICENSE|NOTICE)(\.|$)') {
        return 'notice'
    }

    if ($script:AlbumenApprovedNativePaths -icontains $Path) {
        return 'native-runtime'
    }

    return 'managed-runtime'
}

function Test-AlbumenNativePortableExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $stream = [System.IO.File]::OpenRead($LiteralPath)
    try {
        if ($stream.Length -lt 64) {
            return $false
        }
        $reader = New-Object System.IO.BinaryReader($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                return $false
            }

            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 24)) {
                return $true
            }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                return $true
            }

            $optionalHeaderOffset = $peOffset + 24
            $stream.Position = $optionalHeaderOffset
            $magic = $reader.ReadUInt16()
            if ($magic -eq 0x10B) {
                $dataDirectoryOffset = $optionalHeaderOffset + 96
            }
            elseif ($magic -eq 0x20B) {
                $dataDirectoryOffset = $optionalHeaderOffset + 112
            }
            else {
                return $true
            }

            $clrDirectoryOffset = $dataDirectoryOffset + (14 * 8)
            if ($clrDirectoryOffset -gt ($stream.Length - 8)) {
                return $true
            }
            $stream.Position = $clrDirectoryOffset
            $clrHeaderRva = $reader.ReadUInt32()
            $clrHeaderSize = $reader.ReadUInt32()
            return $clrHeaderRva -eq 0 -or $clrHeaderSize -eq 0
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-AlbumenReleaseNativePathApproved {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    if ($Path -ieq 'Albumen.App.exe' -or
        $script:AlbumenApprovedNativePaths -icontains $Path) {
        return
    }

    $extension = [System.IO.Path]::GetExtension($Path)
    if ($extension -ieq '.exe' -or
        (Test-AlbumenNativePortableExecutable -LiteralPath $LiteralPath)) {
        throw "Release package contains an unexpected native path outside the approved native set: $Path"
    }
}

function Assert-AlbumenReleaseDirectoriesAllowed {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    foreach ($directory in @(Get-ChildItem -Force -Directory -Recurse -LiteralPath $PackagePath)) {
        $relativePath = Get-AlbumenReleaseRelativePath `
            -PackagePath $PackagePath `
            -LiteralPath $directory.FullName
        $forbiddenReason = Test-AlbumenForbiddenReleasePath -Path $relativePath
        if ($null -ne $forbiddenReason) {
            throw "Release package contains forbidden $forbiddenReason directory: $relativePath"
        }
    }
}

function Assert-AlbumenReleaseProperties {
    param(
        [Parameter(Mandatory)]
        $Value,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Context
    )

    $actualNames = [string[]]@($Value.PSObject.Properties | ForEach-Object { $_.Name })
    $expectedNames = [string[]]@($Expected)
    [System.Array]::Sort($actualNames, [System.StringComparer]::Ordinal)
    [System.Array]::Sort($expectedNames, [System.StringComparer]::Ordinal)
    if (($actualNames -join [char]0) -cne ($expectedNames -join [char]0)) {
        throw "$Context has unexpected or missing properties."
    }
}

function Assert-AlbumenReleaseSortedUniquePaths {
    param(
        [Parameter(Mandatory)]
        [string[]]$Paths,

        [Parameter(Mandatory)]
        [string]$Context
    )

    $seen = [System.Collections.Generic.Dictionary[string, bool]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Paths) {
        if ($seen.ContainsKey($path)) {
            throw "$Context contains a duplicate or case-colliding path: $path"
        }
        $seen.Add($path, $true)
    }

    $sorted = [string[]]@($Paths)
    [System.Array]::Sort($sorted, [System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $Paths.Count; $index++) {
        if ($Paths[$index] -cne $sorted[$index]) {
            throw "$Context paths are not in canonical ordinal order."
        }
    }
}

function Get-AlbumenReleaseFiles {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $metadataNames = @('release-manifest.json', 'release-manifest.sha256')
    return @(Get-ChildItem -Force -File -Recurse -LiteralPath $PackagePath |
        ForEach-Object {
            $relativePath = Get-AlbumenReleaseRelativePath `
                -PackagePath $PackagePath `
                -LiteralPath $_.FullName
            if ($metadataNames -inotcontains $relativePath) {
                [pscustomobject]@{
                    Path = $relativePath
                    FullName = $_.FullName
                    Length = $_.Length
                }
            }
            elseif ($metadataNames -cnotcontains $relativePath) {
                throw "Release metadata uses a noncanonical path casing: $relativePath"
            }
        })
}

function Test-AlbumenPackageIntegrity {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $resolvedPackagePath = [System.IO.Path]::GetFullPath($PackagePath)
    if (-not [System.IO.Directory]::Exists($resolvedPackagePath)) {
        throw "Release package directory does not exist: $resolvedPackagePath"
    }
    Assert-AlbumenReleaseHasNoReparsePoints -PackagePath $resolvedPackagePath
    Set-AlbumenApprovedNativePaths -PackagePath $resolvedPackagePath
    Assert-AlbumenReleaseDirectoriesAllowed -PackagePath $resolvedPackagePath

    $manifestPath = Join-Path $resolvedPackagePath 'release-manifest.json'
    $digestPath = Join-Path $resolvedPackagePath 'release-manifest.sha256'
    if (-not [System.IO.File]::Exists($manifestPath) -or -not [System.IO.File]::Exists($digestPath)) {
        throw 'Release package is missing release-manifest.json or release-manifest.sha256.'
    }

    $digestText = [System.IO.File]::ReadAllText($digestPath)
    if ($digestText -notmatch '^[0-9A-F]{64}(\r?\n)?$') {
        throw 'Release manifest digest must contain exactly one uppercase SHA-256 value.'
    }
    $expectedDigest = $digestText.TrimEnd("`r", "`n")
    $actualDigest = Get-AlbumenReleaseSha256 -LiteralPath $manifestPath
    if ($expectedDigest -cne $actualDigest) {
        throw 'Release manifest SHA-256 digest does not match release-manifest.json.'
    }

    try {
        $manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    }
    catch {
        throw "Release manifest is not valid JSON: $($_.Exception.Message)"
    }
    if ($null -eq $manifest) {
        throw 'Release manifest must be a JSON object.'
    }
    Assert-AlbumenReleaseProperties `
        -Value $manifest `
        -Expected @('schemaVersion', 'payload', 'nativePaths') `
        -Context 'Release manifest'
    if (($manifest.schemaVersion -isnot [int] -and
            $manifest.schemaVersion -isnot [long]) -or
        $manifest.schemaVersion -ne 1) {
        throw 'Release manifest schemaVersion must be the integer 1.'
    }

    if ($manifest.payload -isnot [System.Array]) {
        throw 'Release manifest payload must be a JSON array.'
    }
    $payload = @($manifest.payload)
    if ($payload.Count -eq 0) {
        throw 'Release manifest payload must not be empty.'
    }
    $payloadPaths = [string[]]@($payload | ForEach-Object {
            if ($null -eq $_) {
                throw 'Release manifest payload contains a null entry.'
            }
            Assert-AlbumenReleaseProperties `
                -Value $_ `
                -Expected @('path', 'length', 'sha256', 'kind') `
                -Context 'Release manifest payload entry'
            if ($_.path -isnot [string]) {
                throw 'Release manifest payload path must be a string.'
            }
            Assert-AlbumenCanonicalReleasePath -Path $_.path -PackagePath $resolvedPackagePath
            if ($_.path -in @('release-manifest.json', 'release-manifest.sha256')) {
                throw "Release metadata must not appear in its own payload: $($_.path)"
            }
            $forbiddenReason = Test-AlbumenForbiddenReleasePath -Path $_.path
            if ($null -ne $forbiddenReason) {
                throw "Release payload contains forbidden $forbiddenReason`: $($_.path)"
            }
            if (($_.length -isnot [int] -and $_.length -isnot [long]) -or $_.length -lt 0) {
                throw "Release payload length must be a nonnegative integer: $($_.path)"
            }
            if ($_.sha256 -isnot [string] -or $_.sha256 -cnotmatch '^[0-9A-F]{64}$') {
                throw "Release payload SHA-256 must be 64 uppercase hex characters: $($_.path)"
            }
            if ($_.kind -isnot [string] -or $_.kind -notin @(
                    'application', 'managed-runtime', 'native-runtime', 'notice', 'launcher')) {
                throw "Release payload kind is invalid: $($_.path)"
            }
            $expectedKind = Get-AlbumenReleasePayloadKind -Path $_.path
            if ($_.kind -cne $expectedKind) {
                throw "Release payload kind does not match path policy for $($_.path); expected $expectedKind."
            }
            $_.path
        })
    Assert-AlbumenReleaseSortedUniquePaths -Paths $payloadPaths -Context 'Release payload'

    if ($manifest.nativePaths -isnot [System.Array]) {
        throw 'Release manifest nativePaths must be a JSON array.'
    }
    $nativePaths = [string[]]@($manifest.nativePaths)
    foreach ($nativePath in $nativePaths) {
        if ($nativePath -isnot [string]) {
            throw 'Release manifest nativePaths entries must be strings.'
        }
        Assert-AlbumenCanonicalReleasePath -Path $nativePath -PackagePath $resolvedPackagePath
    }
    Assert-AlbumenReleaseSortedUniquePaths -Paths $nativePaths -Context 'Release nativePaths'
    if (($nativePaths -join [char]0) -cne
        ($script:AlbumenApprovedNativePaths -join [char]0)) {
        throw 'Release nativePaths must exactly match the independently approved native path set.'
    }
    $expectedNativePaths = [string[]]@($payload |
        Where-Object { $_.kind -ceq 'native-runtime' } |
        ForEach-Object { $_.path })
    if (($nativePaths -join [char]0) -cne ($expectedNativePaths -join [char]0)) {
        throw 'Release nativePaths must exactly match the native-runtime payload membership.'
    }

    $files = @(Get-AlbumenReleaseFiles -PackagePath $resolvedPackagePath)
    $filePaths = [string[]]@($files | ForEach-Object { $_.Path })
    $sortedFilePaths = [string[]]@($filePaths)
    [System.Array]::Sort($sortedFilePaths, [System.StringComparer]::Ordinal)
    Assert-AlbumenReleaseSortedUniquePaths `
        -Paths $sortedFilePaths `
        -Context 'On-disk release payload'
    $fileByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        Assert-AlbumenReleaseNativePathApproved `
            -Path $file.Path `
            -LiteralPath $file.FullName
        $fileByPath.Add($file.Path, $file)
    }
    if ($files.Count -ne $payload.Count) {
        throw "Release payload set differs from disk; manifest has $($payload.Count) files and disk has $($files.Count)."
    }

    foreach ($entry in $payload) {
        if (-not $fileByPath.ContainsKey($entry.path)) {
            throw "Release payload file is missing: $($entry.path)"
        }
        $file = $fileByPath[$entry.path]
        if ($file.Path -cne $entry.path) {
            throw "Release payload path casing differs from disk: $($entry.path)"
        }
        if ([long]$file.Length -ne [long]$entry.length) {
            throw "Release payload length mismatch: $($entry.path)"
        }
        $actualHash = Get-AlbumenReleaseSha256 -LiteralPath $file.FullName
        if ($actualHash -cne $entry.sha256) {
            throw "Release payload SHA-256 hash mismatch: $($entry.path)"
        }
    }

    return $manifest
}

$libraryMode = Get-Variable -Name AlbumenReleaseLibraryMode -ErrorAction SilentlyContinue
if ($null -eq $libraryMode -or -not [bool]$libraryMode.Value) {
    try {
        Test-AlbumenPackageIntegrity -PackagePath $PSScriptRoot | Out-Null
        $applicationPath = Join-Path $PSScriptRoot 'Albumen.App.exe'
        # A pipeline makes PowerShell wait for a GUI app and populate LASTEXITCODE.
        & $applicationPath @ApplicationArgument | Out-Host
        $applicationExitCode = $LASTEXITCODE
        if ($null -eq $applicationExitCode) {
            $applicationExitCode = 0
        }
        exit $applicationExitCode
    }
    catch {
        [System.Console]::Error.WriteLine("Albumen release verification failed: $($_.Exception.Message)")
        exit 1
    }
}
