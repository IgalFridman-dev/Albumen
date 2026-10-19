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

$controllerPath = Join-Path $PSScriptRoot 'Invoke-AlbumenHyperV.ps1'
if ([string]::IsNullOrWhiteSpace($TranscriptPath)) {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $TranscriptPath = Join-Path `
        $repositoryRoot `
        '.superpowers\sdd\2026-08-30-e1-s2-scope-preflight\hyperv-gates.log'
}

$credential = Get-Credential `
    -UserName 'PHOTOSIFTVM\PhotoSiftRunner' `
    -Message 'Capture the password for PHOTOSIFTVM\PhotoSiftRunner. Authentication occurs after each checkpoint restore.'
if ($null -eq $credential) {
    throw 'Golden VM credential prompt was cancelled.'
}
Write-Output 'Credential captured in memory; the Golden VM has not been authenticated yet.'

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
