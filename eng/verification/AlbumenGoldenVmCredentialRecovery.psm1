Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-AlbumenCredentialRecoveryCheckpointSet {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Checkpoints,

        [Parameter(Mandatory)]
        [string]$RecoveryCheckpointName,

        [Parameter(Mandatory)]
        [string]$TargetCheckpointName,

        [Parameter(Mandatory)]
        [ValidateRange(0, 1)]
        [int]$ExpectedTargetCount,

        [Parameter(Mandatory)]
        [string]$Context
    )

    $expectedCounts = [ordered]@{
        $RecoveryCheckpointName = 1
        $TargetCheckpointName = $ExpectedTargetCount
    }
    foreach ($checkpointName in $expectedCounts.Keys) {
        $matching = @($Checkpoints | Where-Object {
            $nameProperty = $_.PSObject.Properties['Name']
            $null -ne $nameProperty -and
                [string]$nameProperty.Value -ceq $checkpointName
        })
        $expectedCount = [int]$expectedCounts[$checkpointName]
        if ($matching.Count -ne $expectedCount) {
            throw "Golden VM must contain exactly $expectedCount '$checkpointName' checkpoint(s) $Context."
        }
    }
}

function Assert-AlbumenGoldenVmCredentialRecoveryContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedVMName,

        [Parameter(Mandatory)]
        [string]$RecoveryCheckpointName,

        [Parameter(Mandatory)]
        [string]$TargetCheckpointName,

        [Parameter(Mandatory)]
        [object]$VM,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Checkpoints,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$NetworkAdapters,

        [Parameter(Mandatory)]
        [object]$Credential,

        [Parameter(Mandatory)]
        [bool]$IsElevated
    )

    if (-not $IsElevated) {
        throw 'Golden VM credential recovery must run from an elevated PowerShell process.'
    }
    if ($RecoveryCheckpointName -ceq $TargetCheckpointName) {
        throw 'The immutable recovery checkpoint and additive credential checkpoint must have different names.'
    }

    $vmNameProperty = $VM.PSObject.Properties['Name']
    $vmStateProperty = $VM.PSObject.Properties['State']
    if ($null -eq $vmNameProperty -or
        [string]$vmNameProperty.Value -cne $ExpectedVMName) {
        throw "Hyper-V returned a VM that did not match '$ExpectedVMName'."
    }
    if ($null -eq $vmStateProperty -or [string]$vmStateProperty.Value -cne 'Off') {
        throw "Golden VM '$ExpectedVMName' must be Off before credential recovery."
    }
    if (@($NetworkAdapters).Count -ne 0) {
        throw "Golden VM '$ExpectedVMName' must have zero virtual network adapters."
    }
    if ($Credential -isnot [System.Management.Automation.PSCredential]) {
        throw 'Golden VM credential recovery requires an in-memory PowerShell Direct PSCredential.'
    }

    Assert-AlbumenCredentialRecoveryCheckpointSet `
        -Checkpoints $Checkpoints `
        -RecoveryCheckpointName $RecoveryCheckpointName `
        -TargetCheckpointName $TargetCheckpointName `
        -ExpectedTargetCount 0 `
        -Context "before credential recovery for '$ExpectedVMName'"

    return [pscustomobject][ordered]@{
        vmName = $ExpectedVMName
        recoveryCheckpointName = $RecoveryCheckpointName
        targetCheckpointName = $TargetCheckpointName
        vmState = 'Off'
        networkAdapterCount = 0
    }
}

function Assert-AlbumenCredentialRecoveryVmState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Contract,

        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [string]$OperationName
    )

    if ([string]$Contract.vmState -cne 'Off' -or
        [int]$Contract.networkAdapterCount -ne 0) {
        throw "Golden VM '$VMName' must be Off with zero virtual NICs after $OperationName."
    }
}

function Assert-AlbumenCredentialRecoveryGuestBaseline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Baseline,

        [Parameter(Mandatory)]
        [string]$CheckpointContext
    )

    $albumenRootProperty = $Baseline.PSObject.Properties['albumenRootExists']
    if ($null -eq $albumenRootProperty -or
        [bool]$albumenRootProperty.Value) {
        throw "Guest state '$CheckpointContext' is not a clean evaluation baseline because C:\PhotoSift exists."
    }
}

function Invoke-AlbumenGoldenVmCredentialRecovery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [string]$RecoveryCheckpointName,

        [Parameter(Mandatory)]
        [string]$TargetCheckpointName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline,

        [Parameter(Mandatory)]
        [hashtable]$Operations
    )

    $requiredOperations = @(
        'InspectVmContract',
        'StartVM',
        'WaitForVM',
        'OpenSession',
        'InspectGuestBaseline',
        'ShutdownGuest',
        'CloseSession',
        'WaitForPowerOff',
        'CreateTargetCheckpoint',
        'ReadCheckpoints',
        'RestoreTargetCheckpoint',
        'RemoveTargetCheckpoint',
        'StopVM')
    foreach ($operationName in $requiredOperations) {
        if (-not $Operations.ContainsKey($operationName) -or
            $Operations[$operationName] -isnot [scriptblock]) {
            throw "Golden VM credential recovery operation '$operationName' was not supplied."
        }
    }

    $session = $null
    $targetCreateAttempted = $false
    $targetCreated = $false
    $completed = $false
    $postCheckpointContract = $null
    $failures = New-Object 'System.Collections.Generic.List[string]'
    try {
        [void](Get-AlbumenRemainingSeconds -Deadline $Deadline -OperationName 'current-state VM inspection')
        $initialContract = & $Operations.InspectVmContract $VMName $Deadline
        Assert-AlbumenCredentialRecoveryVmState `
            -Contract $initialContract `
            -VMName $VMName `
            -OperationName 'initial inspection'

        & $Operations.StartVM $VMName $Deadline
        $waitSeconds = [System.Math]::Min(
            120,
            (Get-AlbumenRemainingSeconds -Deadline $Deadline -OperationName 'current-state VM heartbeat'))
        & $Operations.WaitForVM $VMName $waitSeconds $Deadline
        $session = & $Operations.OpenSession `
            $VMName `
            'current VM state (no checkpoint restored)' `
            $Credential `
            $Deadline
        if ($null -eq $session) {
            throw "PowerShell Direct returned no session for the current state of '$VMName'."
        }
        $baseline = & $Operations.InspectGuestBaseline $session $Deadline
        Assert-AlbumenCredentialRecoveryGuestBaseline `
            -Baseline $baseline `
            -CheckpointContext 'current VM state (no checkpoint restored)'

        & $Operations.ShutdownGuest $session $Deadline
        & $Operations.CloseSession $session
        $session = $null
        $powerOffWaitSeconds = [System.Math]::Min(
            120,
            (Get-AlbumenRemainingSeconds -Deadline $Deadline -OperationName 'credential baseline power off'))
        & $Operations.WaitForPowerOff $VMName $powerOffWaitSeconds $Deadline

        $checkpointsBeforeCreate = @(& $Operations.ReadCheckpoints $VMName $Deadline)
        Assert-AlbumenCredentialRecoveryCheckpointSet `
            -Checkpoints $checkpointsBeforeCreate `
            -RecoveryCheckpointName $RecoveryCheckpointName `
            -TargetCheckpointName $TargetCheckpointName `
            -ExpectedTargetCount 0 `
            -Context 'immediately before credential checkpoint creation'
        $targetCreateAttempted = $true
        & $Operations.CreateTargetCheckpoint $VMName $TargetCheckpointName $Deadline
        $targetCreated = $true
        $checkpoints = @(& $Operations.ReadCheckpoints $VMName $Deadline)
        Assert-AlbumenCredentialRecoveryCheckpointSet `
            -Checkpoints $checkpoints `
            -RecoveryCheckpointName $RecoveryCheckpointName `
            -TargetCheckpointName $TargetCheckpointName `
            -ExpectedTargetCount 1 `
            -Context 'after credential checkpoint creation'

        & $Operations.RestoreTargetCheckpoint $VMName $TargetCheckpointName $Deadline
        $restoredContract = & $Operations.InspectVmContract $VMName $Deadline
        Assert-AlbumenCredentialRecoveryVmState `
            -Contract $restoredContract `
            -VMName $VMName `
            -OperationName "restoring '$TargetCheckpointName'"
        & $Operations.StartVM $VMName $Deadline
        $validationWaitSeconds = [System.Math]::Min(
            120,
            (Get-AlbumenRemainingSeconds -Deadline $Deadline -OperationName 'credential checkpoint validation heartbeat'))
        & $Operations.WaitForVM $VMName $validationWaitSeconds $Deadline
        $session = & $Operations.OpenSession `
            $VMName `
            $TargetCheckpointName `
            $Credential `
            $Deadline
        if ($null -eq $session) {
            throw "PowerShell Direct returned no session for '$TargetCheckpointName'."
        }
        $sealedBaseline = & $Operations.InspectGuestBaseline $session $Deadline
        Assert-AlbumenCredentialRecoveryGuestBaseline `
            -Baseline $sealedBaseline `
            -CheckpointContext $TargetCheckpointName
        & $Operations.ShutdownGuest $session $Deadline
        & $Operations.CloseSession $session
        $session = $null
        $validationPowerOffSeconds = [System.Math]::Min(
            120,
            (Get-AlbumenRemainingSeconds -Deadline $Deadline -OperationName 'credential checkpoint validation power off'))
        & $Operations.WaitForPowerOff $VMName $validationPowerOffSeconds $Deadline
        & $Operations.RestoreTargetCheckpoint $VMName $TargetCheckpointName $Deadline
        $postCheckpointContract = & $Operations.InspectVmContract $VMName $Deadline
        Assert-AlbumenCredentialRecoveryVmState `
            -Contract $postCheckpointContract `
            -VMName $VMName `
            -OperationName "resealing '$TargetCheckpointName'"
        $completed = $true
    }
    catch {
        $failures.Add($_.Exception.ToString())
    }
    finally {
        if ($null -ne $session) {
            try {
                & $Operations.CloseSession $session
            }
            catch {
                $failures.Add("Could not close the PowerShell Direct session: $($_.Exception)")
            }
        }
        if (-not $completed) {
            try {
                & $Operations.StopVM $VMName
            }
            catch {
                $failures.Add("Could not stop Golden VM '$VMName': $($_.Exception)")
            }
            if ($targetCreateAttempted) {
                try {
                    $checkpointsAfterFailure = @(& $Operations.ReadCheckpoints $VMName)
                    $matchingTargets = @($checkpointsAfterFailure | Where-Object {
                        $nameProperty = $_.PSObject.Properties['Name']
                        $null -ne $nameProperty -and
                            [string]$nameProperty.Value -ceq $TargetCheckpointName
                    })
                    if ($matchingTargets.Count -eq 1) {
                        & $Operations.RemoveTargetCheckpoint $VMName $TargetCheckpointName
                        $targetCreated = $false
                    }
                    elseif ($matchingTargets.Count -gt 1) {
                        throw "Golden VM contains multiple incomplete '$TargetCheckpointName' checkpoints."
                    }
                }
                catch {
                    $failures.Add("Could not reconcile incomplete credential checkpoint '$TargetCheckpointName': $($_.Exception)")
                }
                try {
                    $checkpointsAfterCleanup = @(& $Operations.ReadCheckpoints $VMName)
                    Assert-AlbumenCredentialRecoveryCheckpointSet `
                        -Checkpoints $checkpointsAfterCleanup `
                        -RecoveryCheckpointName $RecoveryCheckpointName `
                        -TargetCheckpointName $TargetCheckpointName `
                        -ExpectedTargetCount 0 `
                        -Context 'after failed credential recovery cleanup'
                }
                catch {
                    $failures.Add("Golden VM post-failure credential checkpoint contract failed: $($_.Exception)")
                }
            }
        }
    }

    if ($failures.Count -gt 0) {
        throw ($failures -join [System.Environment]::NewLine)
    }

    return [pscustomobject][ordered]@{
        vmName = $VMName
        recoveryCheckpointName = $RecoveryCheckpointName
        targetCheckpointName = $TargetCheckpointName
        postCheckpointVmState = [string]$postCheckpointContract.vmState
        postCheckpointNetworkAdapterCount = [int]$postCheckpointContract.networkAdapterCount
    }
}

Export-ModuleMember -Function `
    Assert-AlbumenGoldenVmCredentialRecoveryContract, `
    Invoke-AlbumenGoldenVmCredentialRecovery
