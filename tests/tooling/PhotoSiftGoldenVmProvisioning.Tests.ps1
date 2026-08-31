$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verificationModulePath = Join-Path $repositoryRoot 'eng\verification\PhotoSiftVerification.psm1'
Import-Module $verificationModulePath -Force
$modulePath = Join-Path $repositoryRoot 'eng\verification\PhotoSiftGoldenVmProvisioning.psm1'
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

Assert-Equal `
    $true `
    ([bool](Get-Command Connect-PhotoSiftPowerShellDirect -ErrorAction SilentlyContinue)) `
    'importing the provisioning module must retain the caller-visible PowerShell Direct connector'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-GoldenVm-Provisioning-Tests'
if ([System.IO.Directory]::Exists($testRoot)) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    $sdkPath = Join-Path $testRoot 'sdk'
    $packagesPath = Join-Path $testRoot 'packages'
    [System.IO.Directory]::CreateDirectory((Join-Path $sdkPath 'sdk\10.0.400')) | Out-Null
    [System.IO.Directory]::CreateDirectory(
        (Join-Path $sdkPath 'shared\Microsoft.WindowsDesktop.App\10.0.11')) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $packagesPath 'example\1.0.0')) | Out-Null
    'fixture' | Set-Content -LiteralPath (Join-Path $sdkPath 'dotnet.exe') -Encoding ascii
    'sdk-file' | Set-Content -LiteralPath (Join-Path $sdkPath 'sdk\10.0.400\sdk.txt') -Encoding ascii
    'runtime-file' | Set-Content -LiteralPath (
        Join-Path $sdkPath 'shared\Microsoft.WindowsDesktop.App\10.0.11\runtime.txt') -Encoding ascii
    'package-file' | Set-Content -LiteralPath (
        Join-Path $packagesPath 'example\1.0.0\example.nupkg') -Encoding ascii

    $payload = Assert-PhotoSiftToolchainPayload `
        -SdkPath $sdkPath `
        -PackagesPath $packagesPath
    Assert-Equal '10.0.400' $payload.sdkVersion 'the host payload must pin SDK 10.0.400'
    Assert-Equal '10.0.11' $payload.desktopRuntimeVersion 'the host payload must pin Desktop Runtime 10.0.11'
    Assert-Equal 3 $payload.sdkInventory.fileCount 'the SDK inventory must count every fixture file'
    Assert-Equal 1 $payload.packagesInventory.fileCount 'the package cache must be nonempty'

    $emptyPackagesPath = Join-Path $testRoot 'empty-packages'
    [System.IO.Directory]::CreateDirectory($emptyPackagesPath) | Out-Null
    Assert-Throws {
        Assert-PhotoSiftToolchainPayload `
            -SdkPath $sdkPath `
            -PackagesPath $emptyPackagesPath
    } 'an empty package cache must be rejected'

    $wrongSdkPath = Join-Path $testRoot 'wrong-sdk'
    [System.IO.Directory]::CreateDirectory((Join-Path $wrongSdkPath 'sdk\9.0.100')) | Out-Null
    [System.IO.Directory]::CreateDirectory(
        (Join-Path $wrongSdkPath 'shared\Microsoft.WindowsDesktop.App\9.0.0')) | Out-Null
    'fixture' | Set-Content -LiteralPath (Join-Path $wrongSdkPath 'dotnet.exe') -Encoding ascii
    Assert-Throws {
        Assert-PhotoSiftToolchainPayload `
            -SdkPath $wrongSdkPath `
            -PackagesPath $packagesPath
    } 'a payload without the exact SDK and Desktop Runtime must be rejected'

    $credential = New-Object System.Management.Automation.PSCredential(
        'PhotoSiftRunner',
        (ConvertTo-SecureString 'test-only-password' -AsPlainText -Force))
    $vm = [pscustomobject]@{ Name = 'PhotoSift-Verification'; State = 'Off' }
    $sourceCheckpoint = [pscustomobject]@{ Name = 'Clean-Evaluation-Base' }
    $targetCheckpoint = [pscustomobject]@{ Name = 'Toolchain-Evaluation-Base' }

    Assert-PhotoSiftGoldenVmProvisioningContract `
        -ExpectedVMName 'PhotoSift-Verification' `
        -SourceCheckpointName 'Clean-Evaluation-Base' `
        -TargetCheckpointName 'Toolchain-Evaluation-Base' `
        -VM $vm `
        -Checkpoints @($sourceCheckpoint) `
        -NetworkAdapters @() `
        -Credential $credential `
        -IsElevated $true | Out-Null
    $script:passed++

    Assert-Throws {
        Assert-PhotoSiftGoldenVmProvisioningContract `
            -ExpectedVMName 'PhotoSift-Verification' `
            -SourceCheckpointName 'Clean-Evaluation-Base' `
            -TargetCheckpointName 'Toolchain-Evaluation-Base' `
            -VM $vm `
            -Checkpoints @($sourceCheckpoint, $targetCheckpoint) `
            -NetworkAdapters @() `
            -Credential $credential `
            -IsElevated $true
    } 'an existing target checkpoint must be rejected before mutation'

    $hostSdkInventory = [pscustomobject]@{ fileCount = 3; totalBytes = [long]30 }
    $hostPackagesInventory = [pscustomobject]@{ fileCount = 1; totalBytes = [long]12 }
    $successState = [pscustomobject]@{
        Events = New-Object 'System.Collections.Generic.List[string]'
        VMState = 'Off'
        SessionClosed = $false
        TargetCreated = $false
    }
    $successOperations = @{
        RestoreSourceCheckpoint = { param($VMName, $CheckpointName) $successState.Events.Add('restore-source'); $successState.VMState = 'Off' }.GetNewClosure()
        StartVM = { param($VMName) $successState.Events.Add('start'); $successState.VMState = 'Running' }.GetNewClosure()
        WaitForVM = { param($VMName, $TimeoutSeconds) $successState.Events.Add('wait-ready') }.GetNewClosure()
        OpenSession = { param($VMName, $Credential) $successState.Events.Add('open-session'); [pscustomobject]@{ Name = 'session' } }.GetNewClosure()
        PrepareGuest = { param($Session, $GuestToolchainRoot) $successState.Events.Add('prepare-guest') }.GetNewClosure()
        CopySdk = { param($Session, $SdkPath, $GuestSdkPath) $successState.Events.Add('copy-sdk') }.GetNewClosure()
        CopyPackages = { param($Session, $PackagesPath, $GuestPackagesPath) $successState.Events.Add('copy-packages') }.GetNewClosure()
        InspectGuestPayload = {
            param($Session, $GuestSdkPath, $GuestPackagesPath)
            $successState.Events.Add('inspect-guest')
            [pscustomobject]@{
                sdkVersion = '10.0.400'
                desktopRuntimeVersion = '10.0.11'
                sdkInventory = [pscustomobject]@{ fileCount = 3; totalBytes = [long]30 }
                packagesInventory = [pscustomobject]@{ fileCount = 1; totalBytes = [long]12 }
            }
        }.GetNewClosure()
        ShutdownGuest = { param($Session) $successState.Events.Add('shutdown-guest') }.GetNewClosure()
        CloseSession = { param($Session) $successState.Events.Add('close-session'); $successState.SessionClosed = $true }.GetNewClosure()
        WaitForPowerOff = { param($VMName, $TimeoutSeconds) $successState.Events.Add('wait-off'); $successState.VMState = 'Off' }.GetNewClosure()
        CreateTargetCheckpoint = { param($VMName, $CheckpointName) $successState.Events.Add('create-target'); $successState.TargetCreated = $true }.GetNewClosure()
        ReadCheckpoints = {
            param($VMName)
            $successState.Events.Add('read-checkpoints')
            @($sourceCheckpoint, $targetCheckpoint)
        }.GetNewClosure()
        RemoveTargetCheckpoint = { param($VMName, $CheckpointName) $successState.Events.Add('remove-target'); $successState.TargetCreated = $false }.GetNewClosure()
        StopVM = { param($VMName) $successState.Events.Add('stop'); $successState.VMState = 'Off' }.GetNewClosure()
    }

    $result = Invoke-PhotoSiftGoldenVmProvisioning `
        -VMName 'PhotoSift-Verification' `
        -SourceCheckpointName 'Clean-Evaluation-Base' `
        -TargetCheckpointName 'Toolchain-Evaluation-Base' `
        -Credential $credential `
        -SdkPath $sdkPath `
        -PackagesPath $packagesPath `
        -HostSdkInventory $hostSdkInventory `
        -HostPackagesInventory $hostPackagesInventory `
        -Operations $successOperations
    Assert-Equal 'Toolchain-Evaluation-Base' $result.targetCheckpointName 'success must report the additive checkpoint'
    Assert-Equal $true $successState.SessionClosed 'success must close the PowerShell Direct session'
    Assert-Equal 'Off' $successState.VMState 'checkpoint creation must occur with the VM Off'
    Assert-Equal $true $successState.TargetCreated 'success must preserve the new target checkpoint'
    Assert-Equal `
        'restore-source,start,wait-ready,open-session,prepare-guest,copy-sdk,copy-packages,inspect-guest,shutdown-guest,close-session,wait-off,create-target,read-checkpoints' `
        ($successState.Events -join ',') `
        'the target checkpoint must be created only after verification and clean shutdown'

    $failureState = [pscustomobject]@{
        RestoreCount = 0
        VMState = 'Off'
        SessionClosed = $false
        TargetCreated = $false
    }
    $failureOperations = @{
        RestoreSourceCheckpoint = { param($VMName, $CheckpointName) $failureState.RestoreCount++; $failureState.VMState = 'Off' }.GetNewClosure()
        StartVM = { param($VMName) $failureState.VMState = 'Running' }.GetNewClosure()
        WaitForVM = { param($VMName, $TimeoutSeconds) }.GetNewClosure()
        OpenSession = { param($VMName, $Credential) [pscustomobject]@{ Name = 'session' } }.GetNewClosure()
        PrepareGuest = { param($Session, $GuestToolchainRoot) }.GetNewClosure()
        CopySdk = { param($Session, $SdkPath, $GuestSdkPath) throw 'synthetic transfer failure' }.GetNewClosure()
        CopyPackages = { param($Session, $PackagesPath, $GuestPackagesPath) }.GetNewClosure()
        InspectGuestPayload = { param($Session, $GuestSdkPath, $GuestPackagesPath) }.GetNewClosure()
        ShutdownGuest = { param($Session) }.GetNewClosure()
        CloseSession = { param($Session) $failureState.SessionClosed = $true }.GetNewClosure()
        WaitForPowerOff = { param($VMName, $TimeoutSeconds) }.GetNewClosure()
        CreateTargetCheckpoint = { param($VMName, $CheckpointName) $failureState.TargetCreated = $true }.GetNewClosure()
        ReadCheckpoints = { param($VMName) @($sourceCheckpoint) }.GetNewClosure()
        RemoveTargetCheckpoint = { param($VMName, $CheckpointName) $failureState.TargetCreated = $false }.GetNewClosure()
        StopVM = { param($VMName) $failureState.VMState = 'Off' }.GetNewClosure()
    }
    Assert-Throws {
        Invoke-PhotoSiftGoldenVmProvisioning `
            -VMName 'PhotoSift-Verification' `
            -SourceCheckpointName 'Clean-Evaluation-Base' `
            -TargetCheckpointName 'Toolchain-Evaluation-Base' `
            -Credential $credential `
            -SdkPath $sdkPath `
            -PackagesPath $packagesPath `
            -HostSdkInventory $hostSdkInventory `
            -HostPackagesInventory $hostPackagesInventory `
            -Operations $failureOperations
    } 'a transfer failure must abort provisioning'
    Assert-Equal 2 $failureState.RestoreCount 'failure must restore the source checkpoint before and after mutation'
    Assert-Equal $true $failureState.SessionClosed 'failure must close the PowerShell Direct session'
    Assert-Equal 'Off' $failureState.VMState 'failure must leave the VM Off'
    Assert-Equal $false $failureState.TargetCreated 'failure must not leave a target checkpoint'
}
finally {
    if ([System.IO.Directory]::Exists($testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$provisionerProbe = Invoke-PhotoSiftProcess `
    -FilePath (Get-Command pwsh.exe -ErrorAction Stop).Source `
    -Arguments @(
        '-NoProfile',
        '-File',
        (Join-Path $repositoryRoot 'eng\verification\Initialize-PhotoSiftGoldenVm.ps1')) `
    -WorkingDirectory $repositoryRoot
Assert-Equal 1 $provisionerProbe.ExitCode 'a non-elevated provisioner probe must fail closed'
Assert-Equal `
    $true `
    ($provisionerProbe.StandardError -match 'Run Initialize-PhotoSiftGoldenVm.ps1 from an elevated PowerShell process' -and
        $provisionerProbe.StandardError -notmatch 'Get-Credential|Get-VM|provider') `
    'elevation must be the first unmet provisioning contract'

Write-Output "Passed: $script:passed Failed: 0"
