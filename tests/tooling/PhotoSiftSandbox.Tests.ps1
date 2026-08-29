$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $repositoryRoot 'eng\sandbox\PhotoSiftSandbox.psm1'

Import-Module $modulePath -Force

$script:passed = 0

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Because
    )

    if ($Expected -ne $Actual) {
        throw "Expected '$Expected' but got '$Actual': $Because"
    }

    $script:passed++
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Operation,

        [Parameter(Mandatory)]
        [string]$Because
    )

    try {
        & $Operation
    }
    catch {
        $script:passed++
        return
    }

    throw "Expected operation to throw: $Because"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-Sandbox-Tooling-Tests'
$sourcePath = Join-Path $testRoot 'source & input'
$sdkPath = Join-Path $testRoot 'sdk'
$packagesPath = Join-Path $testRoot 'packages'
$outputPath = Join-Path $testRoot 'output'

foreach ($path in $sourcePath, $sdkPath, $packagesPath, $outputPath) {
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
}

$configuration = New-PhotoSiftSandboxConfiguration `
    -SourcePath $sourcePath `
    -SdkPath $sdkPath `
    -PackagesPath $packagesPath `
    -OutputPath $outputPath `
    -Stage Architecture

[xml]$xml = $configuration
Assert-Equal 'Disable' $xml.Configuration.Networking 'networking must be disabled'
Assert-Equal 'Disable' $xml.Configuration.ClipboardRedirection 'clipboard must be disabled'
Assert-Equal 'Disable' $xml.Configuration.PrinterRedirection 'printer redirection must be disabled'
Assert-Equal 'Disable' $xml.Configuration.AudioInput 'audio input must be disabled'
Assert-Equal 'Disable' $xml.Configuration.VideoInput 'video input must be disabled'
Assert-Equal 'Disable' $xml.Configuration.VGpu 'vGPU must be disabled'

$mappings = @($xml.Configuration.MappedFolders.MappedFolder)
Assert-Equal 4 $mappings.Count 'exactly four host folders may be mapped'
Assert-Equal 'true' $mappings[0].ReadOnly.ToLowerInvariant() 'source mapping must be read-only'
Assert-Equal 'true' $mappings[1].ReadOnly.ToLowerInvariant() 'SDK mapping must be read-only'
Assert-Equal 'true' $mappings[2].ReadOnly.ToLowerInvariant() 'package mapping must be read-only'
Assert-Equal 'false' $mappings[3].ReadOnly.ToLowerInvariant() 'output mapping must be writable'
Assert-Equal $sourcePath ([string]$mappings[0].HostFolder) 'host path must round-trip through XML escaping'

if ($xml.Configuration.LogonCommand.Command -notmatch '-Stage Architecture') {
    throw 'Guest logon command did not contain the Architecture stage.'
}
$script:passed++

$coreConfiguration = New-PhotoSiftSandboxConfiguration `
    -SourcePath $sourcePath `
    -SdkPath $sdkPath `
    -PackagesPath $packagesPath `
    -OutputPath $outputPath `
    -Stage Core
if ($coreConfiguration -notmatch '-Stage Core') {
    throw 'Guest logon command did not contain the Core stage.'
}
$script:passed++

$missingSummary = Join-Path $outputPath 'missing-summary.json'
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $missingSummary -ExpectedStage Architecture
} 'missing evidence must be rejected'

$failedSummary = Join-Path $outputPath 'failed-summary.json'
@{
    success = $false
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $failedSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $failedSummary -ExpectedStage Architecture
} 'unsuccessful evidence must be rejected'

$wrongStageSummary = Join-Path $outputPath 'wrong-stage-summary.json'
@{
    success = $true
    stage = 'Full'
    networkingDisabled = $true
    commands = @()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $wrongStageSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $wrongStageSummary -ExpectedStage Architecture
} 'stage-mismatched evidence must be rejected'

$failedCommandSummary = Join-Path $outputPath 'failed-command-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test'; exitCode = 1 })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $failedCommandSummary -Encoding utf8
Assert-Throws {
    Assert-PhotoSiftSandboxResult -SummaryPath $failedCommandSummary -ExpectedStage Architecture
} 'nonzero guest commands must be rejected'

$passingSummary = Join-Path $outputPath 'passing-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'test'; exitCode = 0 })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $passingSummary -Encoding utf8
Assert-PhotoSiftSandboxResult -SummaryPath $passingSummary -ExpectedStage Architecture | Out-Null
$script:passed++

$acceptedNonzeroSummary = Join-Path $outputPath 'accepted-nonzero-summary.json'
@{
    success = $true
    stage = 'Architecture'
    networkingDisabled = $true
    commands = @(@{ name = 'copy'; exitCode = 1; succeeded = $true })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $acceptedNonzeroSummary -Encoding utf8
Assert-PhotoSiftSandboxResult -SummaryPath $acceptedNonzeroSummary -ExpectedStage Architecture | Out-Null
$script:passed++

$commandProcessor = Join-Path $env:SystemRoot 'System32\cmd.exe'
$processResult = Invoke-PhotoSiftProcess `
    -FilePath $commandProcessor `
    -Arguments @('/d', '/c', '1>&2 echo non-fatal-warning & exit /b 0')
Assert-Equal 0 $processResult.ExitCode 'stderr must not turn a successful native process into a PowerShell error'
if ($processResult.StandardError -notmatch 'non-fatal-warning') {
    throw 'Expected native stderr to be captured as evidence.'
}
$script:passed++

$workingDirectoryProbe = Join-Path $testRoot 'working-directory-probe'
[System.IO.Directory]::CreateDirectory($workingDirectoryProbe) | Out-Null
Set-Content -LiteralPath (Join-Path $workingDirectoryProbe 'marker.txt') -Value 'present'
$workingDirectoryResult = Invoke-PhotoSiftProcess `
    -FilePath $commandProcessor `
    -Arguments @('/d', '/c', 'if exist marker.txt (exit /b 0) else (exit /b 9)') `
    -WorkingDirectory $workingDirectoryProbe
Assert-Equal 0 $workingDirectoryResult.ExitCode 'native processes must run from the requested working directory'

$guestVerificationScript = Get-Content -Raw -LiteralPath (
    Join-Path $repositoryRoot 'eng\sandbox\Run-PhotoSiftVerification.ps1')
if ($guestVerificationScript -notmatch [regex]::Escape("@('restore', `$projectPath, '-r', 'win-x64'")) {
    throw 'Architecture restore must produce win-x64 assets.'
}
$script:passed++
if ($guestVerificationScript -notmatch [regex]::Escape("@('restore', 'PhotoSift.slnx', '-r', 'win-x64'")) {
    throw 'Full restore must produce win-x64 assets.'
}
$script:passed++

Assert-Throws {
    Assert-PhotoSiftSmokeResult `
        -ExitCode 1 `
        -StandardOutput 'PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true' `
        -TimedOut $false
} 'nonzero WPF smoke exits must be rejected'
Assert-Throws {
    Assert-PhotoSiftSmokeResult `
        -ExitCode 0 `
        -StandardOutput 'wrong marker' `
        -TimedOut $false
} 'missing WPF smoke marker must be rejected'
Assert-PhotoSiftSmokeResult `
    -ExitCode 0 `
    -StandardOutput 'PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true' `
    -TimedOut $false
$script:passed++

[xml]$sharedBuildConfiguration = Get-Content -Raw -LiteralPath (
    Join-Path $repositoryRoot 'Directory.Build.props')
Assert-Equal `
    'win-x64' `
    ([string]$sharedBuildConfiguration.Project.PropertyGroup.RuntimeIdentifier) `
    'the shared project boundary must select win-x64'
if ($guestVerificationScript -match [regex]::Escape("@('build', 'PhotoSift.slnx', '-c', 'Release', '-r'")) {
    throw 'Solution build must not use the unsupported solution-level RuntimeIdentifier switch.'
}
$script:passed++

Write-Output "Passed: $script:passed Failed: 0"
