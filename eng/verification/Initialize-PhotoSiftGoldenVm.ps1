[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$VMName = 'PhotoSift-Verification',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$SourceCheckpointName = 'Clean-Evaluation-Base',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$TargetCheckpointName = 'Toolchain-Evaluation-Base',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$SdkPath = (Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        'photosift-dotnet-sdk-10.0.400'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$PackagesPath = (Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        'photosift-nuget-packages'),

    [Parameter()]
    [System.Management.Automation.PSCredential]$Credential,

    [Parameter()]
    [ValidateRange(1, 240)]
    [int]$TimeoutMinutes = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verificationModulePath = Join-Path $PSScriptRoot 'PhotoSiftVerification.psm1'
$provisioningModulePath = Join-Path $PSScriptRoot 'PhotoSiftGoldenVmProvisioning.psm1'
Import-Module $verificationModulePath -Force
Import-Module $provisioningModulePath -Force

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isElevated = $principal.IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    throw 'Run Initialize-PhotoSiftGoldenVm.ps1 from an elevated PowerShell process.'
}

$resolvedCredential = $Credential
if ($null -eq $resolvedCredential) {
    $resolvedCredential = Get-Credential `
        -UserName 'PhotoSiftRunner' `
        -Message 'Enter the PhotoSiftRunner password to provision the Golden VM toolchain checkpoint.'
    if ($null -eq $resolvedCredential) {
        throw 'Golden VM credential prompt was cancelled.'
    }
}

$payload = Assert-PhotoSiftToolchainPayload `
    -SdkPath $SdkPath `
    -PackagesPath $PackagesPath
$vm = Get-VM -Name $VMName -ErrorAction Stop
$checkpoints = @(Get-VMSnapshot -VMName $VMName -ErrorAction Stop)
$networkAdapters = @(Get-VMNetworkAdapter -VMName $VMName -ErrorAction Stop)
$contract = Assert-PhotoSiftGoldenVmProvisioningContract `
    -ExpectedVMName $VMName `
    -SourceCheckpointName $SourceCheckpointName `
    -TargetCheckpointName $TargetCheckpointName `
    -VM $vm `
    -Checkpoints $checkpoints `
    -NetworkAdapters $networkAdapters `
    -Credential $resolvedCredential `
    -IsElevated $isElevated

$deadline = [System.DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)
$restoreSourceCheckpoint = {
    param($RequestedVMName, $RequestedCheckpointName)
    Restore-VMSnapshot `
        -VMName $RequestedVMName `
        -Name $RequestedCheckpointName `
        -Confirm:$false `
        -ErrorAction Stop | Out-Null
}.GetNewClosure()
$startVM = {
    param($RequestedVMName)
    Start-VM -Name $RequestedVMName -ErrorAction Stop | Out-Null
}.GetNewClosure()
$waitForVM = {
    param($RequestedVMName, $TimeoutSeconds)
    Wait-VM `
        -Name $RequestedVMName `
        -For Heartbeat `
        -Timeout $TimeoutSeconds `
        -ErrorAction Stop | Out-Null
}.GetNewClosure()
$openSession = {
    param($RequestedVMName, $RequestedCredential)
    Connect-PhotoSiftPowerShellDirect `
        -VMName $RequestedVMName `
        -Credential $RequestedCredential `
        -Deadline $deadline
}.GetNewClosure()
$prepareGuest = {
    param($Session, $GuestToolchainRoot)
    Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
        param($ToolchainRoot)
        if ([System.IO.Directory]::Exists($ToolchainRoot)) {
            Remove-Item -LiteralPath $ToolchainRoot -Recurse -Force -ErrorAction Stop
        }
        [System.IO.Directory]::CreateDirectory((Join-Path $ToolchainRoot 'sdk')) | Out-Null
        [System.IO.Directory]::CreateDirectory((Join-Path $ToolchainRoot 'packages')) | Out-Null
    } -ArgumentList $GuestToolchainRoot
}.GetNewClosure()
$copySdk = {
    param($Session, $RequestedSdkPath, $GuestSdkPath)
    foreach ($item in @(Get-ChildItem -LiteralPath $RequestedSdkPath -Force)) {
        Copy-Item `
            -LiteralPath $item.FullName `
            -Destination $GuestSdkPath `
            -Recurse `
            -Force `
            -ToSession $Session `
            -ErrorAction Stop
    }
}.GetNewClosure()
$copyPackages = {
    param($Session, $RequestedPackagesPath, $GuestPackagesPath)
    foreach ($item in @(Get-ChildItem -LiteralPath $RequestedPackagesPath -Force)) {
        Copy-Item `
            -LiteralPath $item.FullName `
            -Destination $GuestPackagesPath `
            -Recurse `
            -Force `
            -ToSession $Session `
            -ErrorAction Stop
    }
}.GetNewClosure()
$inspectGuestPayload = {
    param($Session, $GuestSdkPath, $GuestPackagesPath)
    Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
        param($SdkRoot, $PackagesRoot)

        function Get-DirectoryInventory {
            param([string]$Path)
            if (-not [System.IO.Directory]::Exists($Path)) {
                throw "Provisioned directory does not exist: $Path"
            }
            $files = @(Get-ChildItem `
                -LiteralPath $Path `
                -File `
                -Recurse `
                -Force `
                -ErrorAction Stop)
            [long]$totalBytes = 0
            foreach ($file in $files) {
                $totalBytes += [long]$file.Length
            }
            [pscustomobject]@{
                fileCount = $files.Count
                totalBytes = $totalBytes
            }
        }

        $dotnetPath = Join-Path $SdkRoot 'dotnet.exe'
        if (-not [System.IO.File]::Exists($dotnetPath)) {
            throw "Provisioned SDK executable does not exist: $dotnetPath"
        }
        $dotnetHome = 'C:\PhotoSift\provisioning-dotnet-home'
        [System.IO.Directory]::CreateDirectory($dotnetHome) | Out-Null
        $env:DOTNET_CLI_HOME = $dotnetHome
        $env:DOTNET_NOLOGO = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

        $sdkVersionOutput = @(& $dotnetPath --version)
        if ($LASTEXITCODE -ne 0 -or $sdkVersionOutput.Count -eq 0) {
            throw 'Provisioned dotnet --version failed.'
        }
        $sdkVersion = [string]$sdkVersionOutput[0]
        $runtimeOutput = @(& $dotnetPath --list-runtimes)
        if ($LASTEXITCODE -ne 0) {
            throw 'Provisioned dotnet --list-runtimes failed.'
        }
        $desktopRuntimeLine = @($runtimeOutput | Where-Object {
            $_ -match '^Microsoft\.WindowsDesktop\.App\s+10\.0\.11\s+'
        })
        if ($desktopRuntimeLine.Count -ne 1) {
            throw 'Provisioned SDK did not report Windows Desktop Runtime 10.0.11 exactly once.'
        }

        [pscustomobject][ordered]@{
            sdkVersion = $sdkVersion.Trim()
            desktopRuntimeVersion = '10.0.11'
            sdkInventory = Get-DirectoryInventory -Path $SdkRoot
            packagesInventory = Get-DirectoryInventory -Path $PackagesRoot
        }
    } -ArgumentList $GuestSdkPath, $GuestPackagesPath
}.GetNewClosure()
$shutdownGuest = {
    param($Session)
    Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
        & (Join-Path $env:SystemRoot 'System32\shutdown.exe') /s /t 0 /d p:0:0
        if ($LASTEXITCODE -ne 0) {
            throw "Guest shutdown request exited $LASTEXITCODE."
        }
    }
}.GetNewClosure()
$closeSession = {
    param($Session)
    Remove-PSSession -Session $Session -ErrorAction Stop
}.GetNewClosure()
$waitForPowerOff = {
    param($RequestedVMName, $TimeoutSeconds)
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
    param($RequestedVMName, $RequestedCheckpointName)
    Checkpoint-VM `
        -VMName $RequestedVMName `
        -SnapshotName $RequestedCheckpointName `
        -ErrorAction Stop | Out-Null
}.GetNewClosure()
$readCheckpoints = {
    param($RequestedVMName)
    @(Get-VMSnapshot -VMName $RequestedVMName -ErrorAction Stop)
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
    RestoreSourceCheckpoint = $restoreSourceCheckpoint
    StartVM = $startVM
    WaitForVM = $waitForVM
    OpenSession = $openSession
    PrepareGuest = $prepareGuest
    CopySdk = $copySdk
    CopyPackages = $copyPackages
    InspectGuestPayload = $inspectGuestPayload
    ShutdownGuest = $shutdownGuest
    CloseSession = $closeSession
    WaitForPowerOff = $waitForPowerOff
    CreateTargetCheckpoint = $createTargetCheckpoint
    ReadCheckpoints = $readCheckpoints
    RemoveTargetCheckpoint = $removeTargetCheckpoint
    StopVM = $stopVM
}

Write-Output 'Provisioning the PhotoSift Golden VM toolchain checkpoint.'
Write-Output "VM: $($contract.vmName)"
Write-Output "Source checkpoint: $($contract.sourceCheckpointName)"
Write-Output "Target checkpoint: $($contract.targetCheckpointName)"
Write-Output "SDK files: $($payload.sdkInventory.fileCount); bytes: $($payload.sdkInventory.totalBytes)"
Write-Output "Package files: $($payload.packagesInventory.fileCount); bytes: $($payload.packagesInventory.totalBytes)"
Write-Output 'The original checkpoint will be preserved. Large payload copies can take several minutes.'

$result = Invoke-PhotoSiftGoldenVmProvisioning `
    -VMName $VMName `
    -SourceCheckpointName $SourceCheckpointName `
    -TargetCheckpointName $TargetCheckpointName `
    -Credential $resolvedCredential `
    -SdkPath $payload.sdkPath `
    -PackagesPath $payload.packagesPath `
    -HostSdkInventory $payload.sdkInventory `
    -HostPackagesInventory $payload.packagesInventory `
    -Operations $operations

Write-Output "Golden VM provisioning passed: $($result.targetCheckpointName)"
Write-Output "The VM is Off and '$($result.sourceCheckpointName)' remains available."
