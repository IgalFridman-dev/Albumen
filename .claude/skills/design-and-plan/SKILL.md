---
name: design-and-plan
description: Turn a Albumen story SPEC or idea into an approved design doc and a step-by-step implementation plan before writing code. Use before implementing any non-trivial story; skip for one-file fixes.
---

# Design, then plan (Superpowers brainstorming + writing-plans, converted)

Skip both documents for a bug fix or doc change that touches one area: put a short plan in the
PR instead. Write them when the change adds a port, touches the schema or the Action path, or
spans projects.

## Design doc — `docs/superpowers/specs/YYYY-MM-DD-<id>-<slug>-design.md`
1. Read the story SPEC, the Architecture Spine and the relevant code (`docs/HOW-IT-WORKS.md`).
2. Ask the owner only the questions whose answers change scope. Propose 2–3 approaches and
   recommend the smallest sound one (lean V1 / YAGNI).
3. Write: goal, non-goals, chosen approach, affected projects and ports, data/schema changes
   (SQLite schema is forward-only), failure and safety behavior, test strategy, and a table
   mapping each SPEC acceptance criterion to its test.
4. Get owner approval before planning.

## Plan — `docs/superpowers/plans/YYYY-MM-DD-<id>-<slug>.md`
- Header: links to the design and SPEC, branch, starting commit.
- Numbered tasks as `- [ ]` checkboxes, each small enough for one commit and naming the files,
  the failing test to write first, the command that proves it, and the commit message.
- Final tasks: review (`story-reviewer`), gates (`verify-gates`), evidence file, sprint status.
- Tick a box only when the work landed; never tick a gate that was not run.
