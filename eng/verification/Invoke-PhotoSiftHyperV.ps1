[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet('Architecture', 'Core', 'Full')]
    [string]$Stage = 'Full',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$VMName = 'PhotoSift-Verification',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$CheckpointName = 'Toolchain-Evaluation-Base',

    [Parameter()]
    [System.Management.Automation.PSCredential]$Credential,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $PSScriptRoot 'PhotoSiftVerification.psm1'
Import-Module $modulePath -Force
$deadline = [System.DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isElevated = $principal.IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    throw 'Run Invoke-PhotoSiftHyperV.ps1 from an elevated PowerShell process.'
}

Assert-PhotoSiftGoldenVmVerificationCheckpoint `
    -CheckpointName $CheckpointName | Out-Null

$resolvedCredential = Get-PhotoSiftGoldenVmCredential -Credential $Credential

$vm = Get-VM -Name $VMName -ErrorAction Stop
$checkpoints = @(Get-VMSnapshot -VMName $VMName -ErrorAction Stop)
$networkAdapters = @(Get-VMNetworkAdapter -VMName $VMName -ErrorAction Stop)
$contract = Assert-PhotoSiftGoldenVmContract `
    -ExpectedVMName $VMName `
    -ExpectedCheckpointName $CheckpointName `
    -VM $vm `
    -Checkpoints $checkpoints `
    -NetworkAdapters $networkAdapters `
    -Credential $resolvedCredential `
    -IsElevated $isElevated

$runsRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-HyperV-Runs'
[System.IO.Directory]::CreateDirectory($runsRoot) | Out-Null
$runId = '{0}-{1}-{2}' -f (
    Get-Date -Format 'yyyyMMdd-HHmmss'),
    $Stage.ToLowerInvariant(),
    [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$runPath = Join-Path $runsRoot $runId
$hostInputPath = Join-Path $runPath 'input'
$hostOutputPath = Join-Path $runPath 'output'
[System.IO.Directory]::CreateDirectory($hostInputPath) | Out-Null
[System.IO.Directory]::CreateDirectory($hostOutputPath) | Out-Null

try {
    New-PhotoSiftSourceSnapshot `
        -RepositoryRoot $repositoryRoot `
        -HostInputPath $hostInputPath `
        -Deadline $deadline | Out-Null

$guestRunRoot = "C:\PhotoSift\runs\$runId"
$guestSdkRoot = 'C:\PhotoSift\golden\sdk'
$guestPackagesRoot = 'C:\PhotoSift\golden\packages'

$restoreCheckpoint = {
    param($RequestedVMName, $RequestedCheckpointName, $RequestedDeadline)
    Restore-VMSnapshot `
        -VMName $RequestedVMName `
        -Name $RequestedCheckpointName `
        -Confirm:$false `
        -ErrorAction Stop | Out-Null
}.GetNewClosure()
$inspectVmContract = {
    param($RequestedVMName, $RequestedDeadline)
    $currentVM = Get-VM -Name $RequestedVMName -ErrorAction Stop
    $currentNetworkAdapters = @(
        Get-VMNetworkAdapter -VMName $RequestedVMName -ErrorAction Stop)
    if ([string]$currentVM.Name -cne $RequestedVMName) {
        throw "Hyper-V returned a VM that did not match '$RequestedVMName'."
    }
    if ([string]$currentVM.State -cne 'Off') {
        throw "Golden VM '$RequestedVMName' must be Off after checkpoint restore."
    }
    if ($currentNetworkAdapters.Count -ne 0) {
        throw "Golden VM '$RequestedVMName' must have zero virtual network adapters after checkpoint restore."
    }
    [pscustomobject]@{
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
    param($RequestedVMName, $RequestedCredential, $RequestedDeadline)
    Connect-PhotoSiftPowerShellDirect `
        -VMName $RequestedVMName `
        -CheckpointName $CheckpointName `
        -Credential $RequestedCredential `
        -Deadline $RequestedDeadline | ForEach-Object {
            Write-Information `
                "PowerShell Direct authenticated PHOTOSIFTVM\PhotoSiftRunner for VM '$RequestedVMName' checkpoint '$CheckpointName'." `
                -InformationAction Continue
            $_
        }
}.GetNewClosure()
$prepareGuest = {
    param($Session, $RequestedGuestRunRoot, $RequestedDeadline)
    Invoke-PhotoSiftPowerShellWithDeadline `
        -ScriptBlock {
            param($RemoteSession, $RunRoot)
            Invoke-Command -Session $RemoteSession -ErrorAction Stop -ScriptBlock {
                param($RemoteRunRoot)
                if ([System.IO.Directory]::Exists($RemoteRunRoot)) {
                    Remove-Item -LiteralPath $RemoteRunRoot -Recurse -Force
                }
                [System.IO.Directory]::CreateDirectory((Join-Path $RemoteRunRoot 'input')) | Out-Null
                [System.IO.Directory]::CreateDirectory((Join-Path $RemoteRunRoot 'output')) | Out-Null
            } -ArgumentList $RunRoot
        } `
        -ArgumentList @($Session, $RequestedGuestRunRoot) `
        -Deadline $RequestedDeadline `
        -OperationName 'guest preparation' | Out-Null
}.GetNewClosure()
$copySource = {
    param($Session, $RequestedHostInputPath, $GuestInputPath, $RequestedDeadline)
    foreach ($item in @(Get-ChildItem -LiteralPath $RequestedHostInputPath -Force)) {
        Invoke-PhotoSiftPowerShellWithDeadline `
            -ScriptBlock {
                param($RemoteSession, $SourcePath, $DestinationPath)
                Copy-Item `
                    -LiteralPath $SourcePath `
                    -Destination $DestinationPath `
                    -Recurse `
                    -Force `
                    -ToSession $RemoteSession `
                    -ErrorAction Stop
            } `
            -ArgumentList @($Session, $item.FullName, $GuestInputPath) `
            -Deadline $RequestedDeadline `
            -OperationName "guest source copy '$($item.Name)'" | Out-Null
    }
}.GetNewClosure()
$invokeGuest = {
    param($Session, $RequestedStage, $RequestedGuestRunRoot, $SdkRoot, $PackagesRoot, $RequestedDeadline)
    $guestScript = Join-Path `
        $RequestedGuestRunRoot `
        'input\eng\verification\Run-PhotoSiftVerification.ps1'
    $remainingSeconds = Get-PhotoSiftRemainingSeconds `
        -Deadline $RequestedDeadline `
        -OperationName 'guest verification'
    $guestInvocation = Get-PhotoSiftGuestRunnerInvocation `
        -ScriptPath $guestScript `
        -Stage $RequestedStage `
        -RunRoot $RequestedGuestRunRoot `
        -SdkRoot $SdkRoot `
        -PackagesRoot $PackagesRoot `
        -TimeoutSeconds $remainingSeconds
    Invoke-PhotoSiftPowerShellWithDeadline `
        -ScriptBlock {
            param(
                $RemoteSession,
                $ScriptPath,
                $StageName,
                $RunRoot,
                $GuestSdkRoot,
                $GuestPackagesRoot,
                $GuestTimeoutSeconds)
            Invoke-Command -Session $RemoteSession -ErrorAction Stop -ScriptBlock {
                param(
                    $RemoteScriptPath,
                    $RemoteStageName,
                    $RemoteRunRoot,
                    $RemoteSdkRoot,
                    $RemotePackagesRoot,
                    $RemoteTimeoutSeconds)
                $windowsPowerShell = Join-Path `
                    $env:SystemRoot `
                    'System32\WindowsPowerShell\v1.0\powershell.exe'
                $runnerArguments = @(
                    '-NoProfile',
                    '-NonInteractive',
                    '-ExecutionPolicy', 'Bypass',
                    '-File', $RemoteScriptPath,
                    '-Stage', $RemoteStageName,
                    '-RunRoot', $RemoteRunRoot,
                    '-SdkRoot', $RemoteSdkRoot,
                    '-PackagesRoot', $RemotePackagesRoot,
                    '-TimeoutSeconds', $RemoteTimeoutSeconds)
                & $windowsPowerShell @runnerArguments
                if ($LASTEXITCODE -ne 0) {
                    throw "Golden VM guest runner exited $LASTEXITCODE."
                }
            } -ArgumentList `
                $ScriptPath,
                $StageName,
                $RunRoot,
                $GuestSdkRoot,
                $GuestPackagesRoot,
                $GuestTimeoutSeconds
        } `
        -ArgumentList @(
            $Session,
            $guestInvocation.scriptPath,
            $guestInvocation.stage,
            $guestInvocation.runRoot,
            $guestInvocation.sdkRoot,
            $guestInvocation.packagesRoot,
            $guestInvocation.timeoutSeconds) `
        -Deadline $RequestedDeadline `
        -OperationName 'guest verification' | Out-Null
}.GetNewClosure()
$copyEvidence = {
    param($Session, $GuestOutputPath, $RequestedHostOutputPath, $RequestedDeadline)
    $remoteItems = @(Invoke-PhotoSiftPowerShellWithDeadline `
        -ScriptBlock {
            param($RemoteSession, $OutputPath)
            Invoke-Command -Session $RemoteSession -ErrorAction Stop -ScriptBlock {
                param($RemoteOutputPath)
                Get-ChildItem -LiteralPath $RemoteOutputPath -Force
            } -ArgumentList $OutputPath
        } `
        -ArgumentList @($Session, $GuestOutputPath) `
        -Deadline $RequestedDeadline `
        -OperationName 'guest evidence enumeration')
    foreach ($guestItemPath in @(Get-PhotoSiftGuestEvidencePaths -Items $remoteItems)) {
        Invoke-PhotoSiftPowerShellWithDeadline `
            -ScriptBlock {
                param($RemoteSession, $SourcePath, $DestinationPath)
                Copy-Item `
                    -FromSession $RemoteSession `
                    -LiteralPath $SourcePath `
                    -Destination $DestinationPath `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
            } `
            -ArgumentList @($Session, $guestItemPath, $RequestedHostOutputPath) `
            -Deadline $RequestedDeadline `
            -OperationName "guest evidence copy '$guestItemPath'" | Out-Null
    }
}.GetNewClosure()
$closeSession = {
    param($Session)
    Remove-PSSession -Session $Session -ErrorAction Stop
}.GetNewClosure()
$stopVM = {
    param($RequestedVMName)
    $currentVM = Get-VM -Name $RequestedVMName -ErrorAction Stop
    if ([string]$currentVM.State -cne 'Off') {
        Stop-VM -Name $RequestedVMName -TurnOff -Force -ErrorAction Stop | Out-Null
    }
}.GetNewClosure()

$operations = @{
    RestoreCheckpoint = $restoreCheckpoint
    InspectVmContract = $inspectVmContract
    StartVM = $startVM
    WaitForVM = $waitForVM
    OpenSession = $openSession
    PrepareGuest = $prepareGuest
    CopySource = $copySource
    InvokeGuest = $invokeGuest
    CopyEvidence = $copyEvidence
    CloseSession = $closeSession
    StopVM = $stopVM
}

Write-Output "Starting Golden VM verification for stage $Stage."
Write-Output "VM: $($contract.vmName); checkpoint: $($contract.checkpointName); virtual NICs: $($contract.networkAdapterCount)"
Write-Output "Run output: $runPath"

$runResult = Invoke-PhotoSiftGoldenVmRun `
    -VMName $VMName `
    -CheckpointName $CheckpointName `
    -Credential $resolvedCredential `
    -Stage $Stage `
    -RunId $runId `
    -HostInputPath $hostInputPath `
    -HostOutputPath $hostOutputPath `
    -GuestRunRoot $guestRunRoot `
    -SdkRoot $guestSdkRoot `
    -PackagesRoot $guestPackagesRoot `
    -Deadline $deadline `
    -Operations $operations

$summary = Assert-PhotoSiftVerificationResult `
    -SummaryPath (Join-Path $hostOutputPath 'summary.json') `
    -ExpectedStage $Stage

$controllerEvidence = [ordered]@{
    vmName = $contract.vmName
    checkpointName = $contract.checkpointName
    initialVmState = $contract.vmState
    networkAdapterCount = $contract.networkAdapterCount
    postRestoreVmState = $runResult.postRestoreVmState
    postRestoreNetworkAdapterCount = $runResult.postRestoreNetworkAdapterCount
    postCleanupVmState = $runResult.postCleanupVmState
    postCleanupNetworkAdapterCount = $runResult.postCleanupNetworkAdapterCount
    stage = $Stage
    runId = $runId
    completedAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
}
$controllerEvidence | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $hostOutputPath 'controller.json') -Encoding utf8

Write-Output "Golden VM verification passed for stage $Stage."
Write-Output "Commands: $(@($summary.commands).Count)"
Write-Output "Output: $runPath"
}
finally {
    Remove-PhotoSiftHostInputSnapshot `
        -HostInputPath $hostInputPath `
        -RunsRoot $runsRoot
}
