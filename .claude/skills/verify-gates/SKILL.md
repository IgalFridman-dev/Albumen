---
name: verify-gates
description: How to build, test and run PhotoSift's verification gates (local dotnet test, Golden Hyper-V VM Architecture/Core/Full stages, runtime boundary, release packaging). Use when asked to test, verify, or package PhotoSift.
---

# Verification gates

Requires Windows + .NET SDK 10.0.400. A Linux sandbox can only do static review.

## Quick local loop
```powershell
dotnet restore PhotoSift.slnx --locked-mode
dotnet build PhotoSift.slnx -c Release --no-restore
dotnet test tests/PhotoSift.Domain.Tests -c Release --no-build   # fastest
dotnet test PhotoSift.slnx -c Release --no-build                  # everything
```
Pester tooling tests: `Invoke-Pester tests/tooling`.

## Official gates (owner's machine, elevated PowerShell)
The dev host's Application Control blocks unsigned builds, so gates run in a NIC-free Hyper-V
"Golden VM" restored from `Toolchain-Evaluation-Base`.
- One-time: `eng/verification/Repair-PhotoSiftGoldenVmCredential.ps1`, then
  `eng/verification/Initialize-PhotoSiftGoldenVm.ps1`.
- One stage: `pwsh -NoProfile -File eng/verification/Invoke-PhotoSiftHyperV.ps1 -Stage Full`
  (`Architecture` = Infrastructure/architecture tests, `Core` = Domain+Application+Analysis+Scenarios,
  `Full` = all five suites + runtime boundary + offline WPF smoke).
- All stages, one prompt: `eng/verification/Invoke-PhotoSiftHyperVStages.ps1`.
Record the run id (e.g. `20260921-131737-full-3eefc956`) and counts in the evidence file.
Known flaky: routed Ctrl+A focus case `WpfReviewSelectionTests.cs:267` — report it, never skip it.

## Release
`eng/release/New-PhotoSiftRelease.ps1` builds the framework-dependent `win-x64` ZIP;
`Test-PhotoSiftRelease.ps1` re-verifies it. Two identical publishes, SHA256SUMS, and owner
approval of the dependency inventory (`release-dependencies.json`) are required.
