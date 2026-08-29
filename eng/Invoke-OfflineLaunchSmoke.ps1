[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'Release',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$RuntimeIdentifier = 'win-x64',

    [Parameter()]
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$modulePath = Join-Path $PSScriptRoot 'sandbox\PhotoSiftSandbox.psm1'
Import-Module $modulePath -Force

$activeNetworkAdapters = @(
    Get-NetAdapter -ErrorAction Stop |
        Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Loopback' })
if ($activeNetworkAdapters.Count -ne 0) {
    $adapterNames = $activeNetworkAdapters.Name -join ', '
    throw "Offline launch requires zero active network adapters; found: $adapterNames"
}

$executablePath = Join-Path $repositoryRoot (
    'src\PhotoSift.App\bin\{0}\net10.0-windows\{1}\PhotoSift.App.exe' -f `
        $Configuration,
        $RuntimeIdentifier)
if (-not [System.IO.File]::Exists($executablePath)) {
    throw "PhotoSift executable does not exist: $executablePath"
}

$result = Invoke-PhotoSiftProcess `
    -FilePath $executablePath `
    -Arguments @('--smoke-test') `
    -WorkingDirectory (Split-Path -Parent $executablePath) `
    -TimeoutSeconds $TimeoutSeconds

Assert-PhotoSiftSmokeResult `
    -ExitCode $result.ExitCode `
    -StandardOutput $result.StandardOutput `
    -TimedOut $result.TimedOut

Write-Output $result.StandardOutput.TrimEnd()
if (-not [string]::IsNullOrWhiteSpace($result.StandardError)) {
    Write-Output $result.StandardError.TrimEnd()
}
Write-Output 'NetworkAdaptersUp=0'
Write-Output "SmokeExitCode=$($result.ExitCode)"
