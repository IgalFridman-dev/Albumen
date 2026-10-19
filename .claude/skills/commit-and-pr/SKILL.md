---
name: commit-and-pr
description: Write Albumen commit messages and PR titles/descriptions. Use before every git commit, PR creation, or PR body edit in this repo.
---

# Commit and PR messages

Write for a reviewer who knows the codebase and will read the diff. Say what the
diff can't: the symptom, the cause, the decision, what was and wasn't verified.

## Commits

- Subject: `type: what changes for the user or the code`, imperative, lowercase
  after the prefix, no period, ≤ 60 chars. Types: `feat fix test docs build
  refactor chore`. Name the behavior, not the file (`fix: show saved Action
  Records after relaunch`, not `fix: update ActionRecordsViewModel.cs`).
- Body only when the why isn't obvious. Two to four lines: the cause and the fix.
  Wrap at 72. No file lists, no "This commit…", no investigation diary.
- One logical change per commit. A test that drives a fix may be its own `test:`
  commit before the `fix:`.

Good:
```
fix: checkpoint the WAL before the writer closes

A clean close left an empty -wal and -shm, and the next start refused them
as retained journal state. TRUNCATE checkpoint before close removes them.
```

## PR title

Same rule as a commit subject, describing the whole PR. Never a branch name.

## PR body

Keep it under ~20 lines. Sections, in order, each only if it has content:

1. **Before / After** — two short paragraphs a non-author can follow: what a
   user or developer saw, and what they see now. Quote the exact error text if
   there was one.
2. **Why** — the root cause in one to three sentences, when it isn't obvious.
   Say plainly if the cause is not fully known.
3. **Verified** — bullets of what actually ran and where (Golden VM stage, host
   `dotnet test` counts, UI smoke run), with commit SHAs. List what did **not**
   run. Never imply a gate passed without its evidence (CLAUDE.md "Truthful
   status").
4. **Follow-ups** — backlog IDs (PM-00x) only; no prose roadmap.

Required lines the harness asks for (attribution block at the top, one
generated-by footer at the bottom) go in as-is, once each.

## Cut these

- "Summary", "Changes", "Implementation notes" headings that restate the diff.
- File-by-file bullet lists. The Files tab already shows them; name a file only
  when the reader needs it to follow the Why.
- Filler: "This PR…", "comprehensive", "robust", "ensure", "various",
  "improve", "enhance", "seamlessly", "In order to".
- Bold on every other phrase, nested bullets, emoji (except the required footer).
- Repeating the same fact in the title, Before/After and Why.
- Duplicate footers or session links.

## Before you send

Re-read and delete any sentence that would still be true for a different PR.
