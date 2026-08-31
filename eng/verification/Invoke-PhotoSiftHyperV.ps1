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
    [ValidateNotNullOrEmpty()]
    [string]$CredentialPath = (Join-Path `
        ([System.Environment]::GetFolderPath('LocalApplicationData')) `
        'PhotoSift\verification\golden-vm.credential.xml'),

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $PSScriptRoot 'PhotoSiftVerification.psm1'
Import-Module $modulePath -Force

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isElevated = $principal.IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    throw 'Run Invoke-PhotoSiftHyperV.ps1 from an elevated PowerShell process.'
}

Assert-PhotoSiftGoldenVmVerificationCheckpoint `
    -CheckpointName $CheckpointName | Out-Null

$resolvedCredential = $Credential
if ($null -eq $resolvedCredential) {
    $resolvedCredentialPath = [System.IO.Path]::GetFullPath($CredentialPath)
    if (-not [System.IO.File]::Exists($resolvedCredentialPath)) {
        throw "Golden VM credential file does not exist: $resolvedCredentialPath"
    }
    $resolvedCredential = Import-Clixml -LiteralPath $resolvedCredentialPath
}

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

$copyResult = Invoke-PhotoSiftProcess `
    -FilePath (Join-Path $env:SystemRoot 'System32\robocopy.exe') `
    -Arguments @(
        $repositoryRoot,
        $hostInputPath,
        '/E',
        '/XD', '.git', 'bin', 'obj',
        '/XF', '.git',
        '/R:1',
        '/W:1',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS') `
    -WorkingDirectory $repositoryRoot
if ($copyResult.ExitCode -notin 0..7) {
    throw "Could not prepare the Golden VM source snapshot; robocopy exited $($copyResult.ExitCode)."
}

$guestRunRoot = "C:\PhotoSift\runs\$runId"
$guestSdkRoot = 'C:\PhotoSift\golden\sdk'
$guestPackagesRoot = 'C:\PhotoSift\golden\packages'
$deadline = [System.DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)

$restoreCheckpoint = {
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
    param($Session, $RequestedGuestRunRoot)
    Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
        param($RunRoot)
        if ([System.IO.Directory]::Exists($RunRoot)) {
            Remove-Item -LiteralPath $RunRoot -Recurse -Force
        }
        [System.IO.Directory]::CreateDirectory((Join-Path $RunRoot 'input')) | Out-Null
        [System.IO.Directory]::CreateDirectory((Join-Path $RunRoot 'output')) | Out-Null
    } -ArgumentList $RequestedGuestRunRoot
}.GetNewClosure()
$copySource = {
    param($Session, $RequestedHostInputPath, $GuestInputPath)
    foreach ($item in @(Get-ChildItem -LiteralPath $RequestedHostInputPath -Force)) {
        Copy-Item `
            -LiteralPath $item.FullName `
            -Destination $GuestInputPath `
            -Recurse `
            -Force `
            -ToSession $Session `
            -ErrorAction Stop
    }
}.GetNewClosure()
$invokeGuest = {
    param($Session, $RequestedStage, $RequestedGuestRunRoot, $SdkRoot, $PackagesRoot)
    $guestScript = Join-Path `
        $RequestedGuestRunRoot `
        'input\eng\verification\Run-PhotoSiftVerification.ps1'
    $guestInvocation = Get-PhotoSiftGuestRunnerInvocation `
        -ScriptPath $guestScript `
        -Stage $RequestedStage `
        -RunRoot $RequestedGuestRunRoot `
        -SdkRoot $SdkRoot `
        -PackagesRoot $PackagesRoot
    Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
        param($ScriptPath, $StageName, $RunRoot, $GuestSdkRoot, $GuestPackagesRoot)
        $windowsPowerShell = Join-Path `
            $env:SystemRoot `
            'System32\WindowsPowerShell\v1.0\powershell.exe'
        $runnerArguments = @(
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy', 'Bypass',
            '-File', $ScriptPath,
            '-Stage', $StageName,
            '-RunRoot', $RunRoot,
            '-SdkRoot', $GuestSdkRoot,
            '-PackagesRoot', $GuestPackagesRoot)
        & $windowsPowerShell @runnerArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Golden VM guest runner exited $LASTEXITCODE."
        }
    } -ArgumentList `
        $guestInvocation.scriptPath,
        $guestInvocation.stage,
        $guestInvocation.runRoot,
        $guestInvocation.sdkRoot,
        $guestInvocation.packagesRoot
}.GetNewClosure()
$copyEvidence = {
    param($Session, $GuestOutputPath, $RequestedHostOutputPath)
    $remoteItems = @(Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
            param($OutputPath)
            Get-ChildItem -LiteralPath $OutputPath -Force
        } -ArgumentList $GuestOutputPath)
    foreach ($guestItemPath in @(Get-PhotoSiftGuestEvidencePaths -Items $remoteItems)) {
        Copy-Item `
            -FromSession $Session `
            -LiteralPath $guestItemPath `
            -Destination $RequestedHostOutputPath `
            -Recurse `
            -Force `
            -ErrorAction Stop
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

Invoke-PhotoSiftGoldenVmRun `
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
    -Operations $operations | Out-Null

$summary = Assert-PhotoSiftVerificationResult `
    -SummaryPath (Join-Path $hostOutputPath 'summary.json') `
    -ExpectedStage $Stage

$controllerEvidence = [ordered]@{
    vmName = $contract.vmName
    checkpointName = $contract.checkpointName
    initialVmState = $contract.vmState
    networkAdapterCount = $contract.networkAdapterCount
    stage = $Stage
    runId = $runId
    completedAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
}
$controllerEvidence | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $hostOutputPath 'controller.json') -Encoding utf8

Write-Output "Golden VM verification passed for stage $Stage."
Write-Output "Commands: $(@($summary.commands).Count)"
Write-Output "Output: $runPath"
