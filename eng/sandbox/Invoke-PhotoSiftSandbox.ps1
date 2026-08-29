[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet('Architecture', 'Full')]
    [string]$Stage = 'Full',

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$TimeoutMinutes = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $PSScriptRoot 'PhotoSiftSandbox.psm1'
Import-Module $modulePath -Force

$sandboxExecutable = Get-Command WindowsSandbox.exe -ErrorAction Stop
$sdkPath = Join-Path ([System.IO.Path]::GetTempPath()) 'photosift-dotnet-sdk-10.0.400'
$packagesPath = Join-Path ([System.IO.Path]::GetTempPath()) 'photosift-nuget-packages'

foreach ($requiredPath in $repositoryRoot, $sdkPath, $packagesPath) {
    if (-not [System.IO.Directory]::Exists($requiredPath)) {
        throw "Required Sandbox input directory does not exist: $requiredPath"
    }
}

$runsRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PhotoSift-Sandbox-Runs'
[System.IO.Directory]::CreateDirectory($runsRoot) | Out-Null

$runName = '{0}-{1}-{2}' -f (
    Get-Date -Format 'yyyyMMdd-HHmmss'),
    $Stage.ToLowerInvariant(),
    [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$runPath = Join-Path $runsRoot $runName
[System.IO.Directory]::CreateDirectory($runPath) | Out-Null

$configurationPath = Join-Path $runPath 'PhotoSift.wsb'
$summaryPath = Join-Path $runPath 'summary.json'
$configuration = New-PhotoSiftSandboxConfiguration `
    -SourcePath $repositoryRoot `
    -SdkPath $sdkPath `
    -PackagesPath $packagesPath `
    -OutputPath $runPath `
    -Stage $Stage
$configuration | Set-Content -LiteralPath $configurationPath -Encoding utf8

Write-Output "Starting Windows Sandbox with networking disabled."
Write-Output "Run output: $runPath"

Start-Process -FilePath $sandboxExecutable.Source -ArgumentList $configurationPath | Out-Null

$deadline = [System.DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)
while (-not [System.IO.File]::Exists($summaryPath)) {
    if ([System.DateTimeOffset]::Now -ge $deadline) {
        throw "Windows Sandbox did not produce summary.json within $TimeoutMinutes minutes. Output: $runPath"
    }

    Start-Sleep -Seconds 2
}

$summary = Assert-PhotoSiftSandboxResult `
    -SummaryPath $summaryPath `
    -ExpectedStage $Stage

Write-Output "Windows Sandbox verification passed for stage $Stage."
Write-Output "Commands: $(@($summary.commands).Count)"
Write-Output "Output: $runPath"
