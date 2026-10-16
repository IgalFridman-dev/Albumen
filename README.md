# PhotoSift

**Find duplicate photos on Windows, review them safely, and send only what you approve to the Recycle Bin.**

PhotoSift is a privacy-first Windows 11 desktop app (.NET 10, C# 14, WPF). It scans one local
folder for byte-identical and conservatively near-identical JPEG/PNG images, groups them for
side-by-side review, and moves only the files you explicitly approve to the Recycle Bin. It runs
fully offline, never deletes permanently, and keeps a durable record of every action.

> **Status: V1 candidate (accepted for a bounded demo).** 19 of 26 planned stories are done;
> 2 are planned for V1.1 (Blur, Signature reuse) and 5 for V1.2 (Semantic / Likely Matches).
> Full automated suite: **1,757 / 1,757** passing. Release decision: `CONCERNS` with
> documented limitations — see [release readiness](docs/evidence/e5-s5/release-readiness.md).

## Highlights

- **Exact + Near duplicates** — streaming SHA-256 for byte identity (hard links detected as
  aliases), a managed dHash + color signature for conservative near matches.
- **Safety by design** — at least one survivor per group, sealed approval, fresh revalidation
  and a durable SQLite journal before every Recycle Bin call; interrupted actions are reconciled
  on restart, never replayed.
- **Clean architecture** — hexagonal modular monolith: Domain → Application (ports) →
  Infrastructure (Win32, WIC, SQLite) → WPF App, with boundaries enforced by architecture tests.
- **Offline & private** — no network, no telemetry, no accounts; verified with network audits.
- **Reproducible release** — locked package restore, pinned SDK, two identical publishes,
  checksum-verified ZIP and third-party notices.

How it works and looks: **[docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md)**.

## Running it

End users need Windows 11 x64 and the **.NET 10 Desktop Runtime x64 10.0.11+** (no SDK).
Extract the release ZIP and run `Start-PhotoSift.cmd`; the launcher verifies the package first.

Developers need the .NET SDK `10.0.400` (pinned in `global.json`):

```powershell
dotnet restore PhotoSift.slnx --locked-mode
dotnet build   PhotoSift.slnx -c Release --no-restore
dotnet run --project src/PhotoSift.App -c Release
dotnet test    PhotoSift.slnx -c Release --no-build
```

Official verification runs in an isolated, NIC-free Hyper-V VM — see
[`.claude/skills/verify-gates/SKILL.md`](.claude/skills/verify-gates/SKILL.md).

## Known limitations (V1)

- At the default window size Review content can be clipped; maximize and scroll the Approval pane (PM-006).
- Accessibility is not complete: a local-data warning is not announced by Narrator (PM-002, PM-005). No accessibility-compliance claim.
- HEIF depends on the Windows codec and is best effort.
- A first 300-image scan took 12.1 s on a 2-CPU / 4 GiB VM — an observation, not a guarantee.

## Repository layout

```text
src/                     6 production projects (Domain, Application, Analysis, Infrastructure.Sqlite,
                         Infrastructure.Windows, App)
tests/                   5 xUnit v3 projects + Pester tests for tooling
eng/                     verification (Hyper-V VM gates) and release packaging scripts
docs/                    HOW-IT-WORKS, documentation index, per-story evidence, backlog,
                         superpowers/ design specs and implementation plans
_bmad-output/            product brief, PRD, UX, architecture, epics, one SPEC.md per story,
                         sprint-status.yaml
CLAUDE.md, .claude/      Claude Code project guide, skills, agent and commands
```

## How it was built

Planned with the BMAD Method (brief → PRD → UX → architecture → epics → 26 story specs) and
implemented story by story with a test-first design → plan → build → review → evidence loop
(originally Codex + Superpowers, now continued with Claude Code). Every finished story has a
verification record in [`docs/evidence/`](docs/evidence/); the documentation index is
[`docs/README.md`](docs/README.md); progress lives in
[`sprint-status.yaml`](_bmad-output/implementation-artifacts/sprint-status.yaml).

## Notices

No open-source license has been chosen yet. Shipped dependency notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
