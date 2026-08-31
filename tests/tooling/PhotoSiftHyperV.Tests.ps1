$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $repositoryRoot 'eng\verification\PhotoSiftVerification.psm1'
Import-Module $modulePath -Force

$script:passed = 0

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Expected,

        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if ($Expected -cne $Actual) {
        throw "Expected '$Expected' but got '$Actual': $Message"
    }
    $script:passed++
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Message
    )

    try {
        & $Action
    }
    catch {
        $script:passed++
        return
    }

    throw "Expected operation to throw: $Message"
}

$credential = New-Object System.Management.Automation.PSCredential(
    'PhotoSiftVerifier',
    (ConvertTo-SecureString 'test-only-password' -AsPlainText -Force))
$vm = [pscustomobject]@{
    Name = 'PhotoSift-Golden'
    State = 'Off'
}
$checkpoint = [pscustomobject]@{ Name = 'Golden' }

Assert-Throws {
    Assert-PhotoSiftGoldenVmVerificationCheckpoint `
        -CheckpointName 'Clean-Evaluation-Base'
} 'normal verification must never use the immutable recovery checkpoint'
Assert-PhotoSiftGoldenVmVerificationCheckpoint `
    -CheckpointName 'Toolchain-Evaluation-Base' | Out-Null
$script:passed++

$contract = Assert-PhotoSiftGoldenVmContract `
    -ExpectedVMName 'PhotoSift-Golden' `
    -ExpectedCheckpointName 'Golden' `
    -VM $vm `
    -Checkpoints @($checkpoint) `
    -NetworkAdapters @() `
    -Credential $credential `
    -IsElevated $true
Assert-Equal 'PhotoSift-Golden' $contract.vmName 'the accepted contract must record the exact VM name'
Assert-Equal 'Golden' $contract.checkpointName 'the accepted contract must record the exact checkpoint name'
Assert-Equal 0 $contract.networkAdapterCount 'the accepted contract must record zero virtual NICs'

Assert-Throws {
    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName 'PhotoSift-Golden' `
        -ExpectedCheckpointName 'Golden' `
        -VM $vm `
        -Checkpoints @($checkpoint) `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $false
} 'Hyper-V control must fail before mutation when the controller is not elevated'

Assert-Throws {
    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName 'PhotoSift-Golden' `
        -ExpectedCheckpointName 'Golden' `
        -VM ([pscustomobject]@{ Name = 'Wrong-VM'; State = 'Off' }) `
        -Checkpoints @($checkpoint) `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $true
} 'a VM identity mismatch must be rejected'

Assert-Throws {
    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName 'PhotoSift-Golden' `
        -ExpectedCheckpointName 'Golden' `
        -VM ([pscustomobject]@{ Name = 'PhotoSift-Golden'; State = 'Running' }) `
        -Checkpoints @($checkpoint) `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $true
} 'the Golden VM must be Off before a run'

Assert-Throws {
    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName 'PhotoSift-Golden' `
        -ExpectedCheckpointName 'Golden' `
        -VM $vm `
        -Checkpoints @() `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $true
} 'a missing Golden checkpoint must be rejected'

Assert-Throws {
    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName 'PhotoSift-Golden' `
        -ExpectedCheckpointName 'Golden' `
        -VM $vm `
        -Checkpoints @($checkpoint, [pscustomobject]@{ Name = 'Golden' }) `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $true
} 'duplicate Golden checkpoints must be rejected'

Assert-Throws {
    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName 'PhotoSift-Golden' `
        -ExpectedCheckpointName 'Golden' `
        -VM $vm `
        -Checkpoints @($checkpoint) `
        -NetworkAdapters @([pscustomobject]@{ Name = 'Network Adapter' }) `
        -Credential $credential `
        -IsElevated $true
} 'any virtual NIC must be rejected even when disconnected'

Assert-Throws {
    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName 'PhotoSift-Golden' `
        -ExpectedCheckpointName 'Golden' `
        -VM $vm `
        -Checkpoints @($checkpoint) `
        -NetworkAdapters @() `
        -Credential $null `
        -IsElevated $true
} 'a missing PowerShell Direct credential must be rejected'

$invalidCredentialCalls = 0
Assert-Throws {
    Connect-PhotoSiftPowerShellDirect `
        -VMName 'PhotoSift-Verification' `
        -Credential $credential `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
        -OpenSession {
            param($VMName, $Credential)
            $script:invalidCredentialCalls++
            throw 'The credential is invalid.'
        } `
        -WaitBeforeRetry { throw 'invalid credentials must not be retried' }
} 'an invalid guest credential must fail immediately'
Assert-Equal 1 $invalidCredentialCalls 'an invalid guest credential must be attempted exactly once'

$guestRunnerInvocation = Get-PhotoSiftGuestRunnerInvocation `
    -ScriptPath 'C:\PhotoSift\runs\test-run\input\eng\verification\Run-PhotoSiftVerification.ps1' `
    -Stage Architecture `
    -RunRoot 'C:\PhotoSift\runs\test-run' `
    -SdkRoot 'C:\PhotoSift\golden\sdk' `
    -PackagesRoot 'C:\PhotoSift\golden\packages'
Assert-Equal `
    'System.Management.Automation.PSCustomObject' `
    $guestRunnerInvocation.GetType().FullName `
    'the guest runner invocation must cross PowerShell Direct as one structured argument'
Assert-Equal `
    'C:\PhotoSift\golden\packages' `
    $guestRunnerInvocation.packagesRoot `
    'the controller must retain each guest path as an explicit scalar transport field'
Assert-Equal `
    '-NoProfile,-NonInteractive,-ExecutionPolicy,Bypass,-File,C:\PhotoSift\runs\test-run\input\eng\verification\Run-PhotoSiftVerification.ps1,-Stage,Architecture,-RunRoot,C:\PhotoSift\runs\test-run,-SdkRoot,C:\PhotoSift\golden\sdk,-PackagesRoot,C:\PhotoSift\golden\packages' `
    (@($guestRunnerInvocation.arguments) -join ',') `
    'the guest runner must bypass the sealed image execution policy explicitly'

$remoteEvidencePaths = @(Get-PhotoSiftGuestEvidencePaths -Items @(
        [pscustomobject]@{
            FullName = 'C:\PhotoSift\runs\test-run\output\summary.json'
            PSPath = $null
        }))
Assert-Equal `
    'C:\PhotoSift\runs\test-run\output\summary.json' `
    ($remoteEvidencePaths -join ',') `
    'evidence copy must use serialized guest FullName values rather than provider PSPath objects'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-HyperV-Tooling-Tests'
if ([System.IO.Directory]::Exists($testRoot)) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    $hostInput = Join-Path $testRoot 'input'
    $hostOutput = Join-Path $testRoot 'output'
    [System.IO.Directory]::CreateDirectory($hostInput) | Out-Null
    [System.IO.Directory]::CreateDirectory($hostOutput) | Out-Null

    $state = [pscustomobject]@{
        VMState = 'Off'
        RestoreCount = 0
        SessionClosed = $false
        EvidenceCopied = $false
    }
    $operations = @{
        RestoreCheckpoint = {
            param($VMName, $CheckpointName)
            $state.RestoreCount++
            $state.VMState = 'Off'
        }
        StartVM = {
            param($VMName)
            $state.VMState = 'Running'
        }
        WaitForVM = { param($VMName, $TimeoutSeconds) }
        OpenSession = {
            param($VMName, $Credential)
            return [pscustomobject]@{ Name = 'session' }
        }
        PrepareGuest = { param($Session, $GuestRunRoot) }
        CopySource = { param($Session, $HostInputPath, $GuestInputPath) }
        InvokeGuest = {
            param($Session, $Stage, $GuestRunRoot, $SdkRoot, $PackagesRoot)
            throw 'synthetic guest failure'
        }
        CopyEvidence = {
            param($Session, $GuestOutputPath, $HostOutputPath)
            $state.EvidenceCopied = $true
        }
        CloseSession = {
            param($Session)
            $state.SessionClosed = $true
        }
        StopVM = {
            param($VMName)
            $state.VMState = 'Off'
        }
    }

    Assert-Throws {
        Invoke-PhotoSiftGoldenVmRun `
            -VMName 'PhotoSift-Golden' `
            -CheckpointName 'Golden' `
            -Credential $credential `
            -Stage Architecture `
            -RunId 'test-run' `
            -HostInputPath $hostInput `
            -HostOutputPath $hostOutput `
            -GuestRunRoot 'C:\PhotoSift\runs\test-run' `
            -SdkRoot 'C:\PhotoSift\golden\sdk' `
            -PackagesRoot 'C:\PhotoSift\golden\packages' `
            -Operations $operations
    } 'a guest failure must propagate after cleanup'
    Assert-Equal $true $state.EvidenceCopied 'guest evidence must be attempted before lifecycle cleanup'
    Assert-Equal $true $state.SessionClosed 'the PowerShell Direct session must close after failure'
    Assert-Equal 'Off' $state.VMState 'the VM must be Off after failure cleanup'
    Assert-Equal 2 $state.RestoreCount 'the Golden checkpoint must be restored before and after a run'
}
finally {
    if ([System.IO.Directory]::Exists($testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$architectureStage = Get-PhotoSiftVerificationStage -Stage Architecture
Assert-Equal `
    'PhotoSift.Infrastructure.Tests' `
    ($architectureStage.testProjects -join ',') `
    'Architecture must execute only the Infrastructure project'
Assert-Equal $false $architectureStage.runRuntimeBoundary 'Architecture must not run the Full-only runtime boundary'
Assert-Equal $false $architectureStage.runWpfSmoke 'Architecture must not run the Full-only WPF smoke probe'

$coreStage = Get-PhotoSiftVerificationStage -Stage Core
Assert-Equal `
    'PhotoSift.Domain.Tests,PhotoSift.Application.Tests,PhotoSift.Analysis.Tests,PhotoSift.Scenarios.Tests' `
    ($coreStage.testProjects -join ',') `
    'Core must execute the four non-Infrastructure projects'

$fullStage = Get-PhotoSiftVerificationStage -Stage Full
Assert-Equal `
    'PhotoSift.Domain.Tests,PhotoSift.Application.Tests,PhotoSift.Analysis.Tests,PhotoSift.Infrastructure.Tests,PhotoSift.Scenarios.Tests' `
    ($fullStage.testProjects -join ',') `
    'Full must execute all five test projects'
Assert-Equal $true $fullStage.runRuntimeBoundary 'Full must run the authored-source runtime boundary'
Assert-Equal $true $fullStage.runWpfSmoke 'Full must run the WPF smoke probe'

$evidenceRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-HyperV-Evidence-Tests'
if ([System.IO.Directory]::Exists($evidenceRoot)) {
    Remove-Item -LiteralPath $evidenceRoot -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null

try {
    $passingTrxPath = Join-Path $evidenceRoot 'passing.trx'
    @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="3" executed="3" passed="3" failed="0" error="0" timeout="0" aborted="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $passingTrxPath -Encoding utf8
    $trxResult = Read-PhotoSiftTrxResult `
        -ProjectName 'PhotoSift.Infrastructure.Tests' `
        -TrxPath $passingTrxPath `
        -OutputRoot $evidenceRoot
    Assert-Equal 3 $trxResult.executed 'a reconciled TRX must retain its nonzero executed count'

    $zeroTrxPath = Join-Path $evidenceRoot 'zero.trx'
    @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="0" executed="0" passed="0" failed="0" error="0" timeout="0" aborted="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath $zeroTrxPath -Encoding utf8
    Assert-Throws {
        Read-PhotoSiftTrxResult `
            -ProjectName 'PhotoSift.Infrastructure.Tests' `
            -TrxPath $zeroTrxPath `
            -OutputRoot $evidenceRoot
    } 'zero-test TRX evidence must be rejected'

    $passingSummaryPath = Join-Path $evidenceRoot 'passing-summary.json'
    @{
        success = $true
        stage = 'Architecture'
        networkingDisabled = $true
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = @(@{ name = 'test-architecture'; exitCode = 0; succeeded = $true })
        tests = @(@{
            project = 'PhotoSift.Infrastructure.Tests'
            resultFile = 'passing.trx'
            total = 3
            executed = 3
            passed = 3
            failed = 0
            error = 0
            timeout = 0
            aborted = 0
            notExecuted = 0
        })
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $passingSummaryPath -Encoding utf8
    Assert-PhotoSiftVerificationResult `
        -SummaryPath $passingSummaryPath `
        -ExpectedStage Architecture | Out-Null
    $script:passed++

    $zeroSummaryPath = Join-Path $evidenceRoot 'zero-summary.json'
    @{
        success = $true
        stage = 'Architecture'
        networkingDisabled = $true
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = @(@{ name = 'test-architecture'; exitCode = 0; succeeded = $true })
        tests = @(@{
            project = 'PhotoSift.Infrastructure.Tests'
            resultFile = 'zero.trx'
            total = 0
            executed = 0
            passed = 0
            failed = 0
            error = 0
            timeout = 0
            aborted = 0
            notExecuted = 0
        })
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $zeroSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $zeroSummaryPath `
            -ExpectedStage Architecture
    } 'zero-test summary evidence must be rejected independently by the controller'
}
finally {
    if ([System.IO.Directory]::Exists($evidenceRoot)) {
        Remove-Item -LiteralPath $evidenceRoot -Recurse -Force
    }
}

Assert-Throws {
    Assert-PhotoSiftSmokeResult `
        -ExitCode 0 `
        -StandardOutput 'wrong marker' `
        -TimedOut $false
} 'the WPF smoke probe must reject a missing marker'
Assert-PhotoSiftSmokeResult `
    -ExitCode 0 `
    -StandardOutput 'PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true' `
    -TimedOut $false
$script:passed++

$controllerProbe = Invoke-PhotoSiftProcess `
    -FilePath (Get-Command pwsh.exe -ErrorAction Stop).Source `
    -Arguments @(
        '-NoProfile',
        '-File',
        (Join-Path $repositoryRoot 'eng\verification\Invoke-PhotoSiftHyperV.ps1'),
        '-Stage', 'Architecture') `
    -WorkingDirectory $repositoryRoot
Assert-Equal 1 $controllerProbe.ExitCode 'a non-elevated controller probe must fail closed'
Assert-Equal `
    $true `
    ($controllerProbe.StandardError -match 'Run Invoke-PhotoSiftHyperV.ps1 from an elevated PowerShell process' -and
        $controllerProbe.StandardError -notmatch 'Cannot find a provider') `
    'the first unmet controller contract must be reported without parameter-default noise'

Write-Output "Passed: $script:passed Failed: 0"
