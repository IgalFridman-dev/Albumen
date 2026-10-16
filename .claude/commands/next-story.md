---
description: Start the next eligible PhotoSift story (or the one given) using the story-workflow skill
argument-hint: "[story id, e.g. E2-S4 or PM-006]"
---
Target: $ARGUMENTS (if empty, pick the next eligible item from sprint-status.yaml; if none is
eligible in the open lane, list the candidates and ask the owner which lane to open).
Follow the `story-workflow` skill. Stop after the design doc for owner approval.
