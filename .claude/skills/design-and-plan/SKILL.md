---
name: design-and-plan
description: Turn a PhotoSift story SPEC or idea into an approved design doc and a step-by-step implementation plan before writing code. Use before implementing any non-trivial story or change.
---

# Design, then plan (Superpowers brainstorming + writing-plans, converted)

## Design doc — `docs/superpowers/specs/YYYY-MM-DD-<slug>-design.md`
1. Read the story SPEC, Architecture Spine and the relevant existing code (`docs/HOW-IT-WORKS.md`).
2. Ask the owner only the questions whose answers change scope; propose 2–3 approaches with a
   recommendation (smallest sound option under the lean V1/YAGNI rule).
3. Write: goal, non-goals, chosen approach, affected projects/ports, data/schema changes
   (SQLite schema is forward-only), failure and safety behavior, test strategy, acceptance mapping
   to the SPEC's criteria. Get owner approval before planning.

## Plan — `docs/superpowers/plans/YYYY-MM-DD-<slug>.md`
- Header: link to design + SPEC, branch, starting commit.
- Numbered tasks, each a `- [ ]` checkbox small enough for one commit, each naming:
  files to touch, the failing test to write first, the command that proves it, the commit message.
- Final tasks: review (`story-reviewer` agent), gates (`verify-gates`), evidence file, sprint status.
- Tick boxes as work lands; never tick a gate that was not actually run.
