$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$verificationModulePath = Join-Path $repositoryRoot 'eng\verification\AlbumenVerification.psm1'
$recoveryModulePath = Join-Path $repositoryRoot 'eng\verification\AlbumenGoldenVmCredentialRecovery.psm1'
Import-Module $verificationModulePath -Force
Import-Module $recoveryModulePath -Force

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
    'PHOTOSIFTVM\PhotoSiftRunner',
    (ConvertTo-SecureString 'test-only-password' -AsPlainText -Force))
$vm = [pscustomobject]@{ Name = 'PhotoSift-Verification'; State = 'Off' }
$cleanCheckpoint = [pscustomobject]@{ Name = 'Clean-Evaluation-Base' }
$credentialCheckpoint = [pscustomobject]@{ Name = 'Credential-Evaluation-Base' }

$contract = Assert-AlbumenGoldenVmCredentialRecoveryContract `
    -ExpectedVMName 'PhotoSift-Verification' `
    -RecoveryCheckpointName 'Clean-Evaluation-Base' `
    -TargetCheckpointName 'Credential-Evaluation-Base' `
    -VM $vm `
    -Checkpoints @($cleanCheckpoint) `
    -NetworkAdapters @() `
    -Credential $credential `
    -IsElevated $true
Assert-Equal 'Credential-Evaluation-Base' $contract.targetCheckpointName 'the recovery contract must preserve the immutable checkpoint and name an additive target'

$successState = [pscustomobject]@{
    Events = New-Object 'System.Collections.Generic.List[string]'
    VMState = 'Off'
    TargetCreated = $false
}
$successOperations = @{
    InspectVmContract = {
        param($VMName)
        [pscustomobject]@{ vmState = $successState.VMState; networkAdapterCount = 0 }
    }.GetNewClosure()
    StartVM = {
        param($VMName)
        $successState.Events.Add('start')
        $successState.VMState = 'Running'
    }.GetNewClosure()
    WaitForVM = { param($VMName, $TimeoutSeconds) $successState.Events.Add('wait-ready') }.GetNewClosure()
    OpenSession = {
        param($VMName, $CheckpointContext, $Credential)
        $successState.Events.Add("authenticate:$CheckpointContext")
        [pscustomobject]@{ Name = 'session' }
    }.GetNewClosure()
    InspectGuestBaseline = {
        param($Session)
        $successState.Events.Add('inspect-clean')
        [pscustomobject]@{ albumenRootExists = $false }
    }.GetNewClosure()
    ShutdownGuest = { param($Session) $successState.Events.Add('shutdown') }.GetNewClosure()
    CloseSession = { param($Session) $successState.Events.Add('close') }.GetNewClosure()
    WaitForPowerOff = {
        param($VMName, $TimeoutSeconds)
        $successState.Events.Add('wait-off')
        $successState.VMState = 'Off'
    }.GetNewClosure()
    CreateTargetCheckpoint = {
        param($VMName, $CheckpointName)
        $successState.Events.Add('create-target')
        $successState.TargetCreated = $true
    }.GetNewClosure()
    ReadCheckpoints = {
        param($VMName)
        if ($successState.TargetCreated) {
            return @($cleanCheckpoint, $credentialCheckpoint)
        }
        return @($cleanCheckpoint)
    }.GetNewClosure()
    RestoreTargetCheckpoint = {
        param($VMName, $CheckpointName)
        $successState.Events.Add('restore-target')
        $successState.VMState = 'Off'
    }.GetNewClosure()
    RemoveTargetCheckpoint = {
        param($VMName, $CheckpointName)
        $successState.Events.Add('remove-target')
        $successState.TargetCreated = $false
    }.GetNewClosure()
    StopVM = {
        param($VMName)
        $successState.Events.Add('stop')
        $successState.VMState = 'Off'
    }.GetNewClosure()
}

$result = Invoke-AlbumenGoldenVmCredentialRecovery `
    -VMName 'PhotoSift-Verification' `
    -RecoveryCheckpointName 'Clean-Evaluation-Base' `
    -TargetCheckpointName 'Credential-Evaluation-Base' `
    -Credential $credential `
    -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
    -Operations $successOperations
Assert-Equal $true $successState.TargetCreated 'successful recovery must preserve the additive credential checkpoint'
Assert-Equal 'Off' $successState.VMState 'successful recovery must leave the VM Off'
Assert-Equal 'Credential-Evaluation-Base' $result.targetCheckpointName 'successful recovery must report the additive target'
Assert-Equal `
    'start,wait-ready,authenticate:current VM state (no checkpoint restored),inspect-clean,shutdown,close,wait-off,create-target,restore-target,start,wait-ready,authenticate:Credential-Evaluation-Base,inspect-clean,shutdown,close,wait-off,restore-target' `
    ($successState.Events -join ',') `
    'recovery must authenticate current state before any restore and then re-authenticate the additive checkpoint'

$failureState = [pscustomobject]@{
    Events = New-Object 'System.Collections.Generic.List[string]'
    VMState = 'Off'
    TargetCreated = $false
}
$failureOperations = @{
    InspectVmContract = { param($VMName) [pscustomobject]@{ vmState = $failureState.VMState; networkAdapterCount = 0 } }.GetNewClosure()
    StartVM = { param($VMName) $failureState.Events.Add('start'); $failureState.VMState = 'Running' }.GetNewClosure()
    WaitForVM = { param($VMName, $TimeoutSeconds) $failureState.Events.Add('wait-ready') }.GetNewClosure()
    OpenSession = { param($VMName, $CheckpointContext, $Credential) $failureState.Events.Add('authenticate-current'); throw 'synthetic authentication failure' }.GetNewClosure()
    InspectGuestBaseline = { param($Session) $failureState.Events.Add('inspect-clean') }.GetNewClosure()
    ShutdownGuest = { param($Session) $failureState.Events.Add('shutdown') }.GetNewClosure()
    CloseSession = { param($Session) $failureState.Events.Add('close') }.GetNewClosure()
    WaitForPowerOff = { param($VMName, $TimeoutSeconds) $failureState.Events.Add('wait-off'); $failureState.VMState = 'Off' }.GetNewClosure()
    CreateTargetCheckpoint = { param($VMName, $CheckpointName) $failureState.Events.Add('create-target'); $failureState.TargetCreated = $true }.GetNewClosure()
    ReadCheckpoints = { param($VMName) @($cleanCheckpoint) }.GetNewClosure()
    RestoreTargetCheckpoint = { param($VMName, $CheckpointName) $failureState.Events.Add('restore-target') }.GetNewClosure()
    RemoveTargetCheckpoint = { param($VMName, $CheckpointName) $failureState.Events.Add('remove-target'); $failureState.TargetCreated = $false }.GetNewClosure()
    StopVM = { param($VMName) $failureState.Events.Add('stop'); $failureState.VMState = 'Off' }.GetNewClosure()
}
Assert-Throws {
    Invoke-AlbumenGoldenVmCredentialRecovery `
        -VMName 'PhotoSift-Verification' `
        -RecoveryCheckpointName 'Clean-Evaluation-Base' `
        -TargetCheckpointName 'Credential-Evaluation-Base' `
        -Credential $credential `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
        -Operations $failureOperations
} 'current-state authentication failure must abort without creating or restoring a checkpoint'
Assert-Equal $false $failureState.TargetCreated 'authentication failure must not create a checkpoint'
Assert-Equal 'start,wait-ready,authenticate-current,stop' ($failureState.Events -join ',') 'authentication failure cleanup must stop the VM without restoring the immutable checkpoint'

Assert-Throws {
    Assert-AlbumenGoldenVmCredentialRecoveryContract `
        -ExpectedVMName 'PhotoSift-Verification' `
        -RecoveryCheckpointName 'Clean-Evaluation-Base' `
        -TargetCheckpointName 'Credential-Evaluation-Base' `
        -VM $vm `
        -Checkpoints @($cleanCheckpoint, $cleanCheckpoint) `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $true
} 'credential recovery must reject a duplicated immutable recovery checkpoint'

Assert-Throws {
    Assert-AlbumenGoldenVmCredentialRecoveryContract `
        -ExpectedVMName 'PhotoSift-Verification' `
        -RecoveryCheckpointName 'Clean-Evaluation-Base' `
        -TargetCheckpointName 'Credential-Evaluation-Base' `
        -VM $vm `
        -Checkpoints @($cleanCheckpoint, $credentialCheckpoint) `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $true
} 'credential recovery must reject a pre-existing credential target'

function New-AlbumenCredentialRecoveryFaultHarness {
    param([Parameter(Mandatory)][string]$Fault)

    $state = [pscustomobject]@{
        Events = New-Object 'System.Collections.Generic.List[string]'
        VMState = 'Off'
        TargetCreated = ($Fault -ceq 'foreign-target-precreate')
        OpenCount = 0
        RemovalNames = New-Object 'System.Collections.Generic.List[string]'
    }
    $operations = @{
        InspectVmContract = {
            param($VMName)
            $networkAdapterCount = 0
            if ($Fault -ceq 'restored-nic' -and $state.Events.Contains('restore-target')) {
                $networkAdapterCount = 1
            }
            [pscustomobject]@{
                vmState = $state.VMState
                networkAdapterCount = $networkAdapterCount
            }
        }.GetNewClosure()
        StartVM = {
            param($VMName)
            $state.Events.Add('start')
            $state.VMState = 'Running'
        }.GetNewClosure()
        WaitForVM = { param($VMName, $TimeoutSeconds) $state.Events.Add('wait-ready') }.GetNewClosure()
        OpenSession = {
            param($VMName, $CheckpointContext, $Credential)
            $state.OpenCount++
            $state.Events.Add("authenticate:$CheckpointContext")
            if ($Fault -ceq 'target-auth' -and $state.OpenCount -eq 2) {
                throw 'synthetic target authentication failure'
            }
            [pscustomobject]@{ Name = "session-$($state.OpenCount)" }
        }.GetNewClosure()
        InspectGuestBaseline = {
            param($Session)
            $state.Events.Add('inspect-clean')
            if ($Fault -ceq 'cleanup-aggregate' -and $state.OpenCount -eq 2) {
                throw 'synthetic sealed baseline inspection failure'
            }
            [pscustomobject]@{
                albumenRootExists = ($Fault -ceq 'dirty-baseline')
            }
        }.GetNewClosure()
        ShutdownGuest = { param($Session) $state.Events.Add('shutdown') }.GetNewClosure()
        CloseSession = {
            param($Session)
            $state.Events.Add('close')
            if ($Fault -ceq 'cleanup-aggregate' -and $state.OpenCount -eq 2) {
                throw 'synthetic close cleanup failure'
            }
        }.GetNewClosure()
        WaitForPowerOff = {
            param($VMName, $TimeoutSeconds)
            $state.Events.Add('wait-off')
            $state.VMState = 'Off'
        }.GetNewClosure()
        CreateTargetCheckpoint = {
            param($VMName, $CheckpointName)
            $state.Events.Add('create-target')
            if ($Fault -ceq 'foreign-target-precreate') {
                throw 'synthetic duplicate checkpoint creation failure'
            }
            $state.TargetCreated = $true
            if ($Fault -ceq 'create-then-throw') {
                throw 'synthetic post-create failure'
            }
        }.GetNewClosure()
        ReadCheckpoints = {
            param($VMName)
            if ($state.TargetCreated) {
                return @($cleanCheckpoint, $credentialCheckpoint)
            }
            return @($cleanCheckpoint)
        }.GetNewClosure()
        RestoreTargetCheckpoint = {
            param($VMName, $CheckpointName)
            $state.Events.Add('restore-target')
            $state.VMState = 'Off'
        }.GetNewClosure()
        RemoveTargetCheckpoint = {
            param($VMName, $CheckpointName)
            $state.Events.Add('remove-target')
            $state.RemovalNames.Add($CheckpointName)
            if ($Fault -ceq 'cleanup-aggregate') {
                throw 'synthetic checkpoint cleanup failure'
            }
            $state.TargetCreated = $false
        }.GetNewClosure()
        StopVM = {
            param($VMName)
            $state.Events.Add('stop')
            $state.VMState = 'Off'
            if ($Fault -ceq 'cleanup-aggregate') {
                throw 'synthetic VM cleanup failure'
            }
        }.GetNewClosure()
    }
    [pscustomobject]@{ State = $state; Operations = $operations }
}

$foreignTargetHarness = New-AlbumenCredentialRecoveryFaultHarness `
    -Fault 'foreign-target-precreate'
Assert-Throws {
    Invoke-AlbumenGoldenVmCredentialRecovery `
        -VMName 'PhotoSift-Verification' `
        -RecoveryCheckpointName 'Clean-Evaluation-Base' `
        -TargetCheckpointName 'Credential-Evaluation-Base' `
        -Credential $credential `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
        -Operations $foreignTargetHarness.Operations
} 'a same-named target appearing immediately before creation must abort without claiming ownership'
Assert-Equal $true $foreignTargetHarness.State.TargetCreated 'a foreign pre-create target must remain present'
Assert-Equal $false $foreignTargetHarness.State.Events.Contains('create-target') 'the last-moment checkpoint assertion must run before creation'
Assert-Equal $false $foreignTargetHarness.State.Events.Contains('remove-target') 'cleanup must not remove a target this invocation did not create'
Assert-Equal 'Off' $foreignTargetHarness.State.VMState 'a foreign pre-create target failure must leave the VM Off'

foreach ($fault in @('dirty-baseline', 'restored-nic', 'target-auth', 'create-then-throw')) {
    $harness = New-AlbumenCredentialRecoveryFaultHarness -Fault $fault
    Assert-Throws {
        Invoke-AlbumenGoldenVmCredentialRecovery `
            -VMName 'PhotoSift-Verification' `
            -RecoveryCheckpointName 'Clean-Evaluation-Base' `
            -TargetCheckpointName 'Credential-Evaluation-Base' `
            -Credential $credential `
            -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
            -Operations $harness.Operations
    } "the '$fault' lifecycle fault must abort credential checkpoint creation"
    Assert-Equal 'Off' $harness.State.VMState "the '$fault' lifecycle fault must leave the VM Off"
    Assert-Equal $false $harness.State.Events.Contains('restore-clean') "the '$fault' lifecycle fault must never restore Clean-Evaluation-Base"
    if ($fault -ceq 'dirty-baseline') {
        Assert-Equal $false $harness.State.TargetCreated 'a dirty current baseline must abort before checkpoint creation'
        Assert-Equal $false $harness.State.Events.Contains('restore-target') 'a dirty current baseline must not restore any checkpoint'
    }
    else {
        Assert-Equal $false $harness.State.TargetCreated "the '$fault' lifecycle fault must remove the incomplete target"
        Assert-Equal 'Credential-Evaluation-Base' ($harness.State.RemovalNames -join ',') "the '$fault' lifecycle fault must remove only the exact credential target"
    }
    if ($fault -ceq 'restored-nic') {
        Assert-Equal 1 (@($harness.State.Events | Where-Object { $_ -ceq 'start' }).Count) 'a restored NIC must fail before starting the restored target'
    }
}

$cleanupHarness = New-AlbumenCredentialRecoveryFaultHarness -Fault 'cleanup-aggregate'
$cleanupFailureMessage = $null
try {
    Invoke-AlbumenGoldenVmCredentialRecovery `
        -VMName 'PhotoSift-Verification' `
        -RecoveryCheckpointName 'Clean-Evaluation-Base' `
        -TargetCheckpointName 'Credential-Evaluation-Base' `
        -Credential $credential `
        -Deadline ([System.DateTimeOffset]::Now.AddMinutes(1)) `
        -Operations $cleanupHarness.Operations
}
catch {
    $cleanupFailureMessage = $_.Exception.Message
}
Assert-Equal `
    $true `
    ($cleanupFailureMessage -match 'sealed baseline inspection failure' -and
        $cleanupFailureMessage -match 'close cleanup failure' -and
        $cleanupFailureMessage -match 'VM cleanup failure' -and
        $cleanupFailureMessage -match 'checkpoint cleanup failure') `
    'credential recovery must aggregate the primary and every cleanup failure'
Assert-Equal 'Off' $cleanupHarness.State.VMState 'cleanup aggregation must still leave the VM Off'
Assert-Equal 'Credential-Evaluation-Base' ($cleanupHarness.State.RemovalNames -join ',') 'cleanup aggregation must target only the incomplete credential checkpoint'

$recoveryProbe = Invoke-AlbumenProcess `
    -FilePath (Get-Command pwsh.exe -ErrorAction Stop).Source `
    -Arguments @(
        '-NoProfile',
        '-File',
        (Join-Path $repositoryRoot 'eng\verification\Repair-AlbumenGoldenVmCredential.ps1')) `
    -WorkingDirectory $repositoryRoot
Assert-Equal 1 $recoveryProbe.ExitCode 'a non-elevated credential recovery probe must fail closed'
Assert-Equal `
    $true `
    ($recoveryProbe.StandardError -match 'Run Repair-AlbumenGoldenVmCredential.ps1 from an elevated PowerShell process' -and
        $recoveryProbe.StandardError -notmatch 'Get-Credential|Get-VM|provider') `
    'elevation must be the first unmet credential recovery contract'

Write-Output "Passed: $script:passed Failed: 0"
