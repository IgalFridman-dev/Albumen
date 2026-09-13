# PhotoSift

PhotoSift is a privacy-first Windows 11 desktop application for safely finding and reviewing exact duplicates, near duplicates, and likely blurry photos in ordinary local folders. Analysis stays on-device, recommendations remain explainable, and no file is removed without explicit approval. Approved removals use the Windows Recycle Bin only.

## Project status

Development status: **5 of 26 stories complete; 21 remain. E1-S6 is next.** E1-S5 was merged into `develop` through [PR #1](https://github.com/IgalFridman-dev/PhotoSift/pull/1). GitHub's default `main` branch is a separate integration state; use the [`develop` branch](https://github.com/IgalFridman-dev/PhotoSift/tree/develop) for the accepted development progress.

Planning is complete and validated. E1-S1 through E1-S5 are implemented, reviewed, verified by their complete offline Full gates, and done. E1-S5 adds one-item Windows Recycle Bin execution behind fresh safety checks and separate durable Requested/Executing barriers. Final reviewed Full passed 1,337/1,337 tests plus runtime-boundary and offline WPF smoke. The execution policy remains pinned to E1-S5: E1-S6 reconciliation and the Epic 1 safety gate require separate authorization; E1-S6 and Epics 2–5 remain backlog.

- Latest completed story: **E1-S5 — Execute one-item Recycle Bin operations behind a durable barrier (done)**
- Current implementation authorization: **E1-S5 remains the policy lock; E1-S6 and later work are not authorized**
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

Each Epic 1 story must be complete before the next begins. No later epic—and especially no semantic or Likely Matches work—may begin until the E1-S6 Windows safety harness records `PASS`. The execution policy in `sprint-status.yaml` is authoritative for this gate.

## Product boundary

The MVP is a native, offline-first Windows application built as a hexagonal modular monolith. It analyzes one explicitly selected local folder, keeps source-photo mutation behind a durable and revalidated Action boundary, and degrades optional semantic capability without blocking the core cleanup workflow.

The project deliberately excludes cloud inference, accounts, telemetry, automatic or permanent deletion, and general-purpose photo-library management.

## Implementation workflow

1. Read the current story's `SPEC.md` and its linked upstream decisions.
2. Implement only that story's contract and acceptance criteria.
3. Run its required automated, integration, and platform evidence.
4. Review the change before marking the story done.
5. Update `_bmad-output/implementation-artifacts/sprint-status.yaml` without bypassing its execution policy.
6. Stop at E1-S6 until the safety gate is explicitly recorded as `PASS`.

The repository contains the .NET 10/C# 14/WPF `win-x64` application with exact local Scope selection/preflight, Exact duplicate detection, survivor-safe Review Selection, durable override evidence, and one-item Recycle Bin execution through the sole Action owner, plus five test projects and a NIC-free Golden Hyper-V VM verification gate. Review approval supplies an exact sealed request; fresh authority/survivor checks and durable Requested/Executing commits must still precede every shell call. Uncertain or interrupted work is never automatically replayed. Reconciliation and later-story behavior remain outside the accepted implementation.

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
