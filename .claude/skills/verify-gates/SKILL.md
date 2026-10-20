---
name: verify-gates
description: Build, test and run Albumen's verification gates (local dotnet test, GitHub CI, Golden Hyper-V VM Architecture/Core/Full stages, release packaging) and report what actually passed. Use when asked to test, verify, or package Albumen.
---

# Verification gates

Windows + .NET SDK 10.0.400 only. A Linux sandbox can read and edit code but cannot build or run
WPF; say "not run" for anything it could not execute.

## Which check proves what
| Check | Proves | Does not prove |
|---|---|---|
| Local `dotnet test` | Unit/scenario behavior on the dev host | Anything the host blocks (see below) |
| CI (`.github/workflows/ci.yml`, windows-latest; releases into `main` and manual runs only) | Locked restore, warnings-as-errors build, suites minus `Category=GoldenVm` | Action/Recycle Bin/SQLite acceptance, offline smoke |
| Golden VM `Full` | The official gate for a story | - |

Never report CI green or a host run as Architecture/Core/Full PASS.

## Quick local loop
```powershell
dotnet restore Albumen.slnx --locked-mode
dotnet build Albumen.slnx -c Release --no-restore
dotnet test tests/Albumen.Domain.Tests -c Release --no-build      # fastest
dotnet test Albumen.slnx -c Release --no-build --filter "Category!=GoldenVm"
Get-ChildItem tests/tooling -Filter *.Tests.ps1 | % { pwsh -NoProfile -File $_.FullName }  # tooling
```
The tooling tests are plain scripts, not Pester. CI runs the four that are safe on a hosted
runner (NetworkAudit, Release, ReleaseNativeCommand, RuntimeBoundary) under both shells; the
Golden VM, Hyper-V, crash-harness and ReleaseInputs tests need the owner's non-elevated host.
`Category=GoldenVm` tests mutate real files and the Recycle Bin and refuse to run outside a
disposable VM, so they fail on the host by design. Run suites one at a time; the WPF scenario
tests have wall-clock budgets that parallel runs can exceed.

## Official gates (owner's machine, elevated PowerShell)
The dev host's Application Control blocks unsigned builds, so gates run in a NIC-free Hyper-V
"Golden VM" restored from `Toolchain-Evaluation-Base`.
- One-time: `eng/verification/Repair-AlbumenGoldenVmCredential.ps1`, then
  `eng/verification/Initialize-AlbumenGoldenVm.ps1`.
- One stage: `pwsh -NoProfile -File eng/verification/Invoke-AlbumenHyperV.ps1 -Stage Full`
  (`Architecture` = Infrastructure/architecture tests, `Core` = Domain+Application+Analysis+
  Scenarios, `Full` = all five suites + runtime boundary + offline WPF smoke).
- All stages, one prompt: `eng/verification/Invoke-AlbumenHyperVStages.ps1`.
- Record the run id (e.g. `20260921-131737-full-3eefc956`) and the pass counts in the evidence file.
- Known flaky: the routed Ctrl+A focus test `RoutedCtrlASelectsOnlyTheGroupContainingKeyboardFocus`
  in `WpfReviewSelectionTests.cs`. Report a failure; never skip it.

## Release
`eng/release/New-AlbumenRelease.ps1` builds the self-contained `win-x64` ZIP (bundled .NET and
WPF runtime, PM-008) and
`Test-AlbumenRelease.ps1` re-verifies it. Required: two identical publishes, SHA256SUMS, and
owner approval of the dependency inventory (`release-dependencies.json`). Any package change goes
through that gate.
