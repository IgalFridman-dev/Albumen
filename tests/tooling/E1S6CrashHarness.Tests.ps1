BeforeAll {
    $script:Harness = Join-Path $PSScriptRoot '..\..\eng\verification\Invoke-E1S6CrashHarness.ps1'
    $script:Root = Join-Path ([System.IO.Path]::GetTempPath()) ('photosift-e1s6-harness-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Root | Out-Null
    $script:Fake = Join-Path $script:Root 'scenario.cmd'
    @'
@echo off
if not "%PHOTOSIFT_E1S6_CRASH_POINT%"=="" (
  echo {"DurableRows":1} > "%PHOTOSIFT_E1S6_STATE_PATH%"
  exit /b 23
)
if not "%PHOTOSIFT_E1S6_RESTART_POINT%"=="" (
  echo stable-no-replay>"%PHOTOSIFT_E1S6_STATE_PATH%.verified"
  exit /b 0
)
exit /b 99
'@ | Set-Content -LiteralPath $script:Fake -Encoding ascii
}

AfterAll {
    Remove-Item -LiteralPath $script:Root -Recurse -Force
}

Describe 'E1-S6 crash harness' {
    It 'forces and verifies all six bounded restart points' {
        $results = Join-Path $script:Root 'results'
        $outcome = & $script:Harness -ScenarioExecutable $script:Fake -ResultsDirectory $results
        $outcome.CrashPoints | Should -Be 6
        $outcome.VerifiedRestarts | Should -Be 6
        (Get-ChildItem -LiteralPath $results -Filter '*.verified').Count | Should -Be 6
    }
}
