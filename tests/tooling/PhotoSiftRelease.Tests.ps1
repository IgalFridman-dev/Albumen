$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $repositoryRoot 'eng\release\PhotoSiftRelease.psm1'
$checkerPath = Join-Path $repositoryRoot 'eng\release\Test-PhotoSiftRelease.ps1'
$launcherAssetPath = Join-Path $repositoryRoot 'eng\release\assets\Start-PhotoSift.cmd'
$verifierAssetPath = Join-Path $repositoryRoot 'eng\release\assets\Verify-PhotoSiftRelease.ps1'

foreach ($requiredPath in @($modulePath, $checkerPath, $launcherAssetPath, $verifierAssetPath)) {
    if (-not [System.IO.File]::Exists($requiredPath)) {
        throw "Required release implementation does not exist: $requiredPath"
    }
}

Import-Module -Name $modulePath -Force

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'PhotoSift-Release-Tests-' + [System.Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:passed = 0

function Assert-Equal {
    param($Expected, $Actual, [string]$Because)

    if ($Expected -cne $Actual) {
        throw "Expected '$Expected' but got '$Actual': $Because"
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Because)

    if (-not $Condition) {
        throw "Expected true: $Because"
    }
}

function Assert-ThrowsLike {
    param(
        [scriptblock]$Action,
        [string]$Pattern,
        [string]$Because
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected failure matching '$Pattern' but got '$($_.Exception.Message)': $Because"
        }
        $script:passed++
        return
    }

    throw "Expected failure matching '$Pattern': $Because"
}

function Test-CorrectionRejection {
    param(
        [scriptblock]$Action,
        [string]$Pattern,
        [string]$Because,
        [System.Collections.Generic.List[string]]$Failures
    )

    try {
        & $Action
        $Failures.Add("ACCEPTED: $Because")
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            $Failures.Add(
                "WRONG FAILURE '$($_.Exception.Message)' (expected '$Pattern'): $Because")
        }
        else {
            $script:passed++
        }
    }
}

function Get-Sha256 {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
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

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Write-ManifestAndDigest {
    param([string]$PackagePath, $Manifest)

    $manifestPath = Join-Path $PackagePath 'release-manifest.json'
    $digestPath = Join-Path $PackagePath 'release-manifest.sha256'
    Write-Utf8NoBom -Path $manifestPath -Content ($Manifest | ConvertTo-Json -Depth 8)
    Write-Utf8NoBom -Path $digestPath -Content ((Get-Sha256 -Path $manifestPath) + [System.Environment]::NewLine)
}

function Read-Manifest {
    param([string]$PackagePath)

    return Get-Content -Raw -LiteralPath (Join-Path $PackagePath 'release-manifest.json') |
        ConvertFrom-Json
}

function New-CleanPackage {
    param([string]$Name)

    $packagePath = Join-Path $testRoot $Name
    [System.IO.Directory]::CreateDirectory($packagePath) | Out-Null
    [System.IO.File]::Copy($launcherAssetPath, (Join-Path $packagePath 'Start-PhotoSift.cmd'))
    [System.IO.File]::Copy($verifierAssetPath, (Join-Path $packagePath 'Verify-PhotoSiftRelease.ps1'))
    Write-Utf8NoBom -Path (Join-Path $packagePath 'PhotoSift.App.exe') -Content 'synthetic application'
    [System.IO.Directory]::CreateDirectory((Join-Path $packagePath 'runtime')) | Out-Null
    Write-Utf8NoBom -Path (Join-Path $packagePath 'runtime\PhotoSift.Managed.dll') -Content 'managed fixture'
    Write-Utf8NoBom -Path (Join-Path $packagePath 'coreclr.dll') -Content 'named native fixture'
    Write-Utf8NoBom -Path (Join-Path $packagePath 'THIRD-PARTY-NOTICES.md') -Content 'fixture notice'
    New-PhotoSiftReleaseManifest `
        -PackagePath $packagePath `
        -OutputPath (Join-Path $packagePath 'release-manifest.json') | Out-Null
    return $packagePath
}

function Copy-Package {
    param([string]$SourcePath, [string]$Name)

    $destination = Join-Path $testRoot $Name
    Copy-Item -Recurse -LiteralPath $SourcePath -Destination $destination
    return $destination
}

function Invoke-ReleaseChecker {
    param([string]$PackagePath, [string]$Name)

    $stdoutPath = Join-Path $testRoot ($Name + '-stdout.txt')
    $stderrPath = Join-Path $testRoot ($Name + '-stderr.txt')
    $engineArguments = @('-NoProfile')
    if ([System.IO.Path]::GetFileName((Get-Process -Id $PID).Path) -ieq 'powershell.exe') {
        $engineArguments += @('-ExecutionPolicy', 'Bypass')
    }
    $process = Start-Process `
        -FilePath (Get-Process -Id $PID).Path `
        -ArgumentList ($engineArguments + @('-File', $checkerPath, '-PackagePath', $PackagePath)) `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = @(
            Get-Content -Raw -LiteralPath $stdoutPath
            Get-Content -Raw -LiteralPath $stderrPath
        ) -join [System.Environment]::NewLine
    }
}

function Invoke-PackageVerifier {
    param(
        [string]$PackagePath,
        [string]$Name,
        [string[]]$ApplicationArguments = @()
    )

    $stdoutPath = Join-Path $testRoot ($Name + '-stdout.txt')
    $stderrPath = Join-Path $testRoot ($Name + '-stderr.txt')
    $engineArguments = @('-NoProfile')
    if ([System.IO.Path]::GetFileName((Get-Process -Id $PID).Path) -ieq 'powershell.exe') {
        $engineArguments += @('-ExecutionPolicy', 'Bypass')
    }
    $argumentList = $engineArguments + @(
        '-File',
        ('"' + (Join-Path $PackagePath 'Verify-PhotoSiftRelease.ps1') + '"')) + $ApplicationArguments
    $process = Start-Process `
        -FilePath (Get-Process -Id $PID).Path `
        -ArgumentList $argumentList `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = @(
            Get-Content -Raw -LiteralPath $stdoutPath
            Get-Content -Raw -LiteralPath $stderrPath
        ) -join [System.Environment]::NewLine
    }
}

function Invoke-PackageLauncher {
    param(
        [string]$PackagePath,
        [string]$Name,
        [string[]]$ApplicationArguments = @()
    )

    $stdoutPath = Join-Path $testRoot ($Name + '-stdout.txt')
    $stderrPath = Join-Path $testRoot ($Name + '-stderr.txt')
    $process = Start-Process `
        -FilePath (Join-Path $PackagePath 'Start-PhotoSift.cmd') `
        -ArgumentList $ApplicationArguments `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = @(
            Get-Content -Raw -LiteralPath $stdoutPath
            Get-Content -Raw -LiteralPath $stderrPath
        ) -join [System.Environment]::NewLine
    }
}

try {
    $cleanPackage = New-CleanPackage -Name 'clean'
    Test-PhotoSiftRelease -PackagePath $cleanPackage | Out-Null
    $cleanCheckerResult = Invoke-ReleaseChecker -PackagePath $cleanPackage -Name 'clean-checker'
    Assert-Equal 0 $cleanCheckerResult.ExitCode 'the public checker script must accept the canonical package'
    $script:passed++

    $manifest = Read-Manifest -PackagePath $cleanPackage
    Assert-Equal 1 $manifest.schemaVersion 'the schema version must be exact'
    Assert-Equal `
        'PhotoSift.App.exe,Start-PhotoSift.cmd,THIRD-PARTY-NOTICES.md,Verify-PhotoSiftRelease.ps1,coreclr.dll,runtime/PhotoSift.Managed.dll' `
        (($manifest.payload | ForEach-Object { $_.path }) -join ',') `
        'payload paths must be canonical slash-separated ordinal-sorted paths'
    Assert-Equal `
        'application,launcher,notice,launcher,native-runtime,managed-runtime' `
        (($manifest.payload | ForEach-Object { $_.kind }) -join ',') `
        'each clean payload path must have its exact bounded kind'
    Assert-Equal 'coreclr.dll' ($manifest.nativePaths -join ',') 'the named native path must be explicit'
    foreach ($entry in $manifest.payload) {
        Assert-True ($entry.length -ge 0) "payload length must be nonnegative for $($entry.path)"
        Assert-True ($entry.sha256 -cmatch '^[0-9A-F]{64}$') "payload SHA-256 must be uppercase hex for $($entry.path)"
    }
    $script:passed++

    $correctionFailures = [System.Collections.Generic.List[string]]::new()
    foreach ($nativeBypass in @(
            @{ Name = 'unexpected-executable'; Path = 'unexpected.exe'; NativePe = $false },
            @{ Name = 'unexpected-native-pe'; Path = 'unexpected.dll'; NativePe = $true }
        )) {
        $fixture = Copy-Package -SourcePath $cleanPackage -Name $nativeBypass.Name
        $artifactPath = Join-Path $fixture $nativeBypass.Path
        if ($nativeBypass.NativePe) {
            [System.IO.File]::Copy((Join-Path $env:SystemRoot 'System32\cmd.exe'), $artifactPath)
        }
        else {
            Write-Utf8NoBom -Path $artifactPath -Content 'unexpected executable fixture'
        }
        Test-CorrectionRejection `
            -Action {
                New-PhotoSiftReleaseManifest `
                    -PackagePath $fixture `
                    -OutputPath (Join-Path $fixture 'release-manifest.json') | Out-Null
            } `
            -Pattern 'approved native|unexpected native' `
            -Because "an unapproved native-like payload must be rejected: $($nativeBypass.Path)" `
            -Failures $correctionFailures
    }
    $missingApprovedNative = Copy-Package `
        -SourcePath $cleanPackage `
        -Name 'missing-approved-native'
    Remove-Item -LiteralPath (Join-Path $missingApprovedNative 'coreclr.dll')
    Test-CorrectionRejection `
        -Action {
            New-PhotoSiftReleaseManifest `
                -PackagePath $missingApprovedNative `
                -OutputPath (Join-Path $missingApprovedNative 'release-manifest.json') | Out-Null
        } `
        -Pattern 'approved native|required payload' `
        -Because 'the independently approved native path set must be present exactly' `
        -Failures $correctionFailures
    foreach ($toolingBypass in @('python.exe', 'python311.dll', 'export.exe')) {
        $fixture = Copy-Package `
            -SourcePath $cleanPackage `
            -Name ('root-tooling-' + $toolingBypass.Replace('.', '-'))
        Write-Utf8NoBom `
            -Path (Join-Path $fixture $toolingBypass) `
            -Content 'root-level tooling fixture'
        Test-CorrectionRejection `
            -Action {
                New-PhotoSiftReleaseManifest `
                    -PackagePath $fixture `
                    -OutputPath (Join-Path $fixture 'release-manifest.json') | Out-Null
            } `
            -Pattern 'Python|development tooling|export' `
            -Because "root-level Python/export tooling must be rejected: $toolingBypass" `
            -Failures $correctionFailures
    }
    if ($correctionFailures.Count -gt 0) {
        throw "Correction rejection failures:`n$($correctionFailures -join [System.Environment]::NewLine)"
    }

    foreach ($case in @(
            @{ Name = 'duplicate-path'; Pattern = 'duplicate|collision'; Mutate = {
                    param($value)
                    $value.payload = @($value.payload) + @($value.payload[0])
                } },
            @{ Name = 'case-collision'; Pattern = 'duplicate|collision'; Mutate = {
                    param($value)
                    $copy = $value.payload[0].PSObject.Copy()
                    $copy.path = $copy.path.ToUpperInvariant()
                    $value.payload = @($value.payload) + @($copy)
                } },
            @{ Name = 'parent-segment'; Pattern = 'canonical|segment|path'; Mutate = {
                    param($value)
                    $value.payload[0].path = '../PhotoSift.App.exe'
                } },
            @{ Name = 'rooted-path'; Pattern = 'rooted|canonical|path'; Mutate = {
                    param($value)
                    $value.payload[0].path = 'C:/PhotoSift.App.exe'
                } },
            @{ Name = 'alternate-separator'; Pattern = 'separator|canonical|path'; Mutate = {
                    param($value)
                    $value.payload[-1].path = 'runtime\PhotoSift.Managed.dll'
                } },
            @{ Name = 'native-paths-not-array'; Pattern = 'array'; Mutate = {
                    param($value)
                    $value.nativePaths = $value.nativePaths[0]
                } }
        )) {
        $fixture = Copy-Package -SourcePath $cleanPackage -Name $case.Name
        $caseManifest = Read-Manifest -PackagePath $fixture
        & $case.Mutate $caseManifest
        Write-ManifestAndDigest -PackagePath $fixture -Manifest $caseManifest
        Assert-ThrowsLike `
            -Action { Test-PhotoSiftRelease -PackagePath $fixture | Out-Null } `
            -Pattern $case.Pattern `
            -Because "the $($case.Name) manifest must be rejected before any payload file is opened"
    }

    $malformedDigest = Copy-Package -SourcePath $cleanPackage -Name 'malformed-digest'
    Write-Utf8NoBom -Path (Join-Path $malformedDigest 'release-manifest.sha256') -Content 'not-a-digest'
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $malformedDigest | Out-Null } `
        -Pattern 'digest|SHA-256' `
        -Because 'a malformed detached digest must fail closed'

    $missing = Copy-Package -SourcePath $cleanPackage -Name 'missing'
    Remove-Item -LiteralPath (Join-Path $missing 'runtime\PhotoSift.Managed.dll')
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $missing | Out-Null } `
        -Pattern 'missing|set|payload' `
        -Because 'a missing payload file must be rejected'

    $extra = Copy-Package -SourcePath $cleanPackage -Name 'extra'
    Write-Utf8NoBom -Path (Join-Path $extra 'extra.txt') -Content 'extra'
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $extra | Out-Null } `
        -Pattern 'extra|set|payload' `
        -Because 'an extra on-disk file must be rejected'

    $lengthMutation = Copy-Package -SourcePath $cleanPackage -Name 'length-mutation'
    Add-Content -LiteralPath (Join-Path $lengthMutation 'PhotoSift.App.exe') -Value 'different length'
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $lengthMutation | Out-Null } `
        -Pattern 'length' `
        -Because 'a changed payload length must be rejected'

    $hashMutation = Copy-Package -SourcePath $cleanPackage -Name 'hash-mutation'
    $hashPath = Join-Path $hashMutation 'PhotoSift.App.exe'
    $hashBytes = [System.IO.File]::ReadAllBytes($hashPath)
    $hashBytes[0] = $hashBytes[0] -bxor 1
    [System.IO.File]::WriteAllBytes($hashPath, $hashBytes)
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $hashMutation | Out-Null } `
        -Pattern 'hash|SHA-256' `
        -Because 'same-length payload corruption must be rejected'

    foreach ($forbidden in @(
            @{ Name = 'model'; Path = 'models\weights.onnx' },
            @{ Name = 'test'; Path = 'PhotoSift.App.Tests.dll' },
            @{ Name = 'corpus'; Path = 'corpus\sample.jpg' },
            @{ Name = 'python'; Path = 'tools\export.py' },
            @{ Name = 'cache'; Path = 'cache\index.db' },
            @{ Name = 'secret'; Path = '.env' }
        )) {
        $fixture = Copy-Package -SourcePath $cleanPackage -Name ('forbidden-' + $forbidden.Name)
        $artifactPath = Join-Path $fixture $forbidden.Path
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($artifactPath)) | Out-Null
        Write-Utf8NoBom -Path $artifactPath -Content 'forbidden fixture'
        Assert-ThrowsLike `
            -Action {
                New-PhotoSiftReleaseManifest `
                    -PackagePath $fixture `
                    -OutputPath (Join-Path $fixture 'release-manifest.json') | Out-Null
            } `
            -Pattern 'forbidden|model|test|corpus|Python|cache|secret|development' `
            -Because "the $($forbidden.Name) artifact must never enter a release manifest"
    }

    $emptyModelDirectory = Copy-Package -SourcePath $cleanPackage -Name 'forbidden-empty-model-directory'
    [System.IO.Directory]::CreateDirectory((Join-Path $emptyModelDirectory 'models')) | Out-Null
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $emptyModelDirectory | Out-Null } `
        -Pattern 'forbidden|model' `
        -Because 'an empty model directory must not evade the recursive release-content policy'

    $reparseFixture = Copy-Package -SourcePath $cleanPackage -Name 'reparse'
    $reparseTarget = Join-Path $testRoot 'reparse-target'
    [System.IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    $reparsePath = Join-Path $reparseFixture 'linked'
    New-Item -ItemType Junction -Path $reparsePath -Target $reparseTarget | Out-Null
    Assert-ThrowsLike `
        -Action { Test-PhotoSiftRelease -PackagePath $reparseFixture | Out-Null } `
        -Pattern 'reparse' `
        -Because 'every package reparse point must be rejected before traversal'
    Remove-Item -Force -LiteralPath $reparsePath

    $payloadCorruption = Copy-Package -SourcePath $cleanPackage -Name 'payload-only-corruption'
    $payloadManifestPath = Join-Path $payloadCorruption 'release-manifest.json'
    $payloadDigestPath = Join-Path $payloadCorruption 'release-manifest.sha256'
    $manifestHashBefore = Get-Sha256 -Path $payloadManifestPath
    $digestHashBefore = Get-Sha256 -Path $payloadDigestPath
    Add-Content -LiteralPath (Join-Path $payloadCorruption 'PhotoSift.App.exe') -Value 'corrupt'
    $payloadResult = Invoke-ReleaseChecker -PackagePath $payloadCorruption -Name 'payload-only-corruption'
    Assert-True ($payloadResult.ExitCode -ne 0) 'ordinary payload-only corruption must fail supported verification'
    Assert-Equal $manifestHashBefore (Get-Sha256 -Path $payloadManifestPath) 'verification must never regenerate the manifest from corrupt payload'
    Assert-Equal $digestHashBefore (Get-Sha256 -Path $payloadDigestPath) 'verification must never regenerate the digest from corrupt payload'
    $script:passed++

    $manifestCorruption = Copy-Package -SourcePath $cleanPackage -Name 'manifest-only-corruption'
    $manifestOnlyPath = Join-Path $manifestCorruption 'release-manifest.json'
    $manifestOnlyDigestPath = Join-Path $manifestCorruption 'release-manifest.sha256'
    $manifestOnlyBytes = [System.IO.File]::ReadAllBytes($manifestOnlyPath)
    $manifestOnlyBytes[0] = $manifestOnlyBytes[0] -bxor 1
    [System.IO.File]::WriteAllBytes($manifestOnlyPath, $manifestOnlyBytes)
    $manifestCorruptHashBefore = Get-Sha256 -Path $manifestOnlyPath
    $manifestDigestHashBefore = Get-Sha256 -Path $manifestOnlyDigestPath
    $manifestResult = Invoke-ReleaseChecker -PackagePath $manifestCorruption -Name 'manifest-only-corruption'
    Assert-True ($manifestResult.ExitCode -ne 0) 'manifest-only corruption must fail detached-digest verification'
    Assert-Equal $manifestCorruptHashBefore (Get-Sha256 -Path $manifestOnlyPath) 'verification must not rewrite a corrupt manifest'
    Assert-Equal $manifestDigestHashBefore (Get-Sha256 -Path $manifestOnlyDigestPath) 'verification must not rewrite its detached digest'
    $script:passed++

    $launchFixture = Copy-Package -SourcePath $cleanPackage -Name 'launch'
    [System.IO.File]::Copy(
        (Join-Path $env:SystemRoot 'System32\cmd.exe'),
        (Join-Path $launchFixture 'PhotoSift.App.exe'),
        $true)
    New-PhotoSiftReleaseManifest `
        -PackagePath $launchFixture `
        -OutputPath (Join-Path $launchFixture 'release-manifest.json') | Out-Null
    $markerPath = Join-Path $testRoot 'application-launched.txt'
    Add-Content -LiteralPath (Join-Path $launchFixture 'runtime\PhotoSift.Managed.dll') -Value 'corrupt'
    $blockedLaunch = Invoke-PackageVerifier `
        -PackagePath $launchFixture `
        -Name 'blocked-launch' `
        -ApplicationArguments @('/d', '/c', ('"echo launched>' + $markerPath + ' & exit /b 7"'))
    Assert-True ($blockedLaunch.ExitCode -ne 0) 'the package verifier must reject corruption'
    Assert-True (-not [System.IO.File]::Exists($markerPath)) 'the application must not start before complete verification'
    $script:passed++

    Set-Content -NoNewline -LiteralPath (Join-Path $launchFixture 'runtime\PhotoSift.Managed.dll') -Value 'managed fixture'
    New-PhotoSiftReleaseManifest `
        -PackagePath $launchFixture `
        -OutputPath (Join-Path $launchFixture 'release-manifest.json') | Out-Null
    $successfulLaunch = Invoke-PackageVerifier `
        -PackagePath $launchFixture `
        -Name 'successful-launch' `
        -ApplicationArguments @('/d', '/c', ('"echo launched>' + $markerPath + ' & echo benign 1>&2 & exit /b 7"'))
    Assert-Equal 7 $successfulLaunch.ExitCode 'the verifier must propagate the verified application exit code despite native stderr'
    Assert-True ([System.IO.File]::Exists($markerPath)) 'the application must start after complete verification'
    $script:passed++

    $launcherResult = Invoke-PackageLauncher `
        -PackagePath $launchFixture `
        -Name 'supported-launcher' `
        -ApplicationArguments @('/d', '/c', '"exit /b 9"')
    Assert-Equal 9 $launcherResult.ExitCode 'the supported batch launcher must forward arguments and propagate the verified application exit code'
    $script:passed++

    Write-Output "Passed: $script:passed Failed: 0"
}
finally {
    if ([System.IO.Directory]::Exists($testRoot)) {
        Remove-Item -Recurse -Force -LiteralPath $testRoot -ErrorAction SilentlyContinue
    }
}
