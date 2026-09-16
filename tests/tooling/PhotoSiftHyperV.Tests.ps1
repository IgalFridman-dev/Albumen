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

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Pattern,

        [Parameter(Mandatory)]
        [string]$Message
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected error matching '$Pattern' but got '$($_.Exception.Message)': $Message"
        }
        $script:passed++
        return
    }

    throw "Expected operation to throw: $Message"
}

$credential = New-Object System.Management.Automation.PSCredential(
    'PhotoSiftVerifier',
    (ConvertTo-SecureString 'test-only-password' -AsPlainText -Force))
$runnerCredential = New-Object System.Management.Automation.PSCredential(
    '.\PhotoSiftRunner',
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

$resolvedRunnerCredential = Resolve-PhotoSiftGoldenVmCredential `
    -Credential $runnerCredential `
    -ExpectedComputerName 'PHOTOSIFTVM' `
    -ExpectedUserName 'PhotoSiftRunner'
Assert-Equal `
    'PHOTOSIFTVM\PhotoSiftRunner' `
    $resolvedRunnerCredential.UserName `
    'the guest credential must be canonicalized to the explicit local account'
Assert-Throws {
    Resolve-PhotoSiftGoldenVmCredential `
        -Credential $credential `
        -ExpectedComputerName 'PHOTOSIFTVM' `
        -ExpectedUserName 'PhotoSiftRunner'
} 'an unexpected guest user must be rejected before VM mutation'

$promptCalls = 0
$promptedCredential = Get-PhotoSiftGoldenVmCredential `
    -Credential $null `
    -ExpectedComputerName 'PHOTOSIFTVM' `
    -ExpectedUserName 'PhotoSiftRunner' `
    -PromptCredential {
        param($UserName, $Message)
        $script:promptCalls++
        Assert-Equal 'PHOTOSIFTVM\PhotoSiftRunner' $UserName 'the prompt must identify the explicit guest-local account'
        $runnerCredential
    }
Assert-Equal 1 $promptCalls 'a missing in-memory credential must prompt exactly once'
Assert-Equal `
    'PHOTOSIFTVM\PhotoSiftRunner' `
    $promptedCredential.UserName `
    'prompted credentials must be canonicalized without persistence'
$promptCalls = 0
$explicitCredential = Get-PhotoSiftGoldenVmCredential `
    -Credential $runnerCredential `
    -ExpectedComputerName 'PHOTOSIFTVM' `
    -ExpectedUserName 'PhotoSiftRunner' `
    -PromptCredential { throw 'explicit credentials must not prompt' }
Assert-Equal 0 $promptCalls 'an explicit in-memory credential must bypass prompting'
Assert-Equal 'PHOTOSIFTVM\PhotoSiftRunner' $explicitCredential.UserName 'explicit credentials must use the same canonical account'

$snapshotTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-HyperV-Snapshot-Tests'
if ([System.IO.Directory]::Exists($snapshotTestRoot)) {
    Remove-Item -LiteralPath $snapshotTestRoot -Recurse -Force
}
$snapshotSource = Join-Path $snapshotTestRoot 'source'
$snapshotTarget = Join-Path $snapshotTestRoot 'runs\test-run\input'
$snapshotOutput = Join-Path $snapshotTestRoot 'runs\test-run\output'
$junctionTarget = Join-Path $snapshotTestRoot 'junction-target'
[System.IO.Directory]::CreateDirectory($snapshotSource) | Out-Null
[System.IO.Directory]::CreateDirectory($snapshotTarget) | Out-Null
[System.IO.Directory]::CreateDirectory($snapshotOutput) | Out-Null
[System.IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
Set-Content -LiteralPath (Join-Path $snapshotSource 'source.txt') -Value 'source' -Encoding utf8
Set-Content -LiteralPath (Join-Path $junctionTarget 'secret.txt') -Value 'not source' -Encoding utf8
New-Item `
    -ItemType Junction `
    -Path (Join-Path $snapshotSource 'outside-junction') `
    -Target $junctionTarget | Out-Null
try {
    New-PhotoSiftSourceSnapshot `
        -RepositoryRoot $snapshotSource `
        -HostInputPath $snapshotTarget `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) | Out-Null
    Assert-Equal `
        $true `
        ([System.IO.File]::Exists((Join-Path $snapshotTarget 'source.txt'))) `
        'source staging must copy ordinary project files'
    Assert-Equal `
        $false `
        ([System.IO.Directory]::Exists((Join-Path $snapshotTarget 'outside-junction'))) `
        'source staging must exclude directory junctions rather than following them'

    Remove-PhotoSiftHostInputSnapshot `
        -HostInputPath $snapshotTarget `
        -RunsRoot (Join-Path $snapshotTestRoot 'runs')
    Assert-Equal $false ([System.IO.Directory]::Exists($snapshotTarget)) 'input cleanup must remove only the staged source snapshot'
    Assert-Equal $true ([System.IO.Directory]::Exists($snapshotOutput)) 'input cleanup must preserve durable run output'
}
finally {
    if ([System.IO.Directory]::Exists($snapshotTestRoot)) {
        Remove-Item -LiteralPath $snapshotTestRoot -Recurse -Force
    }
}

$invalidCredentialCalls = 0
$invalidCredentialIdentityCalls = 0
Assert-ThrowsLike {
    Connect-PhotoSiftPowerShellDirect `
        -VMName 'PhotoSift-Verification' `
        -CheckpointName 'Toolchain-Evaluation-Base' `
        -Credential $resolvedRunnerCredential `
        -ExpectedComputerName 'PHOTOSIFTVM' `
        -ExpectedUserName 'PhotoSiftRunner' `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
        -OpenSession {
            param($VMName, $Credential)
            $script:invalidCredentialCalls++
            $exception = New-Object System.Security.Authentication.AuthenticationException('localized failure')
            $record = New-Object System.Management.Automation.ErrorRecord(
                $exception,
                'PhotoSift.InvalidGuestCredential',
                [System.Management.Automation.ErrorCategory]::AuthenticationError,
                $VMName)
            throw $record
        } `
        -InspectIdentity {
            param($Session)
            $script:invalidCredentialIdentityCalls++
        } `
        -InvokeWithDeadline { param($Operation, $Arguments, $Deadline, $OperationName) & $Operation @Arguments } `
        -WaitBeforeRetry { throw 'invalid credentials must not be retried' }
} `
    -Pattern "PhotoSift-Verification.*Toolchain-Evaluation-Base.*captured.*not.*authenticated" `
    -Message 'an invalid guest credential must explain that capture is not authentication'
Assert-Equal 1 $invalidCredentialCalls 'an invalid guest credential must be attempted exactly once'
Assert-Equal 0 $invalidCredentialIdentityCalls 'identity inspection must not run after failed authentication'

$localizedOpenErrorCalls = 0
Assert-ThrowsLike {
    Connect-PhotoSiftPowerShellDirect `
        -VMName 'PhotoSift-Verification' `
        -CheckpointName 'Toolchain-Evaluation-Base' `
        -Credential $resolvedRunnerCredential `
        -ExpectedComputerName 'PHOTOSIFTVM' `
        -ExpectedUserName 'PhotoSiftRunner' `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
        -OpenSession {
            param($VMName, $Credential)
            $script:localizedOpenErrorCalls++
            $exception = New-Object System.Management.Automation.Remoting.PSRemotingTransportException(
                'Las credenciales proporcionadas no son válidas.')
            $record = New-Object System.Management.Automation.ErrorRecord(
                $exception,
                'PSSessionOpenFailed',
                [System.Management.Automation.ErrorCategory]::OpenError,
                $VMName)
            throw $record
        } `
        -InvokeWithDeadline { param($Operation, $Arguments, $Deadline, $OperationName) & $Operation @Arguments } `
        -WaitBeforeRetry { throw 'an unrecognized session-open failure must not be retried' }
} `
    -Pattern 'attempted once.*protect.*account' `
    -Message 'localized or otherwise unrecognized session-open failures must fail closed after one attempt'
Assert-Equal 1 $localizedOpenErrorCalls 'an unrecognized localized session-open failure must be attempted exactly once'

$transientOpenCalls = 0
$transientRetryDelays = New-Object 'System.Collections.Generic.List[int]'
$transientSession = Connect-PhotoSiftPowerShellDirect `
    -VMName 'PhotoSift-Verification' `
    -CheckpointName 'Toolchain-Evaluation-Base' `
    -Credential $resolvedRunnerCredential `
    -ExpectedComputerName 'PHOTOSIFTVM' `
    -ExpectedUserName 'PhotoSiftRunner' `
    -Deadline ([System.DateTimeOffset]::Now.AddSeconds(10)) `
    -OpenSession {
        param($VMName, $Credential)
        $script:transientOpenCalls++
        if ($script:transientOpenCalls -eq 1) {
            $record = New-Object System.Management.Automation.ErrorRecord(
                (New-Object System.InvalidOperationException('synthetic Hyper-V socket readiness delay')),
                'PhotoSift.PowerShellDirectNotReady',
                [System.Management.Automation.ErrorCategory]::ResourceUnavailable,
                $VMName)
            throw $record
        }
        [pscustomobject]@{ Name = 'transient-session' }
    } `
    -InspectIdentity {
        param($Session)
        [pscustomobject]@{
            identity = 'PHOTOSIFTVM\PhotoSiftRunner'
            computerName = 'PHOTOSIFTVM'
            userProfile = 'C:\Users\PhotoSiftRunner'
        }
    } `
    -InvokeWithDeadline { param($Operation, $Arguments, $Deadline, $OperationName) & $Operation @Arguments } `
    -WaitBeforeRetry { param($Seconds) $transientRetryDelays.Add($Seconds) }.GetNewClosure()
Assert-Equal 'transient-session' $transientSession.Name 'a positively identified readiness error may retry within the shared deadline'
Assert-Equal 2 $transientOpenCalls 'only an allowlisted readiness failure may cause another session attempt'
Assert-Equal 1 $transientRetryDelays.Count 'an allowlisted readiness failure must delay exactly once'
Assert-Equal $true ($transientRetryDelays[0] -ge 1 -and $transientRetryDelays[0] -le 2) 'retry delay must be capped to the remaining shared deadline'

Assert-ThrowsLike {
    Connect-PhotoSiftPowerShellDirect `
        -VMName 'PhotoSift-Verification' `
        -CheckpointName 'Toolchain-Evaluation-Base' `
        -Credential $resolvedRunnerCredential `
        -ExpectedComputerName 'PHOTOSIFTVM' `
        -ExpectedUserName 'PhotoSiftRunner' `
        -Deadline ([System.DateTimeOffset]::Now.AddSeconds(10)) `
        -OpenSession {
            param($VMName, $Credential)
            $record = New-Object System.Management.Automation.ErrorRecord(
                (New-Object System.InvalidOperationException('synthetic Hyper-V socket readiness delay')),
                'PhotoSift.PowerShellDirectNotReady',
                [System.Management.Automation.ErrorCategory]::ResourceUnavailable,
                $VMName)
            throw $record
        } `
        -WaitBeforeRetry { param($Seconds) throw "production connector observed allowlisted retry delay $Seconds" }
} `
    -Pattern 'production connector observed allowlisted retry delay [12]' `
    -Message 'the live-session connector must preserve the allowlisted transient error identity'

$identitySession = [pscustomobject]@{ Name = 'identity-session' }
$identityCloseCalls = 0
Assert-ThrowsLike {
    Connect-PhotoSiftPowerShellDirect `
        -VMName 'PhotoSift-Verification' `
        -CheckpointName 'Toolchain-Evaluation-Base' `
        -Credential $resolvedRunnerCredential `
        -ExpectedComputerName 'PHOTOSIFTVM' `
        -ExpectedUserName 'PhotoSiftRunner' `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
        -OpenSession { param($VMName, $Credential) $identitySession }.GetNewClosure() `
        -InspectIdentity {
            param($Session)
            [pscustomobject]@{
                identity = 'PHOTOSIFTVM\DifferentUser'
                computerName = 'PHOTOSIFTVM'
                userProfile = 'C:\Users\DifferentUser'
            }
        } `
        -InvokeWithDeadline { param($Operation, $Arguments, $Deadline, $OperationName) & $Operation @Arguments } `
        -CloseSession { param($Session) $script:identityCloseCalls++ }
} `
    -Pattern 'identity attestation failed' `
    -Message 'a session for the wrong guest identity must be rejected'
Assert-Equal 1 $identityCloseCalls 'an identity-mismatched session must be closed exactly once'

$authenticatedSession = [pscustomobject]@{ Name = 'authenticated-session' }
$returnedSession = Connect-PhotoSiftPowerShellDirect `
    -VMName 'PhotoSift-Verification' `
    -CheckpointName 'Toolchain-Evaluation-Base' `
    -Credential $resolvedRunnerCredential `
    -ExpectedComputerName 'PHOTOSIFTVM' `
    -ExpectedUserName 'PhotoSiftRunner' `
    -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
    -OpenSession { param($VMName, $Credential) $authenticatedSession }.GetNewClosure() `
    -InspectIdentity {
        param($Session)
        [pscustomobject]@{
            identity = 'PHOTOSIFTVM\PhotoSiftRunner'
            computerName = 'PHOTOSIFTVM'
            userProfile = 'C:\Users\PhotoSiftRunner'
        }
    } `
    -InvokeWithDeadline { param($Operation, $Arguments, $Deadline, $OperationName) & $Operation @Arguments }
Assert-Equal $authenticatedSession $returnedSession 'only an authenticated and attested session may be returned'

$defaultBoundedSession = Connect-PhotoSiftPowerShellDirect `
    -VMName 'PhotoSift-Verification' `
    -CheckpointName 'Toolchain-Evaluation-Base' `
    -Credential $resolvedRunnerCredential `
    -ExpectedComputerName 'PHOTOSIFTVM' `
    -ExpectedUserName 'PhotoSiftRunner' `
    -Deadline ([System.DateTimeOffset]::Now.AddSeconds(10)) `
    -OpenSession { param($VMName, $Credential) [pscustomobject]@{ Name = 'default-bounded-session' } } `
    -InspectIdentity {
        param($Session)
        [pscustomobject]@{
            identity = 'PHOTOSIFTVM\PhotoSiftRunner'
            computerName = 'PHOTOSIFTVM'
            userProfile = 'C:\Users\PhotoSiftRunner'
        }
    }
Assert-Equal 'default-bounded-session' $defaultBoundedSession.Name 'bounded identity attestation must preserve a caller-owned live session object'

$deadlineInvocationNames = New-Object 'System.Collections.Generic.List[string]'
$sessionOpenTimeouts = New-Object 'System.Collections.Generic.List[int]'
$deadlineWrappedSession = Connect-PhotoSiftPowerShellDirect `
    -VMName 'PhotoSift-Verification' `
    -CheckpointName 'Toolchain-Evaluation-Base' `
    -Credential $resolvedRunnerCredential `
    -ExpectedComputerName 'PHOTOSIFTVM' `
    -ExpectedUserName 'PhotoSiftRunner' `
    -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
    -OpenSession {
        param($VMName, $Credential, $OpenTimeoutMilliseconds)
        $sessionOpenTimeouts.Add($OpenTimeoutMilliseconds)
        [pscustomobject]@{ Name = 'bounded-session' }
    }.GetNewClosure() `
    -InspectIdentity {
        param($Session)
        [pscustomobject]@{
            identity = 'PHOTOSIFTVM\PhotoSiftRunner'
            computerName = 'PHOTOSIFTVM'
            userProfile = 'C:\Users\PhotoSiftRunner'
        }
    } `
    -InvokeWithDeadline {
        param($Operation, $Arguments, $Deadline, $OperationName)
        $deadlineInvocationNames.Add($OperationName)
        & $Operation @Arguments
    }.GetNewClosure()
Assert-Equal 'bounded-session' $deadlineWrappedSession.Name 'the bounded connector must return the authenticated session'
Assert-Equal 1 $sessionOpenTimeouts.Count 'session creation must receive exactly one open timeout'
Assert-Equal $true ($sessionOpenTimeouts[0] -gt 0 -and $sessionOpenTimeouts[0] -le 60000) 'session creation must be bounded to the remaining shared deadline'
Assert-Equal `
    'PowerShell Direct identity attestation' `
    ($deadlineInvocationNames -join ',') `
    'identity attestation must execute through the shared deadline wrapper'

Assert-Throws {
    Get-PhotoSiftRemainingSeconds `
        -Deadline ([System.DateTimeOffset]::Now.AddSeconds(-1)) `
        -OperationName 'expired test operation'
} 'the shared verification deadline must fail closed once exhausted'
$futureRemaining = Get-PhotoSiftRemainingSeconds `
    -Deadline ([System.DateTimeOffset]::Now.AddSeconds(10)) `
    -OperationName 'future test operation'
Assert-Equal $true ($futureRemaining -gt 0 -and $futureRemaining -le 10) 'remaining time must derive from the one absolute deadline'

$boundedCommandResult = @(Invoke-PhotoSiftPowerShellWithDeadline `
    -ScriptBlock { param($Value) $Value } `
    -ArgumentList @('bounded-result') `
    -Deadline ([System.DateTimeOffset]::Now.AddSeconds(10)) `
    -OperationName 'successful bounded test command')
Assert-Equal 'bounded-result' ($boundedCommandResult -join '') 'bounded PowerShell operations must preserve their output'

$longTimeoutResult = Invoke-PhotoSiftProcess `
    -FilePath (Get-Command powershell.exe -ErrorAction Stop).Source `
    -Arguments @('-NoProfile', '-NonInteractive', '-Command', 'exit 0') `
    -TimeoutSeconds 7200
Assert-Equal 0 $longTimeoutResult.ExitCode 'the native process helper must accept the controller maximum of 7200 seconds'

$boundedCommandStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
Assert-Throws {
    Invoke-PhotoSiftPowerShellWithDeadline `
        -ScriptBlock { Start-Sleep -Seconds 10 } `
        -Deadline ([System.DateTimeOffset]::Now.AddSeconds(1)) `
        -OperationName 'bounded test command'
} 'a bounded PowerShell operation must be interrupted at the shared deadline'
$boundedCommandStopwatch.Stop()
Assert-Equal $true ($boundedCommandStopwatch.Elapsed.TotalSeconds -lt 5) 'bounded PowerShell interruption must return promptly'

$guestRunnerInvocation = Get-PhotoSiftGuestRunnerInvocation `
    -ScriptPath 'C:\PhotoSift\runs\test-run\input\eng\verification\Run-PhotoSiftVerification.ps1' `
    -Stage Architecture `
    -RunRoot 'C:\PhotoSift\runs\test-run' `
    -SdkRoot 'C:\PhotoSift\golden\sdk' `
    -PackagesRoot 'C:\PhotoSift\golden\packages' `
    -TimeoutSeconds 300
Assert-Equal `
    'System.Management.Automation.PSCustomObject' `
    $guestRunnerInvocation.GetType().FullName `
    'the guest runner invocation must cross PowerShell Direct as one structured argument'
Assert-Equal `
    'C:\PhotoSift\golden\packages' `
    $guestRunnerInvocation.packagesRoot `
    'the controller must retain each guest path as an explicit scalar transport field'
Assert-Equal `
    '-NoProfile,-NonInteractive,-ExecutionPolicy,Bypass,-File,C:\PhotoSift\runs\test-run\input\eng\verification\Run-PhotoSiftVerification.ps1,-Stage,Architecture,-RunRoot,C:\PhotoSift\runs\test-run,-SdkRoot,C:\PhotoSift\golden\sdk,-PackagesRoot,C:\PhotoSift\golden\packages,-TimeoutSeconds,300' `
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
        InspectVmContract = {
            param($VMName)
            [pscustomobject]@{ vmState = $state.VMState; networkAdapterCount = 0 }
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
            -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
            -Operations $operations
    } 'a guest failure must propagate after cleanup'
    Assert-Equal $true $state.EvidenceCopied 'guest evidence must be attempted before lifecycle cleanup'
    Assert-Equal $true $state.SessionClosed 'the PowerShell Direct session must close after failure'
    Assert-Equal 'Off' $state.VMState 'the VM must be Off after failure cleanup'
    Assert-Equal 2 $state.RestoreCount 'the Golden checkpoint must be restored before and after a run'

    $restoredNicState = [pscustomobject]@{
        StartCalled = $false
        RestoreCount = 0
    }
    $restoredNicOperations = @{
        RestoreCheckpoint = {
            param($VMName, $CheckpointName)
            $restoredNicState.RestoreCount++
        }.GetNewClosure()
        InspectVmContract = {
            param($VMName)
            throw 'restored checkpoint reintroduced a virtual NIC'
        }.GetNewClosure()
        StartVM = {
            param($VMName)
            $restoredNicState.StartCalled = $true
        }.GetNewClosure()
        WaitForVM = { param($VMName, $TimeoutSeconds) }
        OpenSession = { param($VMName, $Credential) [pscustomobject]@{ Name = 'session' } }
        PrepareGuest = { param($Session, $GuestRunRoot) }
        CopySource = { param($Session, $HostInputPath, $GuestInputPath) }
        InvokeGuest = { param($Session, $Stage, $GuestRunRoot, $SdkRoot, $PackagesRoot) }
        CopyEvidence = { param($Session, $GuestOutputPath, $HostOutputPath) }
        CloseSession = { param($Session) }
        StopVM = { param($VMName) }
    }
    Assert-Throws {
        Invoke-PhotoSiftGoldenVmRun `
            -VMName 'PhotoSift-Golden' `
            -CheckpointName 'Golden' `
            -Credential $credential `
            -Stage Architecture `
            -RunId 'restored-nic-run' `
            -HostInputPath $hostInput `
            -HostOutputPath $hostOutput `
            -GuestRunRoot 'C:\PhotoSift\runs\restored-nic-run' `
            -SdkRoot 'C:\PhotoSift\golden\sdk' `
            -PackagesRoot 'C:\PhotoSift\golden\packages' `
            -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
            -Operations $restoredNicOperations
    } 'a checkpoint that restores a NIC must fail before VM startup'
    Assert-Equal $false $restoredNicState.StartCalled 'post-restore NIC validation must precede StartVM'
    Assert-Equal 2 $restoredNicState.RestoreCount 'post-restore validation failure must still restore during cleanup'

    $expiredRunState = [pscustomobject]@{
        StartCalled = $false
        RestoreCount = 0
    }
    $expiredRunOperations = @{
        RestoreCheckpoint = {
            param($VMName, $CheckpointName)
            $expiredRunState.RestoreCount++
        }.GetNewClosure()
        InspectVmContract = {
            param($VMName)
            [pscustomobject]@{ vmState = 'Off'; networkAdapterCount = 0 }
        }
        StartVM = {
            param($VMName)
            $expiredRunState.StartCalled = $true
        }.GetNewClosure()
        WaitForVM = { param($VMName, $TimeoutSeconds) }
        OpenSession = { param($VMName, $Credential) [pscustomobject]@{ Name = 'session' } }
        PrepareGuest = { param($Session, $GuestRunRoot) }
        CopySource = { param($Session, $HostInputPath, $GuestInputPath) }
        InvokeGuest = { param($Session, $Stage, $GuestRunRoot, $SdkRoot, $PackagesRoot) }
        CopyEvidence = { param($Session, $GuestOutputPath, $HostOutputPath) }
        CloseSession = { param($Session) }
        StopVM = { param($VMName) }
    }
    Assert-Throws {
        Invoke-PhotoSiftGoldenVmRun `
            -VMName 'PhotoSift-Golden' `
            -CheckpointName 'Golden' `
            -Credential $credential `
            -Stage Architecture `
            -RunId 'expired-run' `
            -HostInputPath $hostInput `
            -HostOutputPath $hostOutput `
            -GuestRunRoot 'C:\PhotoSift\runs\expired-run' `
            -SdkRoot 'C:\PhotoSift\golden\sdk' `
            -PackagesRoot 'C:\PhotoSift\golden\packages' `
            -Deadline ([System.DateTimeOffset]::Now.AddSeconds(-1)) `
            -Operations $expiredRunOperations
    } 'an expired shared deadline must fail before VM startup and still clean up'
    Assert-Equal $false $expiredRunState.StartCalled 'an expired run must not start the VM'
    Assert-Equal 1 $expiredRunState.RestoreCount 'an expired run must still restore the checkpoint during cleanup'
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
Assert-Equal `
    'restore,tests\PhotoSift.Infrastructure.Tests\PhotoSift.Infrastructure.Tests.csproj,-r,win-x64,--locked-mode,--configfile,NuGet.Config,-p:NuGetAudit=false' `
    (@($architectureStage.restoreArguments) -join ',') `
    'Architecture restore must use the pinned offline lock contract'

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
Assert-Equal 'eng\Verify-RuntimeBoundary.ps1' $fullStage.runtimeBoundaryScript 'Full must name the runtime boundary script it executes'
Assert-Equal 'eng\Invoke-OfflineLaunchSmoke.ps1' $fullStage.wpfSmokeScript 'Full must name the WPF smoke script it executes'
Assert-Equal $false $fullStage.signingEnabled 'Golden VM verification must not invoke signing'

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

    $architectureCommandNames = @(
        'copy-source',
        'dotnet-version',
        'dotnet-runtimes',
        'restore-architecture',
        'build-architecture',
        'test-architecture'
    )
    $architectureCommandResults = @($architectureCommandNames | ForEach-Object {
        $logName = "$_.log"
        Set-Content -LiteralPath (Join-Path $evidenceRoot $logName) -Value "$_ passed" -Encoding utf8
        @{
            name = $_
            exitCode = 0
            succeeded = $true
            timedOut = $false
            logPath = $logName
        }
    })

    $passingSummaryPath = Join-Path $evidenceRoot 'passing-summary.json'
    @{
        success = $true
        stage = 'Architecture'
        networkingDisabled = $true
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = $architectureCommandResults
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

    $missingLogSummaryPath = Join-Path $evidenceRoot 'missing-log-summary.json'
    $missingLogSummary = Get-Content -Raw -LiteralPath $passingSummaryPath | ConvertFrom-Json
    $missingLogSummary.commands[0].logPath = 'missing.log'
    $missingLogSummary | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $missingLogSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $missingLogSummaryPath `
            -ExpectedStage Architecture
    } 'command evidence must reject a missing declared log file'

    $mismatchedTrxSummaryPath = Join-Path $evidenceRoot 'mismatched-trx-summary.json'
    $mismatchedTrxSummary = Get-Content -Raw -LiteralPath $passingSummaryPath | ConvertFrom-Json
    $mismatchedTrxSummary.tests[0].total = 2
    $mismatchedTrxSummary.tests[0].executed = 2
    $mismatchedTrxSummary.tests[0].passed = 2
    $mismatchedTrxSummary | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $mismatchedTrxSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $mismatchedTrxSummaryPath `
            -ExpectedStage Architecture
    } 'summary counts must reconcile exactly with host-reparsed TRX counters'

    $traversalSummaryPath = Join-Path $evidenceRoot 'traversal-summary.json'
    $traversalSummary = Get-Content -Raw -LiteralPath $passingSummaryPath | ConvertFrom-Json
    $traversalSummary.tests[0].resultFile = '..\passing.trx'
    $traversalSummary | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $traversalSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $traversalSummaryPath `
            -ExpectedStage Architecture
    } 'TRX containment must be checked before any outside path is read'

    $stringCountSummaryPath = Join-Path $evidenceRoot 'string-count-summary.json'
    $stringCountSummary = Get-Content -Raw -LiteralPath $passingSummaryPath | ConvertFrom-Json
    $stringCountSummary.tests[0].executed = '3'
    $stringCountSummary | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $stringCountSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $stringCountSummaryPath `
            -ExpectedStage Architecture
    } 'string values must not satisfy integer test-count properties'

    $stringBooleanSummaryPath = Join-Path $evidenceRoot 'string-boolean-summary.json'
    @{
        success = 'false'
        stage = 'Architecture'
        networkingDisabled = 'false'
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = $architectureCommandResults
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
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stringBooleanSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $stringBooleanSummaryPath `
            -ExpectedStage Architecture
    } 'string values must not satisfy Boolean verification properties'

    $stringTimeoutCommands = @($architectureCommandResults | ForEach-Object {
        @{
            name = $_.name
            exitCode = $_.exitCode
            succeeded = $_.succeeded
            timedOut = $_.timedOut
            logPath = $_.logPath
        }
    })
    $stringTimeoutCommands[0].timedOut = 'false'
    $stringTimeoutSummaryPath = Join-Path $evidenceRoot 'string-timeout-summary.json'
    @{
        success = $true
        stage = 'Architecture'
        networkingDisabled = $true
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = $stringTimeoutCommands
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
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stringTimeoutSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $stringTimeoutSummaryPath `
            -ExpectedStage Architecture
    } 'string values must not satisfy the command timedOut Boolean contract'

    $invalidExitCommands = @($architectureCommandResults | ForEach-Object {
        @{
            name = $_.name
            exitCode = $_.exitCode
            succeeded = $_.succeeded
            timedOut = $_.timedOut
            logPath = $_.logPath
        }
    })
    $invalidExitCommands[1].exitCode = 99
    $invalidExitSummaryPath = Join-Path $evidenceRoot 'invalid-exit-summary.json'
    @{
        success = $true
        stage = 'Architecture'
        networkingDisabled = $true
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = $invalidExitCommands
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
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $invalidExitSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $invalidExitSummaryPath `
            -ExpectedStage Architecture
    } 'successful command evidence must reject an impossible accepted exit code'

    $fabricatedFullSummaryPath = Join-Path $evidenceRoot 'fabricated-full-summary.json'
    @{
        success = $true
        stage = 'Full'
        networkingDisabled = $true
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = @()
        tests = @(
            'PhotoSift.Domain.Tests',
            'PhotoSift.Application.Tests',
            'PhotoSift.Analysis.Tests',
            'PhotoSift.Infrastructure.Tests',
            'PhotoSift.Scenarios.Tests' | ForEach-Object {
                @{
                    project = $_
                    resultFile = 'missing.trx'
                    total = 1
                    executed = 1
                    passed = 1
                    failed = 0
                    error = 0
                    timeout = 0
                    aborted = 0
                    notExecuted = 0
                }
            })
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fabricatedFullSummaryPath -Encoding utf8
    Assert-Throws {
        Assert-PhotoSiftVerificationResult `
            -SummaryPath $fabricatedFullSummaryPath `
            -ExpectedStage Full
    } 'Full must reject empty commands, fabricated counts, and missing TRX evidence'

    $zeroSummaryPath = Join-Path $evidenceRoot 'zero-summary.json'
    @{
        success = $true
        stage = 'Architecture'
        networkingDisabled = $true
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        commands = $architectureCommandResults
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

$processTreeRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-Process-Tree-Tests'
if ([System.IO.Directory]::Exists($processTreeRoot)) {
    Remove-Item -LiteralPath $processTreeRoot -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($processTreeRoot) | Out-Null
$childScriptPath = Join-Path $processTreeRoot 'child.ps1'
$parentScriptPath = Join-Path $processTreeRoot 'parent.ps1'
$childMarkerPath = Join-Path $processTreeRoot 'child-survived.txt'
@'
param([string]$MarkerPath)
Start-Sleep -Seconds 4
Set-Content -LiteralPath $MarkerPath -Value 'survived' -Encoding utf8
'@ | Set-Content -LiteralPath $childScriptPath -Encoding utf8
@'
param([string]$ChildScriptPath, [string]$MarkerPath)
& (Get-Command powershell.exe -ErrorAction Stop).Source `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $ChildScriptPath `
    -MarkerPath $MarkerPath
'@ | Set-Content -LiteralPath $parentScriptPath -Encoding utf8
try {
    $treeTimeoutResult = Invoke-PhotoSiftProcess `
        -FilePath (Get-Command powershell.exe -ErrorAction Stop).Source `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $parentScriptPath,
            '-ChildScriptPath', $childScriptPath,
            '-MarkerPath', $childMarkerPath) `
        -WorkingDirectory $processTreeRoot `
        -TimeoutSeconds 1
    Assert-Equal $true $treeTimeoutResult.TimedOut 'native process timeout must be reported'
    Start-Sleep -Seconds 5
    Assert-Equal `
        $false `
        ([System.IO.File]::Exists($childMarkerPath)) `
        'native process timeout must terminate the entire spawned process tree'
}
finally {
    if ([System.IO.Directory]::Exists($processTreeRoot)) {
        Remove-Item -LiteralPath $processTreeRoot -Recurse -Force
    }
}

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
