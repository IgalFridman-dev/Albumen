---
name: story-workflow
description: Implement a PhotoSift story (E#-S#), a backlog item (PM-###) or a post-V1 bug fix end to end - SPEC, plan, test-first build, review, verify, evidence, status. Use when starting or continuing any such work.
---

# PhotoSift story workflow

Converted from the BMAD (planning) + Superpowers (implementation) flow used to build E1-S1…E5-S5.
Branch off `develop` (`main` is stable releases only). Commit and PR wording: `commit-and-pr` skill.

## Story or backlog item
1. **Pick it.** Read `_bmad-output/implementation-artifacts/sprint-status.yaml`
   (`development_status`, `implementation_policy`). Only `backlog` stories whose dependencies are
   `done` are eligible; `deferred-v1.1` / `deferred-v1.2` stay closed until the owner opens that
   lane. Trust `development_status`, not the `next_eligible_story` comment. PM-### items live in
   `docs/backlog/post-mvp.md`. Ignore the Codex-era model-selection notes in the policy block.
2. **Read the contract.** `_bmad-output/specs/spec-<id>-*/SPEC.md` plus every upstream doc it
   links (PRD, UX, Architecture Spine). If the SPEC conflicts with reality, update the SPEC and
   route product/UX/architecture changes to their canonical artifact; never widen silently.
3. **Design and plan** with the `design-and-plan` skill. Stop for owner approval of the design.
4. **Build test-first.** Per plan task: write the failing test, make it pass with the smallest
   change, commit (`test:` then `feat:`/`fix:`), tick the plan checkbox. Keep the architecture
   boundaries in `CLAUDE.md`.
5. **Review.** Run the `story-reviewer` agent on the diff. Fix Critical/Important findings;
   record anything accepted.
6. **Verify** with the `verify-gates` skill. Only the owner can run the Golden VM; if you can't,
   say so and leave the story `review`, not `done`. A green CI run does not replace it.
7. **Record evidence** in `docs/evidence/<id>/verification.md`: commit, gate run ids, pass
   counts, manual checks, limitations. No PASS without proof.
8. **Update status.** Set the story in `sprint-status.yaml` with a one-line comment pointing to
   the evidence and update `progress_summary`. Edit the status text in `README.md` and
   `docs/README.md` only if it names the story.

## Bug fix after V1 (no SPEC)
1. Reproduce it first: a failing test, or the exact steps and error text if only the Golden VM
   can show it.
2. Find the cause. If it isn't established, say so in the PR rather than guessing.
3. Write the failing test, fix, run `story-reviewer`, run the gates the change touches.
4. If a limitation remains or the fix defers work, add or update a `PM-###` entry in
   `docs/backlog/post-mvp.md` instead of fixing outside scope.
5. Open the PR to `develop`; the Verified section lists exactly what ran and what did not.
