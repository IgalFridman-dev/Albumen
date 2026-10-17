---
description: Start the next eligible PhotoSift story (or the one given) using the story-workflow skill
argument-hint: "[story id, e.g. E2-S4 or PM-006]"
---
Target: $ARGUMENTS. If empty, choose from `development_status` in sprint-status.yaml: a `backlog`
story in an open lane whose dependencies are `done`. If none qualifies, list the candidates with
their lane and ask the owner which lane to open; do not start deferred work.

Follow the `story-workflow` skill on a branch off `develop`. Stop after the design doc and ask
the owner to approve it.
