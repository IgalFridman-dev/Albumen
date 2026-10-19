[CmdletBinding()]
param(
    # Folder holding a copy of the exported/backed-up VM, i.e. the folder that
    # contains Configuration\ and Disk\. Register a copy, never the only backup:
    # Hyper-V takes ownership of the files and checkpoints change them.
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$VmRoot,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$VMName = 'PhotoSift-Verification'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Import-AlbumenVerificationVm.ps1 from an elevated PowerShell process.'
}
Import-Module Hyper-V -ErrorAction Stop

if (@(Get-VM -Name $VMName -ErrorAction SilentlyContinue).Count -ne 0) {
    throw "A VM named '$VMName' is already registered. Nothing was changed."
}

$resolvedRoot = (Resolve-Path -LiteralPath $VmRoot).Path
$configurations = @(Get-ChildItem -LiteralPath (Join-Path $resolvedRoot 'Configuration') `
    -Recurse -Filter '*.vmcx' |
    Where-Object { $_.Directory.Name -eq 'Virtual Machines' })
if ($configurations.Count -ne 1) {
    throw "Expected one VM configuration under $resolvedRoot; found $($configurations.Count)."
}

$report = Compare-VM -Path $configurations[0].FullName -Register

# A backup restored to a different folder or drive still records the original
# disk paths for the VM and every checkpoint. Point each missing disk at the
# file with the same name under $VmRoot\Disk.
$diskDirectory = Join-Path $resolvedRoot 'Disk'
foreach ($incompatibility in @($report.Incompatibilities | Where-Object MessageId -eq 40010)) {
    $drive = $incompatibility.Source
    $relocated = Join-Path $diskDirectory ([System.IO.Path]::GetFileName($drive.Path))
    if (-not (Test-Path -LiteralPath $relocated -PathType Leaf)) {
        throw "Disk '$($drive.Path)' is missing and was not found at '$relocated'."
    }
    Write-Output "Relocating disk: $($drive.Path) -> $relocated"
    $drive | Set-VMHardDiskDrive -Path $relocated
}
$report = Compare-VM -CompatibilityReport $report

if ($report.Incompatibilities) {
    $report.Incompatibilities | Format-Table MessageId, Message -AutoSize | Out-Host
    throw 'The VM configuration has incompatibilities on this host. Nothing was imported.'
}

Import-VM -CompatibilityReport $report | Out-Null
$vm = Get-VM -Name $VMName

$adapters = @(Get-VMNetworkAdapter -VMName $VMName)
if ($adapters.Count -ne 0) {
    throw "Imported VM has $($adapters.Count) virtual network adapter(s); the verification VM must have none."
}

[pscustomobject]@{
    VMName = $vm.Name
    State = [string]$vm.State
    ConfigurationVersion = $vm.Version
    Path = $vm.Path
    NetworkAdapters = $adapters.Count
    TpmEnabled = (Get-VMSecurity -VMName $VMName).TpmEnabled
    Checkpoints = (Get-VMSnapshot -VMName $VMName | ForEach-Object Name) -join ', '
} | Format-List
