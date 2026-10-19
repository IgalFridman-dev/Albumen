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
$modulePath = Join-Path $PSScriptRoot 'verification\AlbumenVerification.psm1'
Import-Module $modulePath -Force

function Get-NetworkAuditRecords {
    param(
        [Parameter(Mandatory)]
        [System.DateTime]$StartedUtc,

        [Parameter(Mandatory)]
        [int]$ProcessId,

        [Parameter(Mandatory)]
        [string]$ExecutableName
    )

    try {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = 'Security'
            Id = 5156, 5157, 5158
            # Get-WinEvent stringifies DateTime and loses its Kind; retain the UTC offset.
            StartTime = $StartedUtc.ToUniversalTime().ToString('o')
        } -ErrorAction Stop)
    }
    catch [System.Exception] {
        if ($_.FullyQualifiedErrorId -match 'NoMatchingEventsFound') { return @() }
        throw
    }

    return @($events | ForEach-Object {
        ConvertFrom-AlbumenNetworkAuditXml -Xml $_.ToXml()
    } | Where-Object {
        $_.ProcessId -eq $ProcessId -and
        [System.String]::Equals($_.ExecutableName, $ExecutableName, [System.StringComparison]::OrdinalIgnoreCase)
    })
}

function Wait-NetworkAuditRecords {
    param(
        [Parameter(Mandatory)]
        [System.DateTime]$StartedUtc,

        [Parameter(Mandatory)]
        [int]$ProcessId,

        [Parameter(Mandatory)]
        [string]$ExecutableName,

        [Parameter()]
        [int]$MinimumCount = 0
    )

    return @(Wait-AlbumenNetworkAuditRecords `
        -ReadRecords {
            Get-NetworkAuditRecords `
                -StartedUtc $StartedUtc `
                -ProcessId $ProcessId `
                -ExecutableName $ExecutableName
        } `
        -MinimumCount $MinimumCount)
}

function Invoke-NetworkObservationCanary {
    param(
        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $startedUtc = [System.DateTime]::UtcNow.AddSeconds(-1)
    $canaryScript = @'
$client = New-Object System.Net.Sockets.UdpClient
try {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes('Albumen network observer canary')
    [void]$client.Send($bytes, $bytes.Length, '127.0.0.1', 9)
}
finally { $client.Dispose() }
'@
    $canary = Invoke-AlbumenProcess `
        -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Arguments @('-NoProfile', '-NonInteractive', '-Command', $canaryScript) `
        -WorkingDirectory $WorkingDirectory `
        -TimeoutSeconds 10
    if ($canary.TimedOut -or $canary.ExitCode -ne 0) {
        throw "Network observation canary failed with exit code $($canary.ExitCode)."
    }
    $events = @(Wait-NetworkAuditRecords `
        -StartedUtc $startedUtc `
        -ProcessId $canary.ProcessId `
        -ExecutableName 'powershell.exe' `
        -MinimumCount 1)
    if ($events.Count -lt 1) {
        throw 'Network observer did not detect the known loopback connection attempt.'
    }
    return $events
}

$activeNetworkAdapters = @(
    Get-NetAdapter -ErrorAction Stop |
        Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Loopback' })
if ($activeNetworkAdapters.Count -ne 0) {
    $adapterNames = $activeNetworkAdapters.Name -join ', '
    throw "Offline launch requires zero active network adapters; found: $adapterNames"
}

$executablePath = Join-Path $repositoryRoot (
    'src\Albumen.App\bin\{0}\net10.0-windows\{1}\Albumen.App.exe' -f `
        $Configuration,
        $RuntimeIdentifier)
if (-not [System.IO.File]::Exists($executablePath)) {
    throw "Albumen executable does not exist: $executablePath"
}

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Offline runtime network observation requires an elevated verification process.'
}

$profileRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'Albumen-FirstLaunch-' + [System.Guid]::NewGuid().ToString('N'))
$auditPolicyBackupPath = Join-Path $profileRoot 'audit-policy.csv'
$previousLocalAppData = $env:LOCALAPPDATA
$previousAppData = $env:APPDATA
$auditPolicyBackedUp = $false
$auditRestoreFailure = $null
$result = $null
$canaryEvents = @()
$fenceEvents = @()
$albumenEvents = @()

try {
    [System.IO.Directory]::CreateDirectory($profileRoot) | Out-Null
    $env:LOCALAPPDATA = Join-Path $profileRoot 'LocalAppData'
    $env:APPDATA = Join-Path $profileRoot 'RoamingAppData'
    [System.IO.Directory]::CreateDirectory($env:LOCALAPPDATA) | Out-Null
    [System.IO.Directory]::CreateDirectory($env:APPDATA) | Out-Null

    & "$env:SystemRoot\System32\auditpol.exe" /backup "/file:$auditPolicyBackupPath" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "auditpol.exe /backup failed with exit code $LASTEXITCODE." }
    $auditPolicyBackedUp = $true
    & "$env:SystemRoot\System32\auditpol.exe" /set `
        '/subcategory:{0CCE9226-69AE-11D9-BED3-505054503030}' `
        '/success:enable' `
        '/failure:enable' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "auditpol.exe /set failed with exit code $LASTEXITCODE." }

    $canaryEvents = @(Invoke-NetworkObservationCanary -WorkingDirectory $profileRoot)

    $albumenStartedUtc = [System.DateTime]::UtcNow.AddSeconds(-1)
    $result = Invoke-AlbumenProcess `
        -FilePath $executablePath `
        -Arguments @('--smoke-test') `
        -WorkingDirectory (Split-Path -Parent $executablePath) `
        -TimeoutSeconds $TimeoutSeconds
    $fenceEvents = @(Invoke-NetworkObservationCanary -WorkingDirectory $profileRoot)
    $albumenEvents = @(Get-NetworkAuditRecords `
        -StartedUtc $albumenStartedUtc `
        -ProcessId $result.ProcessId `
        -ExecutableName 'Albumen.App.exe')
    if ($albumenEvents.Count -ne 0) {
        throw "Albumen produced $($albumenEvents.Count) observed runtime network attempt event(s)."
    }

    Assert-AlbumenSmokeResult `
        -ExitCode $result.ExitCode `
        -StandardOutput $result.StandardOutput `
        -TimedOut $result.TimedOut
}
finally {
    if ($auditPolicyBackedUp) {
        & "$env:SystemRoot\System32\auditpol.exe" /restore "/file:$auditPolicyBackupPath" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $auditRestoreFailure = [System.InvalidOperationException]::new(
                "auditpol.exe /restore failed with exit code $LASTEXITCODE.")
        }
    }
    $env:LOCALAPPDATA = $previousLocalAppData
    $env:APPDATA = $previousAppData
    $resolvedProfileRoot = [System.IO.Path]::GetFullPath($profileRoot)
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolvedProfileRoot.StartsWith($resolvedTemporaryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove first-launch profile outside the temporary root: $resolvedProfileRoot"
    }
    if ([System.IO.Directory]::Exists($resolvedProfileRoot)) {
        Remove-Item -Recurse -Force -LiteralPath $resolvedProfileRoot
    }
    if ($null -ne $auditRestoreFailure) {
        throw $auditRestoreFailure
    }
}

Write-Output $result.StandardOutput.TrimEnd()
if (-not [string]::IsNullOrWhiteSpace($result.StandardError)) {
    Write-Output $result.StandardError.TrimEnd()
}
Write-Output 'NetworkAdaptersUp=0'
Write-Output "NetworkObserverCanaryEvents=$($canaryEvents.Count)"
Write-Output "NetworkObserverFenceEvents=$($fenceEvents.Count)"
Write-Output 'AlbumenNetworkAttemptEvents=0'
Write-Output 'FreshProfile=true'
Write-Output "SmokeExitCode=$($result.ExitCode)"
