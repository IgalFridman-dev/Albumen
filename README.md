# PhotoSift

PhotoSift is a privacy-first Windows 11 desktop application for safely finding and reviewing exact and near-duplicate photos in ordinary local folders. Analysis stays on-device, results remain explainable, and no file is removed without explicit approval. Approved removals use the Windows Recycle Bin only. Blur is planned for V1.1 and Semantic/Likely Matches for V1.2.

## Project status

Development status: **17 of 26 stories are complete and 9 remain: 2 lean V1 stories, 2 deferred V1.1 stories, and 5 deferred V1.2 stories. E5-S3 is complete; E5-S4 is next and remains unstarted. E5-S1 remains complete by explicit product-owner scope waiver for the recruiter-facing MVP, not by a complete accessibility pass.** E1-S5 was merged into `develop` through [PR #1](https://github.com/IgalFridman-dev/PhotoSift/pull/1); later completed work remains stacked locally pending integration decisions. GitHub's default `main` branch is a separate integration state; use the [`develop` branch](https://github.com/IgalFridman-dev/PhotoSift/tree/develop) for integrated progress.

Planning is complete and validated. E1-S1 through E1-S6 are done. E1-S6 adds restart-only Action Records, explicit read-only revalidation, accessible progress, and conservative unknown outcomes. Its user-facing safety behavior passed explicit manual verification after the approved two-attempt VM/timing cap stopped the real COM callback crash harness. The deferred automated edge coverage is [PM-001](docs/backlog/post-mvp.md#pm-001--harden-e1-s6-real-process-crash-boundary-automation).

- Latest completed story: **E5-S3 — Offline privacy and privacy-minimized diagnostics**
- Next eligible story: **E5-S4 — Package a self-contained licensed release candidate**
- E5-S3 evidence: [`docs/evidence/e5-s3/verification.md`](docs/evidence/e5-s3/verification.md)
- E5-S2 evidence: [`docs/evidence/e5-s2/golden-vm-results.md`](docs/evidence/e5-s2/golden-vm-results.md) and [`fixture-manifest.json`](docs/evidence/e5-s2/fixture-manifest.json)
- E5-S1 evidence: [`docs/evidence/e5-s1/verification.md`](docs/evidence/e5-s1/verification.md)
- Deferred E5-S1 accessibility hardening: [`PM-005`](docs/backlog/post-mvp.md#pm-005--complete-accessibility-hardening-after-the-recruiter-demo-v1)
- Approved YAGNI release roadmap: [`docs/superpowers/specs/2026-09-15-yagni-release-roadmap-design.md`](docs/superpowers/specs/2026-09-15-yagni-release-roadmap-design.md)
- Lean V1 delivery plan: [`docs/superpowers/plans/2026-09-15-yagni-v1-delivery.md`](docs/superpowers/plans/2026-09-15-yagni-v1-delivery.md)
- E2-S2 approved design: [`docs/superpowers/specs/2026-09-14-e2-s2-scan-checkpoints-design.md`](docs/superpowers/specs/2026-09-14-e2-s2-scan-checkpoints-design.md)
- E2-S2 verification and closure: [`docs/evidence/e2-s2/verification.md`](docs/evidence/e2-s2/verification.md)
- E2-S3 verification and closure: [`docs/evidence/e2-s3/verification.md`](docs/evidence/e2-s3/verification.md)
- E2-S2 pre-release presentation follow-up: [`PM-003`](docs/backlog/post-mvp.md#pm-003--align-e2-s2-scan-detail-and-coverage-wording-before-release)
- E2-S1 design: [`docs/superpowers/specs/2026-09-14-e2-s1-lean-sqlite-foundation-design.md`](docs/superpowers/specs/2026-09-14-e2-s1-lean-sqlite-foundation-design.md)
- E2-S1 verification and closing observations: [`docs/evidence/e2-s1/verification.md`](docs/evidence/e2-s1/verification.md)
- Deferred final E2-S1 UI verification: [`PM-002`](docs/backlog/post-mvp.md#pm-002--verify-the-e2-s1-startup-failure-ui-on-a-stable-runnable-shell)
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

Epic 1 completed on 2026-09-14 through the explicitly approved E1-S6 manual fallback. The 2026-09-15 YAGNI reset makes Exact/Near cleanup the V1 product: Signature reuse and Blur are V1.1; Semantic/Likely Matches is V1.2.

## Product boundary

V1 is a native, offline-first Windows application built as a hexagonal modular monolith. It analyzes one explicitly selected local folder, finds Exact and conservative Near Duplicates, supports manual grouped Review, and keeps source-photo mutation behind a durable and revalidated Action boundary.

The project deliberately excludes cloud inference, accounts, telemetry, automatic or permanent deletion, and general-purpose photo-library management.

## Implementation workflow

1. Read the current story's `SPEC.md` and its linked upstream decisions.
2. Implement only that story's contract and acceptance criteria.
3. Run its required automated, integration, and platform evidence.
4. Review the change before marking the story done.
5. Update `_bmad-output/implementation-artifacts/sprint-status.yaml` without bypassing its execution policy.
6. Follow the YAGNI roadmap and `sprint-status.yaml`; E5-S3 is complete and E5-S4 is next but unstarted. E5-S1 is done only by the recorded recruiter-MVP scope waiver, with remaining accessibility work in PM-005 and no accessibility-complete claim. PM-001 and PM-002 retain their separate dispositions; PM-003 is closed.

The repository contains the .NET 10/C# 14/WPF `win-x64` application with exact local Scope selection/preflight, Exact duplicate detection, survivor-safe Review Selection, durable override evidence, one-item Recycle Bin execution through the sole Action owner, completed E1-S6 restart reconciliation, and an exact version-5 SQLite foundation with durable Scan identity, `Enumeration`/`Exact` checkpoints, and reconciled row-truth accounting, plus five test projects and a NIC-free Golden Hyper-V VM verification gate. Review approval supplies an exact sealed request; fresh authority/survivor checks and durable Requested/Executing commits must still precede every shell call. Uncertain or interrupted work is never automatically replayed.

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
