[CmdletBinding()]
param(
    # Empty folder that receives the rebuilt VM's configuration and disks.
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$NewVmRoot,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$VMName = 'PhotoSift-Verification'
)

<#
.SYNOPSIS
Rebuilds an imported verification VM whose virtual TPM cannot be unlocked.

.DESCRIPTION
A vTPM's key protector is bound to the guardian certificates of the Windows
installation that created it. After a host reinstall the imported VM fails
with "The key protector could not be unwrapped" and cannot start.

The guest disks are still intact, so this script flattens the disk state of
every checkpoint into standalone VHDX files under $NewVmRoot, unregisters the
old VM, creates a new Generation 2 VM with the
same name, hardware, Secure Boot and a fresh vTPM, and recreates the
checkpoints with their original names and order. The guest does not use
BitLocker, so a fresh TPM does not lock it out. The new VM has no network
adapter, like the original.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Repair-PhotoSiftVerificationVmTpm.ps1 from an elevated PowerShell process.'
}
Import-Module Hyper-V -ErrorAction Stop

$oldVm = Get-VM -Name $VMName -ErrorAction Stop
if ([string]$oldVm.State -cne 'Off') {
    throw "VM '$VMName' must be Off."
}
if (@(Get-VMHardDiskDrive -VMName $VMName).Count -ne 1) {
    throw "VM '$VMName' must have exactly one virtual hard disk."
}

$newRoot = [System.IO.Path]::GetFullPath($NewVmRoot)
if ((Test-Path -LiteralPath $newRoot) -and @(Get-ChildItem -LiteralPath $newRoot -Force).Count -ne 0) {
    throw "NewVmRoot must be empty or absent: $newRoot"
}
$diskRoot = Join-Path $newRoot 'Disk'
$configRoot = Join-Path $newRoot 'Configuration'
New-Item -ItemType Directory -Path $diskRoot, $configRoot -Force | Out-Null

# Order checkpoints root-first along their single parent chain.
$snapshots = @(Get-VMSnapshot -VMName $VMName)
$ordered = New-Object System.Collections.Generic.List[object]
$current = @($snapshots | Where-Object { -not $_.ParentSnapshotName })
if ($current.Count -ne 1) {
    throw "Expected one root checkpoint; found $($current.Count)."
}
while ($current.Count -eq 1) {
    $ordered.Add($current[0])
    $name = $current[0].Name
    $current = @($snapshots | Where-Object { $_.ParentSnapshotName -ceq $name })
}
if ($ordered.Count -ne $snapshots.Count) {
    throw 'Checkpoints do not form a single chain; this script only handles a linear tree.'
}

$hardware = [pscustomobject]@{
    MemoryStartupBytes = $oldVm.MemoryStartup
    DynamicMemory = $oldVm.DynamicMemoryEnabled
    ProcessorCount = $oldVm.ProcessorCount
    SecureBootTemplate = (Get-VMFirmware -VMName $VMName).SecureBootTemplate
}

$flattened = foreach ($snapshot in $ordered) {
    $source = (Get-VMHardDiskDrive -VMSnapshot $snapshot).Path
    $target = Join-Path $diskRoot "$($snapshot.Name).vhdx"
    Write-Host "Flattening '$($snapshot.Name)': $source -> $target"
    Convert-VHD -Path $source -DestinationPath $target -VHDType Dynamic
    [pscustomobject]@{ Name = $snapshot.Name; Disk = $target }
}

# Removing a VM with checkpoints makes Hyper-V merge its checkpoint disks into
# the base disk and delete its configuration. Everything needed is already in
# the flattened copies, so run this against a copy of the backup, never the only one.
Write-Output "Removing the old VM registration '$VMName'."
Remove-VM -Name $VMName -Force

$vm = New-VM -Name $VMName -Generation 2 -MemoryStartupBytes $hardware.MemoryStartupBytes `
    -VHDPath $flattened[0].Disk -Path $configRoot -BootDevice VHD
Set-VM -Name $VMName -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction TurnOff
Set-VMMemory -VMName $VMName -DynamicMemoryEnabled $hardware.DynamicMemory -StartupBytes $hardware.MemoryStartupBytes
Set-VMProcessor -VMName $VMName -Count $hardware.ProcessorCount
Set-VMFirmware -VMName $VMName -EnableSecureBoot On -SecureBootTemplate $hardware.SecureBootTemplate
Set-VMKeyProtector -VMName $VMName -NewLocalKeyProtector
Enable-VMTPM -VMName $VMName
Get-VMNetworkAdapter -VMName $VMName | Remove-VMNetworkAdapter -Confirm:$false
if (@(Get-VMNetworkAdapter -VMName $VMName).Count -ne 0) {
    throw 'The rebuilt VM still has a virtual network adapter.'
}

# Each checkpoint captures the disk attached at that moment. Attach the next
# flattened state, then checkpoint it under the original name.
for ($i = 0; $i -lt $flattened.Count; $i++) {
    if ($i -gt 0) {
        $drive = Get-VMHardDiskDrive -VMName $VMName
        $orphan = $drive.Path
        $drive | Set-VMHardDiskDrive -Path $flattened[$i].Disk
        Remove-Item -LiteralPath $orphan -Force
    }
    Checkpoint-VM -Name $VMName -SnapshotName $flattened[$i].Name
    Write-Output "Created checkpoint '$($flattened[$i].Name)'."
}

[pscustomobject]@{
    VMName = $VMName
    State = [string](Get-VM -Name $VMName).State
    Path = (Get-VM -Name $VMName).Path
    NetworkAdapters = @(Get-VMNetworkAdapter -VMName $VMName).Count
    TpmEnabled = (Get-VMSecurity -VMName $VMName).TpmEnabled
    Checkpoints = (Get-VMSnapshot -VMName $VMName | ForEach-Object Name) -join ', '
} | Format-List
