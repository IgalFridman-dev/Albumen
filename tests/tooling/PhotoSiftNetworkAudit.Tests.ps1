$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$smokePath = Join-Path $repositoryRoot 'eng\Invoke-OfflineLaunchSmoke.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($smokePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'The smoke script must parse before testing its observer.' }
$reader = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-NetworkAuditRecords'
}, $true)
if ($null -eq $reader) { throw 'Network audit reader was not found.' }
# Load only the reader; never execute the smoke script or query the host Security log.
. ([scriptblock]::Create($reader.Extent.Text))
$cmdletType = (Get-Command Get-WinEvent).ImplementingType
$startTimeMethod = $cmdletType.GetMethod('HandleStartTimeHashValue', [Reflection.BindingFlags]'Instance,NonPublic')
$cmdlet = [Activator]::CreateInstance($cmdletType)
if ($null -eq $startTimeMethod) { throw 'Review this regression test for the installed Get-WinEvent implementation.' }

function Get-WinEvent {
    [CmdletBinding()]
    param([hashtable]$FilterHashtable)
    # Exercise the real cmdlet's time conversion without opening any event log.
    $script:query = $startTimeMethod.Invoke($cmdlet, [object[]]@($FilterHashtable.StartTime, $FilterHashtable))
    return @()
}

$passed = 0
foreach ($instantText in @('2026-01-15T20:06:24Z', '2026-09-27T20:06:24Z')) {
    $instant = [DateTimeOffset]::Parse($instantText, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
    foreach ($inputTime in @($instant, $instant.ToLocalTime())) {
        $script:query = $null
        $records = @(Get-NetworkAuditRecords -StartedUtc $inputTime -ProcessId 1234 -ExecutableName 'powershell.exe')
        $expectedTime = $instantText.Replace('Z', '.000Z')
        $expectedQuery = "(System/TimeCreated[@SystemTime&gt;='$expectedTime'])"
        if ($script:query -cne $expectedQuery) {
            throw "Network observation shifted the requested instant: expected $expectedQuery; got $script:query ($($inputTime.Kind), $([TimeZoneInfo]::Local.Id))"
        }
        if ($records.Count -ne 0) { throw 'An empty event query must return no process records.' }
        $passed++
    }
}
Write-Output "PASS: $passed network-audit time-window cases; no host event-log access or product execution."
