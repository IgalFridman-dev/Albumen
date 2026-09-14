[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InfrastructureExecutable,
    [Parameter(Mandatory)] [string] $ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$harness = Join-Path $PSScriptRoot '..\..\eng\verification\Invoke-E1S6CrashHarness.ps1'
$outcome = & $harness -InfrastructureExecutable $InfrastructureExecutable -ResultsDirectory $ResultsDirectory
if ($outcome.CrashPoints -ne 6) { throw "Expected six crash points; observed $($outcome.CrashPoints)." }
if ($outcome.VerifiedRestarts -ne 6) { throw "Expected six verified restarts; observed $($outcome.VerifiedRestarts)." }
$verified = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.verified')
if ($verified.Count -ne 6) { throw "Expected six durable restart receipts; observed $($verified.Count)." }
[pscustomobject]@{ CrashPoints = 6; VerifiedRestarts = 6; ToolingTest = $PSCommandPath }
