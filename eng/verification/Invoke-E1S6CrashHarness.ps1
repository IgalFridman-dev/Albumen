[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InfrastructureExecutable,
    [Parameter(Mandatory)] [string] $ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$points = @(
    'before-requested-commit',
    'after-requested-before-executing',
    'after-executing-before-adapter-entry',
    'during-shell-callbacks',
    'after-shell-return-before-terminal-commit',
    'after-terminal-commit-before-ui-publication'
)
$method = 'Albumen.Infrastructure.Tests.ActionExecution.E1S6WindowsRevalidationAcceptanceTests.ProcessHarnessChildUsesRealActionJournalAndRecovery'
$savedCrash = [Environment]::GetEnvironmentVariable('ALBUMEN_E1S6_CRASH_POINT')
$savedMode = [Environment]::GetEnvironmentVariable('ALBUMEN_E1S6_HARNESS_MODE')
$savedState = [Environment]::GetEnvironmentVariable('ALBUMEN_E1S6_MANIFEST_PATH')
try {
    New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
    foreach ($point in $points) {
        $statePath = Join-Path $ResultsDirectory ($point + '.json')
        [Environment]::SetEnvironmentVariable('ALBUMEN_E1S6_MANIFEST_PATH', $statePath)
        [Environment]::SetEnvironmentVariable('ALBUMEN_E1S6_CRASH_POINT', $point)
        [Environment]::SetEnvironmentVariable('ALBUMEN_E1S6_HARNESS_MODE', 'crash')
        $crash = Start-Process -FilePath $InfrastructureExecutable -ArgumentList @(
            '-automated', 'async', '-parallel', 'none', '-noLogo', '-noColor', '-method', $method
        ) -NoNewWindow -Wait -PassThru
        if ($crash.ExitCode -eq 0 -or -not (Test-Path -LiteralPath $statePath)) {
            throw "Crash injection did not terminate after persisting $point. Exit=$($crash.ExitCode)."
        }
        # FailFast can leave Windows Error Reporting briefly inspecting the
        # terminated process. Wait for that OS-owned crash boundary before the
        # independent restart opens SQLite; the restart itself performs no retry.
        Start-Sleep -Milliseconds 500

        [Environment]::SetEnvironmentVariable('ALBUMEN_E1S6_HARNESS_MODE', 'restart')
        $restart = Start-Process -FilePath $InfrastructureExecutable -ArgumentList @(
            '-automated', 'async', '-parallel', 'none', '-noLogo', '-noColor', '-method', $method
        ) -NoNewWindow -Wait -PassThru
        if ($restart.ExitCode -ne 0 -or -not (Test-Path -LiteralPath ($statePath + '.verified'))) {
            throw "Restarted Results did not verify $point. Exit=$($restart.ExitCode)."
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('ALBUMEN_E1S6_CRASH_POINT', $savedCrash)
    [Environment]::SetEnvironmentVariable('ALBUMEN_E1S6_HARNESS_MODE', $savedMode)
    [Environment]::SetEnvironmentVariable('ALBUMEN_E1S6_MANIFEST_PATH', $savedState)
}

[pscustomobject]@{ CrashPoints = $points.Count; VerifiedRestarts = $points.Count }
