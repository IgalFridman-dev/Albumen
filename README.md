# PhotoSift

PhotoSift is a privacy-first Windows 11 desktop application for safely finding and reviewing exact duplicates, near duplicates, and likely blurry photos in ordinary local folders. Analysis stays on-device, recommendations remain explainable, and no file is removed without explicit approval. Approved removals use the Windows Recycle Bin only.

## Project status

Development status: **6 of 26 stories complete; 20 remain. Epic 1 is complete.** E1-S5 was merged into `develop` through [PR #1](https://github.com/IgalFridman-dev/PhotoSift/pull/1); the E1-S6 branch still requires its separate integration decision. GitHub's default `main` branch is a separate integration state; use the [`develop` branch](https://github.com/IgalFridman-dev/PhotoSift/tree/develop) for integrated progress.

Planning is complete and validated. E1-S1 through E1-S6 are done. E1-S6 adds restart-only Action Records, explicit read-only revalidation, accessible progress, and conservative unknown outcomes. Its user-facing safety behavior passed explicit manual verification after the approved two-attempt VM/timing cap stopped the real COM callback crash harness. The deferred automated edge coverage is [PM-001](docs/backlog/post-mvp.md#pm-001--harden-e1-s6-real-process-crash-boundary-automation).

- Latest completed story: **E1-S6 — Reconcile interrupted Actions and retain truthful records (done)**
- Next eligible story: **E2-S1 — Establish forward-only SQLite schema and serialized ownership (ready for development)**
- E1-S6 final evidence and manual acceptance: [`docs/evidence/e1-s6/verification.md`](docs/evidence/e1-s6/verification.md)
- Historical E1-S6 candidate gates: Core **888/888**, Architecture **536/536**, Full **1,424/1,424** plus runtime-boundary and offline WPF smoke; the later real callback-boundary limitation is recorded separately and is not represented as an automated pass.
- E1-S5 acceptance and closing observations (committed as `0bc0155`): [`docs/evidence/e1-s5/verification.md`](docs/evidence/e1-s5/verification.md)
- Current tracking: [`sprint-status.yaml`](_bmad-output/implementation-artifacts/sprint-status.yaml)
- Fresh original-host verification: Core **847/847**, Architecture **490/490**, Full **1,337/1,337** — [evidence](docs/evidence/e1-s5/return-host-verification.md)
- Story contracts: [`_bmad-output/specs/`](_bmad-output/specs/)
- Documentation map: [`docs/README.md`](docs/README.md)
- E1-S1 verification: [`docs/evidence/e1-s1/verification.md`](docs/evidence/e1-s1/verification.md)
- E1-S2 final verification: [`docs/evidence/e1-s2/verification.md`](docs/evidence/e1-s2/verification.md)
- E1-S3 final verification: [`docs/evidence/e1-s3/verification.md`](docs/evidence/e1-s3/verification.md)
- E1-S3 exact corpus: [`docs/evidence/e1-s3/exact-corpus.md`](docs/evidence/e1-s3/exact-corpus.md)
- E1-S3 concurrency: [`docs/evidence/e1-s3/concurrency.md`](docs/evidence/e1-s3/concurrency.md)
- E1-S3 directory coverage: [`docs/evidence/e1-s3/directory-coverage.md`](docs/evidence/e1-s3/directory-coverage.md)
- E1-S4 final verification: [`docs/evidence/e1-s4/verification.md`](docs/evidence/e1-s4/verification.md)
- E1-S4 concurrency: [`docs/evidence/e1-s4/concurrency.md`](docs/evidence/e1-s4/concurrency.md)
- E1-S4 SQLite override durability: [`docs/evidence/e1-s4/sqlite-override.md`](docs/evidence/e1-s4/sqlite-override.md)
- E1-S4 Review accessibility: [`docs/evidence/e1-s4/review-accessibility.md`](docs/evidence/e1-s4/review-accessibility.md)
- E1-S4 dependency/runtime inventory: [`docs/evidence/e1-s4/dependency-runtime-inventory.md`](docs/evidence/e1-s4/dependency-runtime-inventory.md)

The mandatory first-epic sequence is:

```text
E1-S1 -> E1-S2 -> E1-S3 -> E1-S4 -> E1-S5 -> E1-S6 safety gate
```

Epic 1 completed on 2026-09-14 through the explicitly approved E1-S6 manual fallback. Epic 2 is now unlocked and follows its own dependency order. Semantic or Likely Matches work remains in Epic 4 and still depends on the earlier persistence and visual-analysis foundations.

## Product boundary

The MVP is a native, offline-first Windows application built as a hexagonal modular monolith. It analyzes one explicitly selected local folder, keeps source-photo mutation behind a durable and revalidated Action boundary, and degrades optional semantic capability without blocking the core cleanup workflow.

The project deliberately excludes cloud inference, accounts, telemetry, automatic or permanent deletion, and general-purpose photo-library management.

## Implementation workflow

1. Read the current story's `SPEC.md` and its linked upstream decisions.
2. Implement only that story's contract and acceptance criteria.
3. Run its required automated, integration, and platform evidence.
4. Review the change before marking the story done.
5. Update `_bmad-output/implementation-artifacts/sprint-status.yaml` without bypassing its execution policy.
6. Follow `sprint-status.yaml`; E2-S1 is the next eligible story after the completed Epic 1 gate.

The repository contains the .NET 10/C# 14/WPF `win-x64` application with exact local Scope selection/preflight, Exact duplicate detection, survivor-safe Review Selection, durable override evidence, one-item Recycle Bin execution through the sole Action owner, and completed E1-S6 restart reconciliation, plus five test projects and a NIC-free Golden Hyper-V VM verification gate. Review approval supplies an exact sealed request; fresh authority/survivor checks and durable Requested/Executing commits must still precede every shell call. Uncertain or interrupted work is never automatically replayed.

## Repository layout

```text
PhotoSift/
|-- README.md
|-- PhotoSift.slnx               # Six production projects and five test projects
|-- src/                          # Native offline structural seed
|-- tests/                        # Unit, architecture, scenario, and tooling tests
|-- eng/                          # Runtime/offline verification harnesses
|-- docs/                         # Human-facing documentation index
`-- _bmad-output/
    |-- planning-artifacts/       # Product, UX, architecture, and backlog
    |-- specs/                    # One implementation contract per story
    `-- implementation-artifacts/ # Sprint tracking and implementation evidence
```

Local `.uv-cache-bmad-*` directories are disposable workflow caches and are not project artifacts.

## Build and verification

The repository pins .NET SDK `10.0.400` and requires the .NET/Desktop `10.0.11` runtime family. The complete executable gate is:

Capture and validate the clean credential baseline once from an elevated PowerShell window. This authenticates the current VM state before any restore, preserves `Clean-Evaluation-Base`, and creates `Credential-Evaluation-Base` without saving or changing the password:

```powershell
pwsh -NoProfile -File eng/verification/Repair-PhotoSiftGoldenVmCredential.ps1
```

Then provision the toolchain checkpoint once. This restores `Credential-Evaluation-Base` and creates a re-authenticated `Toolchain-Evaluation-Base`:

```powershell
pwsh -NoProfile -File eng/verification/Initialize-PhotoSiftGoldenVm.ps1
```

Normal gates restore `Toolchain-Evaluation-Base` and copy only the project source:

```powershell
pwsh -NoProfile -File eng/verification/Invoke-PhotoSiftHyperV.ps1 -Stage Full
```

From an elevated PowerShell window, run all three local gates with one guest credential prompt:

```powershell
pwsh -NoProfile -File eng/verification/Invoke-PhotoSiftHyperVStages.ps1
```

The Golden Hyper-V VM is used because this development host's Application Control policy blocks unsigned locally built executables and Windows Sandbox also blocks the xUnit adapter. The VM has zero virtual NICs. `Clean-Evaluation-Base` remains the immutable emergency checkpoint; `Credential-Evaluation-Base` seals the verified local account; normal runs restore the additive toolchain checkpoint and do not weaken host or guest policy.
