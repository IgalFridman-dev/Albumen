# Albumen — guide for Claude Code

Albumen is a Windows 11 WPF app (.NET 10 / C# 14, `win-x64`) that finds Exact and
conservative Near duplicate JPEG/PNG photos in one local folder, lets the user review
groups, and moves only approved files to the Recycle Bin. Offline, no telemetry, no
permanent deletion. V1 is a technically accepted recruiter-demo candidate (commit `b0c4c13`).

Read [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) first for the map of the code and UI.

## Where things are

| Need | Path |
|---|---|
| App code (6 projects) | `src/` — Domain → Application → Analysis / Infrastructure.* → App |
| Tests (5 xUnit v3 projects + PowerShell tooling tests) | `tests/` |
| Build/verify/release scripts (PowerShell) | `eng/verification/`, `eng/release/` |
| Product, UX, architecture, epics (BMAD planning) | `_bmad-output/planning-artifacts/` |
| One contract per story (`SPEC.md`) | `_bmad-output/specs/spec-eX-sY-*/` |
| Sprint state — source of truth for "what's next" | `_bmad-output/implementation-artifacts/sprint-status.yaml` |
| Per-story design + implementation plans | `docs/superpowers/specs/`, `docs/superpowers/plans/` |
| Verification evidence per story | `docs/evidence/eX-sY/` |
| Labelled photo corpus tool (E3-S6, not shipped) | `tools/Albumen.Corpus/` |
| Backlog (PM-001…PM-018) | `docs/backlog/post-mvp.md` |
| Release plan for 1.1 (SigLIP 2, text search, blur) | `docs/superpowers/specs/2026-10-19-albumen-1.1-siglip2-release-plan.md` |
| Docs index / authority rules | `docs/README.md` |

The folder names `_bmad-output/` and `docs/superpowers/` are kept from the original
BMAD + Superpowers (Codex) workflow because hundreds of evidence links point into them.

## Rules that matter

- **Architecture boundaries** (Architecture Spine): Domain has no dependencies; Application
  depends only on Domain and defines ports; Infrastructure implements ports; only
  `Albumen.App/Composition/CompositionRoot.cs` wires concrete types. Architecture tests
  in `tests/Albumen.Infrastructure.Tests` enforce this — don't weaken them.
- **Safety invariants**: never delete permanently; every Recycle Bin call goes through the
  single Action owner after a sealed approval, fresh revalidation and durable
  Requested/Executing journal rows; uncertain work is never auto-replayed; at least one
  survivor per group.
- **Offline**: no network calls, no telemetry, no cloud inference. E5-S3 proves this.
- **YAGNI**: implement only the current story's `SPEC.md`, clear and concise, only what the
  current release needs. 1.1 = SigLIP 2 look-alikes, text search and classical blur; 1.2 =
  accessibility, dark mode, Signature reuse; see the release plan.
- **Truthful status**: never mark a story done or claim PASS without the evidence the SPEC
  requires. Record limitations instead (see `CONCERNS` verdict in E5-S5).
- Don't claim accessibility compliance (PM-005 open).
- Package pins: SDK `10.0.400` (`global.json`, rollForward disabled), central versions in
  `Directory.Packages.props`, locked restore (`packages.lock.json`). Changing a package
  requires the release inventory/owner-approval gate in `eng/release/`.

## Building and testing

Needs Windows with .NET SDK 10.0.400. From the repo root:

```powershell
dotnet restore Albumen.slnx --locked-mode
dotnet build Albumen.slnx -c Release --no-restore
dotnet test  Albumen.slnx -c Release --no-build
```

The official gates (Architecture / Core / Full, last Full = 1,888/1,888 on `e6b6a74`) run inside a NIC-free
Hyper-V "Golden VM" because the dev host blocks unsigned binaries: see the `verify-gates`
skill. A Linux container can read and edit code but cannot build or run WPF.

## How work is done here (converted workflow)

| Original (Codex) | Claude Code equivalent |
|---|---|
| BMAD story creation / `SPEC.md` | `story-workflow` skill, step 1 |
| Superpowers brainstorming → design spec | `design-and-plan` skill |
| Superpowers writing-plans → plan with checkboxes | `design-and-plan` skill |
| Superpowers TDD / subagent-driven development | `story-workflow` skill, step 3 |
| BMAD code-review ("fresh context") | `story-reviewer` agent |
| Golden VM verification | `verify-gates` skill |
| BMAD sprint status | `/status` and `/next-story` commands |

Commit style: conventional prefixes (`feat:`, `fix:`, `test:`, `docs:`), small commits,
work on a feature branch off `develop`. Write commit messages and PR bodies with the
`commit-and-pr` skill (short, no file lists, say what was and was not verified).
