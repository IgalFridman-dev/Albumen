# PhotoSift

PhotoSift is a privacy-first Windows 11 desktop application for safely finding and reviewing exact duplicates, near duplicates, and likely blurry photos in ordinary local folders. Analysis stays on-device, recommendations remain explainable, and no file is removed without explicit approval. Approved removals use the Windows Recycle Bin only.

## Project status

Planning is complete and validated. E1-S1 implemented and verified the native offline application boundary and is done. E1-S2 is implemented, reviewed, verified by the complete offline Full gate, and done. Following the approved continuation from its merge into develop, E1-S3 is the sole eligible story and is in design preparation. E1-S4 through E1-S6 and Epics 2–5 remain locked.

- Latest completed story: **E1-S2 — Select and preflight an exact local Scope (done)**
- Next eligible story: **E1-S3 — Detect byte-identical distinct files and hard-link aliases (ready for development; design preparation)**
- Current tracking: [`sprint-status.yaml`](_bmad-output/implementation-artifacts/sprint-status.yaml)
- Story contracts: [`_bmad-output/specs/`](_bmad-output/specs/)
- Documentation map: [`docs/README.md`](docs/README.md)
- E1-S1 verification: [`docs/evidence/e1-s1/verification.md`](docs/evidence/e1-s1/verification.md)
- E1-S2 final verification: [`docs/evidence/e1-s2/verification.md`](docs/evidence/e1-s2/verification.md)

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

The repository contains the .NET 10/C# 14/WPF `win-x64` application with exact local Scope selection and preflight, five test projects, and a NIC-free Golden Hyper-V VM verification gate. Later-story behavior remains intentionally absent.

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
