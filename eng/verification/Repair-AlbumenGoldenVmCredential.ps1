[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$VMName = 'PhotoSift-Verification',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$RecoveryCheckpointName = 'Clean-Evaluation-Base',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$TargetCheckpointName = 'Credential-Evaluation-Base',

    [Parameter()]
    [System.Management.Automation.PSCredential]$Credential,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isElevated = $principal.IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    throw 'Run Repair-AlbumenGoldenVmCredential.ps1 from an elevated PowerShell process.'
}

$verificationModulePath = Join-Path $PSScriptRoot 'AlbumenVerification.psm1'
$recoveryModulePath = Join-Path $PSScriptRoot 'AlbumenGoldenVmCredentialRecovery.psm1'
Import-Module $verificationModulePath -Force
Import-Module $recoveryModulePath -Force

$resolvedCredential = Get-AlbumenGoldenVmCredential -Credential $Credential
$vm = Get-VM -Name $VMName -ErrorAction Stop
$checkpoints = @(Get-VMSnapshot -VMName $VMName -ErrorAction Stop)
$networkAdapters = @(Get-VMNetworkAdapter -VMName $VMName -ErrorAction Stop)
$contract = Assert-AlbumenGoldenVmCredentialRecoveryContract `
    -ExpectedVMName $VMName `
    -RecoveryCheckpointName $RecoveryCheckpointName `
    -TargetCheckpointName $TargetCheckpointName `
    -VM $vm `
    -Checkpoints $checkpoints `
    -NetworkAdapters $networkAdapters `
    -Credential $resolvedCredential `
    -IsElevated $isElevated
$deadline = [System.DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)

$inspectVmContract = {
    param($RequestedVMName, $RequestedDeadline)
    $currentVM = Get-VM -Name $RequestedVMName -ErrorAction Stop
    $currentNetworkAdapters = @(
        Get-VMNetworkAdapter -VMName $RequestedVMName -ErrorAction Stop)
    if ([string]$currentVM.State -cne 'Off') {
        throw "Golden VM '$RequestedVMName' must be Off at this recovery boundary."
    }
    if ($currentNetworkAdapters.Count -ne 0) {
        throw "Golden VM '$RequestedVMName' must have zero virtual network adapters."
    }
    [pscustomobject][ordered]@{
        vmState = 'Off'
        networkAdapterCount = 0
    }
}.GetNewClosure()
$startVM = {
    param($RequestedVMName, $RequestedDeadline)
    Start-VM -Name $RequestedVMName -ErrorAction Stop | Out-Null
}.GetNewClosure()
$waitForVM = {
    param($RequestedVMName, $TimeoutSeconds, $RequestedDeadline)
    Wait-VM `
        -Name $RequestedVMName `
        -For Heartbeat `
        -Timeout $TimeoutSeconds `
        -ErrorAction Stop | Out-Null
}.GetNewClosure()
$openSession = {
    param($RequestedVMName, $CheckpointContext, $RequestedCredential, $RequestedDeadline)
    $session = Connect-AlbumenPowerShellDirect `
        -VMName $RequestedVMName `
        -CheckpointName $CheckpointContext `
        -Credential $RequestedCredential `
        -Deadline $RequestedDeadline
    Write-Information `
        "PowerShell Direct authenticated PHOTOSIFTVM\PhotoSiftRunner for '$CheckpointContext'." `
        -InformationAction Continue
    $session
}.GetNewClosure()
$inspectGuestBaseline = {
    param($Session, $RequestedDeadline)
    Invoke-AlbumenPowerShellWithDeadline `
        -ScriptBlock {
            param($RemoteSession)
            Invoke-Command -Session $RemoteSession -ErrorAction Stop -ScriptBlock {
                [pscustomobject][ordered]@{
                    albumenRootExists = [System.IO.Directory]::Exists('C:\PhotoSift')
                }
            }
        } `
        -ArgumentList @($Session) `
        -Deadline $RequestedDeadline `
        -OperationName 'clean credential baseline inspection'
}.GetNewClosure()
$shutdownGuest = {
    param($Session, $RequestedDeadline)
    Invoke-AlbumenPowerShellWithDeadline `
        -ScriptBlock {
            param($RemoteSession)
            Invoke-Command -Session $RemoteSession -ErrorAction Stop -ScriptBlock {
                & (Join-Path $env:SystemRoot 'System32\shutdown.exe') /s /t 0 /d p:0:0
                if ($LASTEXITCODE -ne 0) {
                    throw "Guest shutdown request exited $LASTEXITCODE."
                }
            }
        } `
        -ArgumentList @($Session) `
        -Deadline $RequestedDeadline `
        -OperationName 'credential baseline guest shutdown' | Out-Null
}.GetNewClosure()
$closeSession = {
    param($Session)
    Remove-PSSession -Session $Session -ErrorAction Stop
}.GetNewClosure()
$waitForPowerOff = {
    param($RequestedVMName, $TimeoutSeconds, $RequestedDeadline)
    $powerOffDeadline = [System.DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    do {
        $currentVM = Get-VM -Name $RequestedVMName -ErrorAction Stop
        if ([string]$currentVM.State -ceq 'Off') {
            return
        }
        if ([System.DateTimeOffset]::Now -ge $powerOffDeadline) {
            throw "Golden VM '$RequestedVMName' did not shut down cleanly within $TimeoutSeconds seconds."
        }
        Start-Sleep -Seconds 1
    } while ($true)
}.GetNewClosure()
$createTargetCheckpoint = {
    param($RequestedVMName, $RequestedCheckpointName, $RequestedDeadline)
    Checkpoint-VM `
        -VMName $RequestedVMName `
        -SnapshotName $RequestedCheckpointName `
        -ErrorAction Stop | Out-Null
}.GetNewClosure()
$readCheckpoints = {
    param($RequestedVMName, $RequestedDeadline)
    @(Get-VMSnapshot -VMName $RequestedVMName -ErrorAction Stop)
}.GetNewClosure()
$restoreTargetCheckpoint = {
    param($RequestedVMName, $RequestedCheckpointName, $RequestedDeadline)
    Restore-VMSnapshot `
        -VMName $RequestedVMName `
        -Name $RequestedCheckpointName `
        -Confirm:$false `
        -ErrorAction Stop | Out-Null
}.GetNewClosure()
$removeTargetCheckpoint = {
    param($RequestedVMName, $RequestedCheckpointName)
    $target = Get-VMSnapshot `
        -VMName $RequestedVMName `
        -Name $RequestedCheckpointName `
        -ErrorAction Stop
    Remove-VMSnapshot -VMSnapshot $target -Confirm:$false -ErrorAction Stop
}.GetNewClosure()
$stopVM = {
    param($RequestedVMName)
    $currentVM = Get-VM -Name $RequestedVMName -ErrorAction Stop
    if ([string]$currentVM.State -cne 'Off') {
        Stop-VM -Name $RequestedVMName -TurnOff -Force -ErrorAction Stop | Out-Null
    }
}.GetNewClosure()

$operations = @{
    InspectVmContract = $inspectVmContract
    StartVM = $startVM
    WaitForVM = $waitForVM
    OpenSession = $openSession
    InspectGuestBaseline = $inspectGuestBaseline
    ShutdownGuest = $shutdownGuest
    CloseSession = $closeSession
    WaitForPowerOff = $waitForPowerOff
    CreateTargetCheckpoint = $createTargetCheckpoint
    ReadCheckpoints = $readCheckpoints
    RestoreTargetCheckpoint = $restoreTargetCheckpoint
    RemoveTargetCheckpoint = $removeTargetCheckpoint
    StopVM = $stopVM
}

Write-Output 'Recovering the Albumen Golden VM credential baseline.'
Write-Output "VM: $($contract.vmName)"
Write-Output 'No checkpoint will be restored before current-state authentication succeeds.'
Write-Output "The immutable '$($contract.recoveryCheckpointName)' checkpoint will be preserved."

$result = Invoke-AlbumenGoldenVmCredentialRecovery `
    -VMName $VMName `
    -RecoveryCheckpointName $RecoveryCheckpointName `
    -TargetCheckpointName $TargetCheckpointName `
    -Credential $resolvedCredential `
    -Deadline $deadline `
    -Operations $operations

Write-Output "Golden VM credential recovery passed: $($result.targetCheckpointName)"
Write-Output "The VM is Off and '$($result.recoveryCheckpointName)' remains available."
