[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    Import-Module (Join-Path $PSScriptRoot 'PhotoSiftRelease.psm1') -Force
    Test-PhotoSiftRelease -PackagePath $PackagePath | Out-Null
    Write-Output 'PhotoSift release verification passed.'
    exit 0
}
catch {
    [System.Console]::Error.WriteLine("PhotoSift release verification failed: $($_.Exception.Message)")
    exit 1
}
