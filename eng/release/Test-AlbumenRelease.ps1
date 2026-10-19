[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    Import-Module (Join-Path $PSScriptRoot 'AlbumenRelease.psm1') -Force
    Test-AlbumenRelease -PackagePath $PackagePath | Out-Null
    Write-Output 'Albumen release verification passed.'
    exit 0
}
catch {
    [System.Console]::Error.WriteLine("Albumen release verification failed: $($_.Exception.Message)")
    exit 1
}
