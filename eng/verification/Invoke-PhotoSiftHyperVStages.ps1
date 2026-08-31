[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$VMName = 'PhotoSift-Verification',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$CheckpointName = 'Toolchain-Evaluation-Base',

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes = 30,

    [Parameter()]
    [string]$TranscriptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$controllerPath = Join-Path $PSScriptRoot 'Invoke-PhotoSiftHyperV.ps1'
if ([string]::IsNullOrWhiteSpace($TranscriptPath)) {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $TranscriptPath = Join-Path `
        $repositoryRoot `
        '.superpowers\sdd\2026-08-30-e1-s2-scope-preflight\hyperv-gates.log'
}

$credential = Get-Credential `
    -UserName 'PhotoSiftRunner' `
    -Message 'Enter the password for the PhotoSift Golden VM verification account.'
if ($null -eq $credential) {
    throw 'Golden VM credential prompt was cancelled.'
}

Start-Transcript -LiteralPath $TranscriptPath -Force | Out-Null
try {
    foreach ($stage in 'Architecture', 'Core', 'Full') {
        & $controllerPath `
            -Stage $stage `
            -VMName $VMName `
            -CheckpointName $CheckpointName `
            -Credential $credential `
            -TimeoutMinutes $TimeoutMinutes
    }
}
finally {
    Stop-Transcript | Out-Null
}
