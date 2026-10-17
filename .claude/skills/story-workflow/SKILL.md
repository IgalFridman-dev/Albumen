---
name: story-workflow
description: Implement a PhotoSift story end to end (SPEC → plan → TDD → review → evidence → sprint status). Use when starting or continuing any E#-S# story or a PM-### backlog item.
---

# PhotoSift story workflow

Converted from the BMAD (planning) + Superpowers (implementation) flow used to build stories E1-S1…E5-S5.

1. **Pick the story.** Read `_bmad-output/implementation-artifacts/sprint-status.yaml`
   (`development_status`, `implementation_policy`, lane notes). Only `backlog` stories whose
   dependencies are `done` are eligible; `deferred-v1.1` / `deferred-v1.2` stay closed until
   the owner opens that lane. Backlog items PM-### live in `docs/backlog/post-mvp.md`.
2. **Read the contract.** `_bmad-output/specs/spec-<id>-*/SPEC.md` plus every upstream doc it
   links (PRD, UX, Architecture Spine). If the SPEC conflicts with reality, update the SPEC
   (and route product/UX/architecture changes to their canonical artifact); never widen silently.
3. **Design and plan.** Use the `design-and-plan` skill. Save to
   `docs/superpowers/specs/YYYY-MM-DD-<id>-<slug>-design.md` and
   `docs/superpowers/plans/YYYY-MM-DD-<id>-<slug>.md`.
4. **Build test-first.** For each plan task: write the failing test (Domain/Application unit,
   Infrastructure/architecture, or Scenarios for WPF), make it pass with the smallest change,
   commit (`test:` then `feat:`/`fix:`), tick the plan checkbox. Keep architecture boundaries
   from `CLAUDE.md`.
5. **Review.** Run the `story-reviewer` agent on the story diff. Fix Critical/Important
   findings; record anything accepted.
6. **Verify.** Run the gates the SPEC requires (`verify-gates` skill). Only the owner can run
   the Golden VM; if you can't, say so and leave the story `review`, not `done`.
7. **Record evidence.** Write `docs/evidence/<id>/verification.md`: commit, gate run ids,
   pass counts, manual checks, limitations. Be exact; no PASS without proof.
8. **Update status.** Set the story in `sprint-status.yaml` with a short comment pointing to the
   evidence, update `progress_summary`, and the status line in `README.md` / `docs/README.md`.
