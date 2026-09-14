[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ScenarioExecutable,
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
$method = 'PhotoSift.Scenarios.Tests.ActionExecution.E1S6CrashRecoveryHarnessTests.ProcessHarnessChildPersistsBoundaryThenRestartProjectsStableResults'
$savedCrash = [Environment]::GetEnvironmentVariable('PHOTOSIFT_E1S6_CRASH_POINT')
$savedRestart = [Environment]::GetEnvironmentVariable('PHOTOSIFT_E1S6_RESTART_POINT')
$savedState = [Environment]::GetEnvironmentVariable('PHOTOSIFT_E1S6_STATE_PATH')
try {
    New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
    foreach ($point in $points) {
        $statePath = Join-Path $ResultsDirectory ($point + '.json')
        [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_STATE_PATH', $statePath)
        [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_CRASH_POINT', $point)
        [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_RESTART_POINT', $null)
        $crash = Start-Process -FilePath $ScenarioExecutable -ArgumentList @(
            '-automated', 'async', '-parallel', 'none', '-noLogo', '-noColor', '-method', $method
        ) -NoNewWindow -Wait -PassThru
        if ($crash.ExitCode -eq 0 -or -not (Test-Path -LiteralPath $statePath)) {
            throw "Crash injection did not terminate after persisting $point. Exit=$($crash.ExitCode)."
        }

        [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_CRASH_POINT', $null)
        [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_RESTART_POINT', $point)
        $restart = Start-Process -FilePath $ScenarioExecutable -ArgumentList @(
            '-automated', 'async', '-parallel', 'none', '-noLogo', '-noColor', '-method', $method
        ) -NoNewWindow -Wait -PassThru
        if ($restart.ExitCode -ne 0 -or -not (Test-Path -LiteralPath ($statePath + '.verified'))) {
            throw "Restarted Results did not verify $point. Exit=$($restart.ExitCode)."
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_CRASH_POINT', $savedCrash)
    [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_RESTART_POINT', $savedRestart)
    [Environment]::SetEnvironmentVariable('PHOTOSIFT_E1S6_STATE_PATH', $savedState)
}

[pscustomobject]@{ CrashPoints = $points.Count; VerifiedRestarts = $points.Count }
