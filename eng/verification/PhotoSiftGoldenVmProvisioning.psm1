Set-StrictMode -Version Latest

$verificationModulePath = Join-Path $PSScriptRoot 'PhotoSiftVerification.psm1'
Import-Module $verificationModulePath

function Get-PhotoSiftDirectoryInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.Directory]::Exists($resolvedPath)) {
        throw "Toolchain directory does not exist: $resolvedPath"
    }

    $files = @(Get-ChildItem `
        -LiteralPath $resolvedPath `
        -File `
        -Recurse `
        -Force `
        -ErrorAction Stop)
    [long]$totalBytes = 0
    foreach ($file in $files) {
        $totalBytes += [long]$file.Length
    }

    return [pscustomobject][ordered]@{
        path = $resolvedPath
        fileCount = $files.Count
        totalBytes = $totalBytes
    }
}

function Assert-PhotoSiftToolchainPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SdkPath,

        [Parameter(Mandatory)]
        [string]$PackagesPath
    )

    $resolvedSdkPath = [System.IO.Path]::GetFullPath($SdkPath)
    $resolvedPackagesPath = [System.IO.Path]::GetFullPath($PackagesPath)
    $dotnetPath = Join-Path $resolvedSdkPath 'dotnet.exe'
    $sdkVersionPath = Join-Path $resolvedSdkPath 'sdk\10.0.400'
    $desktopRuntimePath = Join-Path `
        $resolvedSdkPath `
        'shared\Microsoft.WindowsDesktop.App\10.0.11'

    if (-not [System.IO.File]::Exists($dotnetPath)) {
        throw "Pinned SDK executable does not exist: $dotnetPath"
    }
    if (-not [System.IO.Directory]::Exists($sdkVersionPath)) {
        throw "Pinned SDK 10.0.400 does not exist beneath: $resolvedSdkPath"
    }
    if (-not [System.IO.Directory]::Exists($desktopRuntimePath)) {
        throw "Pinned Windows Desktop Runtime 10.0.11 does not exist beneath: $resolvedSdkPath"
    }

    $sdkInventory = Get-PhotoSiftDirectoryInventory -Path $resolvedSdkPath
    $packagesInventory = Get-PhotoSiftDirectoryInventory -Path $resolvedPackagesPath
    if ($sdkInventory.fileCount -eq 0) {
        throw "Pinned SDK payload is empty: $resolvedSdkPath"
    }
    if ($packagesInventory.fileCount -eq 0) {
        throw "Locked NuGet package cache is empty: $resolvedPackagesPath"
    }

    return [pscustomobject][ordered]@{
        sdkPath = $resolvedSdkPath
        packagesPath = $resolvedPackagesPath
        sdkVersion = '10.0.400'
        desktopRuntimeVersion = '10.0.11'
        sdkInventory = $sdkInventory
        packagesInventory = $packagesInventory
    }
}

function Assert-PhotoSiftGoldenVmProvisioningContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedVMName,

        [Parameter(Mandatory)]
        [string]$RecoveryCheckpointName,

        [Parameter(Mandatory)]
        [string]$SourceCheckpointName,

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
        [AllowNull()]
        [object]$Credential,

        [Parameter(Mandatory)]
        [bool]$IsElevated
    )

    if (@(@($RecoveryCheckpointName, $SourceCheckpointName, $TargetCheckpointName) |
            Select-Object -Unique).Count -ne 3) {
        throw 'Recovery, source, and target Golden VM checkpoints must have different names.'
    }

    Assert-PhotoSiftGoldenVmContract `
        -ExpectedVMName $ExpectedVMName `
        -ExpectedCheckpointName $SourceCheckpointName `
        -VM $VM `
        -Checkpoints $Checkpoints `
        -NetworkAdapters $NetworkAdapters `
        -Credential $Credential `
        -IsElevated $IsElevated | Out-Null

    $matchingRecovery = @($Checkpoints | Where-Object {
        $nameProperty = $_.PSObject.Properties['Name']
        $null -ne $nameProperty -and
            [string]$nameProperty.Value -ceq $RecoveryCheckpointName
    })
    if ($matchingRecovery.Count -ne 1) {
        throw "Golden VM '$ExpectedVMName' must preserve exactly one '$RecoveryCheckpointName' recovery checkpoint."
    }

    $matchingTargets = @($Checkpoints | Where-Object {
        $nameProperty = $_.PSObject.Properties['Name']
        $null -ne $nameProperty -and
            [string]$nameProperty.Value -ceq $TargetCheckpointName
    })
    if ($matchingTargets.Count -ne 0) {
        throw "Golden VM '$ExpectedVMName' already contains target checkpoint '$TargetCheckpointName'."
    }

    return [pscustomobject][ordered]@{
        vmName = $ExpectedVMName
        recoveryCheckpointName = $RecoveryCheckpointName
        sourceCheckpointName = $SourceCheckpointName
        targetCheckpointName = $TargetCheckpointName
        vmState = 'Off'
        networkAdapterCount = 0
    }
}

function Assert-PhotoSiftProvisioningCheckpointSet {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Checkpoints,

        [Parameter(Mandatory)]
        [string]$RecoveryCheckpointName,

        [Parameter(Mandatory)]
        [string]$SourceCheckpointName,

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
        $SourceCheckpointName = 1
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

function Assert-PhotoSiftInventoryMatch {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual
    )

    if ([int]$Expected.fileCount -le 0 -or
        [int]$Actual.fileCount -ne [int]$Expected.fileCount -or
        [long]$Actual.totalBytes -ne [long]$Expected.totalBytes) {
        throw "$Name payload inventory did not match after Golden VM copy."
    }
}

function Assert-PhotoSiftProvisionedGuestPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$GuestPayload,

        [Parameter(Mandatory)]
        [object]$HostSdkInventory,

        [Parameter(Mandatory)]
        [object]$HostPackagesInventory
    )

    if ([string]$GuestPayload.sdkVersion -cne '10.0.400') {
        throw "Golden VM copied SDK version was '$($GuestPayload.sdkVersion)', expected '10.0.400'."
    }
    if ([string]$GuestPayload.desktopRuntimeVersion -cne '10.0.11') {
        throw "Golden VM copied Desktop Runtime version was '$($GuestPayload.desktopRuntimeVersion)', expected '10.0.11'."
    }
    Assert-PhotoSiftInventoryMatch `
        -Name 'SDK' `
        -Expected $HostSdkInventory `
        -Actual $GuestPayload.sdkInventory
    Assert-PhotoSiftInventoryMatch `
        -Name 'NuGet package cache' `
        -Expected $HostPackagesInventory `
        -Actual $GuestPayload.packagesInventory
}

function Invoke-PhotoSiftGoldenVmProvisioning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$VMName,

        [Parameter(Mandatory)]
        [string]$RecoveryCheckpointName,

        [Parameter(Mandatory)]
        [string]$SourceCheckpointName,

        [Parameter(Mandatory)]
        [string]$TargetCheckpointName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential]$Credential,

        [Parameter(Mandatory)]
        [string]$SdkPath,

        [Parameter(Mandatory)]
        [string]$PackagesPath,

        [Parameter(Mandatory)]
        [object]$HostSdkInventory,

        [Parameter(Mandatory)]
        [object]$HostPackagesInventory,

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline,

        [Parameter(Mandatory)]
        [hashtable]$Operations
    )

    $requiredOperations = @(
        'RestoreSourceCheckpoint',
        'RestoreTargetCheckpoint',
        'InspectVmContract',
        'StartVM',
        'WaitForVM',
        'OpenSession',
        'PrepareGuest',
        'CopySdk',
        'CopyPackages',
        'InspectGuestPayload',
        'ShutdownGuest',
        'CloseSession',
        'WaitForPowerOff',
        'CreateTargetCheckpoint',
        'ReadCheckpoints',
        'RemoveTargetCheckpoint',
        'StopVM')
    foreach ($operationName in $requiredOperations) {
        if (-not $Operations.ContainsKey($operationName) -or
            $Operations[$operationName] -isnot [scriptblock]) {
            throw "Golden VM provisioning operation '$operationName' was not supplied."
        }
    }

    $resolvedSdkPath = [System.IO.Path]::GetFullPath($SdkPath)
    $resolvedPackagesPath = [System.IO.Path]::GetFullPath($PackagesPath)
    $guestToolchainRoot = 'C:\PhotoSift\golden'
    $guestSdkPath = Join-Path $guestToolchainRoot 'sdk'
    $guestPackagesPath = Join-Path $guestToolchainRoot 'packages'
    $session = $null
    $sessionClosed = $false
    $targetCreateAttempted = $false
    $targetCreated = $false
    $completed = $false
    $postRestoreContract = $null
    $postCheckpointContract = $null
    $failures = New-Object 'System.Collections.Generic.List[string]'

    try {
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning source checkpoint restore')
        & $Operations.RestoreSourceCheckpoint $VMName $SourceCheckpointName $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning post-restore VM contract inspection')
        $postRestoreContract = & $Operations.InspectVmContract $VMName $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning VM startup')
        & $Operations.StartVM $VMName $Deadline
        $waitSeconds = [System.Math]::Min(
            120,
            (Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning VM heartbeat'))
        & $Operations.WaitForVM $VMName $waitSeconds $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning PowerShell Direct connection')
        $session = & $Operations.OpenSession `
            $VMName `
            $SourceCheckpointName `
            $Credential `
            $Deadline
        if ($null -eq $session) {
            throw "PowerShell Direct returned no session for '$VMName'."
        }

        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning guest preparation')
        & $Operations.PrepareGuest $session $guestToolchainRoot $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning SDK copy')
        & $Operations.CopySdk $session $resolvedSdkPath $guestSdkPath $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning package copy')
        & $Operations.CopyPackages $session $resolvedPackagesPath $guestPackagesPath $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioned payload inspection')
        $guestPayload = & $Operations.InspectGuestPayload `
            $session `
            $guestSdkPath `
            $guestPackagesPath `
            $Deadline
        if ($null -eq $guestPayload) {
            throw 'Golden VM returned no copied toolchain inventory.'
        }
        Assert-PhotoSiftProvisionedGuestPayload `
            -GuestPayload $guestPayload `
            -HostSdkInventory $HostSdkInventory `
            -HostPackagesInventory $HostPackagesInventory

        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning guest shutdown')
        & $Operations.ShutdownGuest $session $Deadline
        & $Operations.CloseSession $session
        $sessionClosed = $true
        $session = $null
        $powerOffWaitSeconds = [System.Math]::Min(
            120,
            (Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'provisioning VM power off'))
        & $Operations.WaitForPowerOff $VMName $powerOffWaitSeconds $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'target checkpoint creation')
        $checkpointsBeforeCreate = @(& $Operations.ReadCheckpoints $VMName $Deadline)
        Assert-PhotoSiftProvisioningCheckpointSet `
            -Checkpoints $checkpointsBeforeCreate `
            -RecoveryCheckpointName $RecoveryCheckpointName `
            -SourceCheckpointName $SourceCheckpointName `
            -TargetCheckpointName $TargetCheckpointName `
            -ExpectedTargetCount 0 `
            -Context 'immediately before target creation'
        $targetCreateAttempted = $true
        & $Operations.CreateTargetCheckpoint $VMName $TargetCheckpointName $Deadline
        $targetCreated = $true

        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'checkpoint verification')
        $checkpoints = @(& $Operations.ReadCheckpoints $VMName $Deadline)
        Assert-PhotoSiftProvisioningCheckpointSet `
            -Checkpoints $checkpoints `
            -RecoveryCheckpointName $RecoveryCheckpointName `
            -SourceCheckpointName $SourceCheckpointName `
            -TargetCheckpointName $TargetCheckpointName `
            -ExpectedTargetCount 1 `
            -Context 'after provisioning'
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'target checkpoint restore validation')
        & $Operations.RestoreTargetCheckpoint $VMName $TargetCheckpointName $Deadline
        $postCheckpointContract = & $Operations.InspectVmContract $VMName $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'target checkpoint validation startup')
        & $Operations.StartVM $VMName $Deadline
        $targetWaitSeconds = [System.Math]::Min(
            120,
            (Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'target checkpoint validation heartbeat'))
        & $Operations.WaitForVM $VMName $targetWaitSeconds $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'target checkpoint authentication')
        $session = & $Operations.OpenSession `
            $VMName `
            $TargetCheckpointName `
            $Credential `
            $Deadline
        $sessionClosed = $false
        if ($null -eq $session) {
            throw "PowerShell Direct returned no session for target checkpoint '$TargetCheckpointName'."
        }
        $sealedPayload = & $Operations.InspectGuestPayload `
            $session `
            $guestSdkPath `
            $guestPackagesPath `
            $Deadline
        if ($null -eq $sealedPayload) {
            throw 'The target checkpoint returned no sealed toolchain inventory.'
        }
        Assert-PhotoSiftProvisionedGuestPayload `
            -GuestPayload $sealedPayload `
            -HostSdkInventory $HostSdkInventory `
            -HostPackagesInventory $HostPackagesInventory

        & $Operations.ShutdownGuest $session $Deadline
        & $Operations.CloseSession $session
        $sessionClosed = $true
        $session = $null
        $targetPowerOffWaitSeconds = [System.Math]::Min(
            120,
            (Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'target checkpoint validation power off'))
        & $Operations.WaitForPowerOff $VMName $targetPowerOffWaitSeconds $Deadline
        [void](Get-PhotoSiftRemainingSeconds -Deadline $Deadline -OperationName 'target checkpoint reseal restore')
        & $Operations.RestoreTargetCheckpoint $VMName $TargetCheckpointName $Deadline
        $postCheckpointContract = & $Operations.InspectVmContract $VMName $Deadline
        $finalCheckpoints = @(& $Operations.ReadCheckpoints $VMName $Deadline)
        Assert-PhotoSiftProvisioningCheckpointSet `
            -Checkpoints $finalCheckpoints `
            -RecoveryCheckpointName $RecoveryCheckpointName `
            -SourceCheckpointName $SourceCheckpointName `
            -TargetCheckpointName $TargetCheckpointName `
            -ExpectedTargetCount 1 `
            -Context 'after target validation and reseal'
        $completed = $true
    }
    catch {
        $failures.Add($_.Exception.ToString())
    }
    finally {
        if ($null -ne $session -and -not $sessionClosed) {
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
                    $failures.Add("Could not reconcile incomplete target checkpoint '$TargetCheckpointName': $($_.Exception)")
                }
            }
            try {
                & $Operations.RestoreSourceCheckpoint $VMName $SourceCheckpointName
            }
            catch {
                $failures.Add("Could not restore source checkpoint '$SourceCheckpointName': $($_.Exception)")
            }
            try {
                $null = & $Operations.InspectVmContract $VMName
            }
            catch {
                $failures.Add("Golden VM post-failure contract failed: $($_.Exception)")
            }
            try {
                $checkpointsAfterCleanup = @(& $Operations.ReadCheckpoints $VMName)
                Assert-PhotoSiftProvisioningCheckpointSet `
                    -Checkpoints $checkpointsAfterCleanup `
                    -RecoveryCheckpointName $RecoveryCheckpointName `
                    -SourceCheckpointName $SourceCheckpointName `
                    -TargetCheckpointName $TargetCheckpointName `
                    -ExpectedTargetCount 0 `
                    -Context 'after failed provisioning cleanup'
            }
            catch {
                $failures.Add("Golden VM post-failure checkpoint contract failed: $($_.Exception)")
            }
        }
    }

    if ($failures.Count -gt 0) {
        throw ($failures -join [System.Environment]::NewLine)
    }

    return [pscustomobject][ordered]@{
        vmName = $VMName
        recoveryCheckpointName = $RecoveryCheckpointName
        sourceCheckpointName = $SourceCheckpointName
        targetCheckpointName = $TargetCheckpointName
        sdkFileCount = [int]$HostSdkInventory.fileCount
        packagesFileCount = [int]$HostPackagesInventory.fileCount
        postRestoreVmState = [string]$postRestoreContract.vmState
        postRestoreNetworkAdapterCount = [int]$postRestoreContract.networkAdapterCount
        postCheckpointVmState = [string]$postCheckpointContract.vmState
        postCheckpointNetworkAdapterCount = [int]$postCheckpointContract.networkAdapterCount
    }
}

Export-ModuleMember -Function `
    Get-PhotoSiftDirectoryInventory, `
    Assert-PhotoSiftToolchainPayload, `
    Assert-PhotoSiftGoldenVmProvisioningContract, `
    Invoke-PhotoSiftGoldenVmProvisioning
