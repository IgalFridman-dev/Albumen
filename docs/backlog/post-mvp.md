# PhotoSift Post-MVP Engineering Backlog

## PM-001 — Harden E1-S6 real-process crash-boundary automation

**Status:** Backlog

**Priority:** Post-MVP reliability/testing

**Origin:** E1-S6 manual acceptance, 2026-09-14

### Problem

The E1-S6 real-process harness reaches five of the six required durability boundaries, but the `during-shell-callbacks` case does not recover successfully in the Golden Hyper-V VM. After the child is forcibly terminated from the real Windows Recycle Bin callback, a fresh SQLite recovery process reports `ActionRecoveryStatus.Faulted` instead of `Ready`.

The failure repeated in Architecture runs `20260914-121019-architecture-b47aabb3` and `20260914-121219-architecture-b675498d`; the second run included one 500 ms Windows crash-handoff delay. Work stopped after the approved two-attempt limit. No third timing adjustment was attempted.

Read-only follow-up identified the direct fail-closed boundary: `SqliteDatabaseOwner.RefuseRetainedSidecars()` rejects any retained SQLite `-wal`, `-shm`, or `-journal` file before opening the database. A hard process kill may legitimately leave WAL/SHM state, so elapsed time alone does not make this state admissible. The current behavior protects evidence and source photos by refusing startup mutation, but it can prevent Action Records from loading after this exact hard-crash edge case.

Owner observation, 2026-10-16: on a fresh Windows install the restored `%LOCALAPPDATA%\PhotoSift` folder held a
0-byte `photosift.db-wal` and a `photosift.db-shm` dated 2026-10-06. Every launch showed "Local data could not be
safely loaded" and the app offered no in-app way out. The explicit recovery authority above would also cover this case.

### Deferred automated coverage

- Capture and expose the precise retained-sidecar recovery reason without leaking paths or evidence.
- Design explicit SQLite recovery authority that safely validates retained WAL/journal state without deleting, recreating, overwriting, or silently checkpointing untrusted evidence.
- Pass all six real termination/restart boundaries against the durable SQLite journal.
- Add the unfinished real multi-request and hard-link aggregate/deduplication recovery matrix.
- Complete the restarted Scope-to-Results-to-explicit-revalidation automated workflow case.
- Prove every restart performs no cleanup replay and no automatic filesystem probe, never converts uncertainty to success, preserves every durable requested row, and produces stable repeated records.

### Guardrails

- This work must not add automatic cleanup retry, permanent deletion, a production helper process, or environment-variable crash behavior to the product.
- Test fixtures use disposable files and the Windows Recycle Bin only.
- A retained-journal or database failure must remain visible and fail closed; recovery must never invent success or mutate a source photo.

### Completion criteria

The six-boundary harness and deferred matrices pass reproducibly in the approved Windows environment, their evidence matches the code actually exercised, and an independent review confirms that test hooks cannot affect production behavior.

### MVP disposition

The product-facing safety behavior was manually accepted by the user on 2026-09-14: restart showed the unknown status, performed no automatic retry, required explicit revalidation, and displayed the correct final text. Product accepted the retained-sidecar hard-crash limitation for the MVP and placed its recovery work in this post-MVP ticket; PM-001 does not reopen E1-S6 or Epic 1.

The callback/retained-sidecar experiment remains available as the explicit, opt-in `eng/verification/Invoke-E1S6CrashHarness.ps1` diagnostic. It is intentionally outside the standard Core, Architecture, and Full product gates, which do not launch it. This quarantine does not mean that PM-001 passed, was successfully skipped, or was repaired; the two recorded failing attempts and the user's manual safety verification remain the accepted evidence.

## PM-002 — Verify the E2-S1 startup-failure UI on a stable runnable shell

**Status:** Partially verified — visual safety accepted for the recruiter-demo V1;
Narrator criterion remains open under PM-005

**Priority:** Pre-release verification

**Origin:** E2-S1 product-owner verification waiver, 2026-09-14

### Problem

The final E2-S1 candidate moves the local-data failure message to one phase-independent accessible header and composes four unsafe SQLite startup fixtures through the real application shell with a counting Recycle Bin executor. Whole-branch static review found no implementation defect, but the corrected candidate did not receive a successful post-fix executable UI run. Earlier Full/Core runs repeatedly stopped on an unrelated established WPF keyboard-focus timing assertion, and the one later Architecture request ended without a test result after the Hyper-V socket/runspace failed.

The product owner explicitly waived hands-on visual verification so E2-S2 could proceed. This is an accepted verification gap, not a passing test and not evidence that a product defect exists.

### Deferred verification

- Run the current integrated executable in a stable Windows UI environment.
- For corrupt bytes, foreign version-0 contents, read-only migration, and retained SQLite sidecars, confirm startup remains in `ChooseScope` and displays exactly: `Local data could not be safely loaded. PhotoSift will not modify files until the database is available.`
- Confirm the message is exposed once through the accessible live-status surface, including Narrator, without duplicate Review/Action announcements.
- Confirm Review approval, Action entry, and the Recycle Bin executor remain unreachable for every unsafe startup fixture.
- Execute the corrected composed fixture teardown and prove it drains without a dispatcher deadlock.
- Record a clean proportionate UI/Full result against the unchanged candidate or fix only a concrete failure that verification exposes.

### Guardrails

- Do not represent the E2-S1 waiver as an automated or manual pass.
- Do not weaken database admission, remove retained sidecars, normalize corrupt evidence, or enable destructive actions to make the UI test pass.
- Do not reopen E2-S1 merely because the deferred check is pending; reopen or fix only if executable verification exposes an actual defect.

### Completion criteria

The phase-independent startup message, accessibility behavior, zero destructive execution, and composed teardown pass in a stable runnable shell. Evidence must be recorded before release-candidate acceptance.

### MVP disposition

E2-S1 was accepted complete by explicit product-owner waiver on 2026-09-14. PM-002 does not block E2-S2 or later backend stories, but it blocks final release acceptance until verified or replaced by a documented product-owner release decision.

### 2026-09-30 release disposition

The packaged `f33c63b` runtime-only/no-SDK VM run visibly verified all four
unsafe SQLite cases, the exact fail-closed warning, Choose Scope containment,
blocked destructive entry, and preserved database/sidecar bytes and attributes.
Controlled Narrator relaunch did **not** announce the warning. This is a failed
accessibility observation, not a PM-002 completion pass. The product owner
explicitly deferred accessibility beyond the recruiter-demo V1, preferably
within about one month; the missing announcement is carried into PM-005. The
`b0c4c13` candidate changed the Action Records view but not startup admission.
The earlier visual/safety evidence applies to that unchanged path with this
scope limit. The composed teardown test and a complete Narrator pass remain
PM-002 completion work; no accessibility-complete claim is permitted.
See [release verification](../evidence/e5-s4/laptop-release-verification-b0c4c13.md).

## PM-003 — Align E2-S2 Scan detail and coverage wording before release

**Status:** Done — closed by E2-S5 on 2026-09-15

**Priority:** Pre-release presentation and accessibility consistency

**Origin:** E2-S2 final review Minor observations, 2026-09-15

### Problem

E2-S2 correctly derives the six aggregate categories from durable `Enumeration`/`Exact` row truth. Two nearby presentation details do not yet express that accounting with the same precision:

- `Could Not Analyze` currently selects Exact rows whose own state is failed or canceled. An Exact `NotApplicable` row whose Enumeration prerequisite is failed or canceled contributes to the corresponding aggregate, but can be absent from the detail list.
- the complete-coverage sentence says “Read failures and exclusions are reported separately,” while the reconciled count contract and visible count label use `Unsupported`, not the older `Excluded` terminology.

These are Minor detail/wording gaps, not evidence that checkpoint rows or aggregate counts are wrong.

### Resolution

- `Could Not Analyze` includes prerequisite-derived failed, canceled, and unsupported `NotApplicable` rows with distinct labels and next steps.
- The complete-coverage sentence uses `Unsupported` terminology while preserving separate failed and canceled/unprocessed categories.
- E2-S5 focused Scenario coverage proves the summary, detail rows, next steps, and coverage wording agree.

### Guardrails

- Do not reopen E2-S2's accepted SQLite schema, checkpoint transition, persistence-ordering, or completion invariants for a presentation-only correction.
- Do not collapse Unsupported, Failed, Canceled, or Outstanding into one category, and never represent an unprocessed occurrence as analyzed.
- Do not claim the existing detail list is exhaustive until the focused verification passes.

### Completion criteria

The Scan summary, detail list, and complete-coverage wording use consistent category semantics; failed/canceled prerequisite-derived `NotApplicable` rows are presented intentionally; and focused accessible UI coverage passes before release-candidate acceptance.

### MVP disposition

PM-003 is closed and is not a release-candidate blocker. Its E2-S5 presentation correction does not turn E2-S2's unrelated WPF gate exception into a pass or a product defect. Semantic/Likely Matches remains assigned to V1.2 by the approved YAGNI roadmap.

## PM-004 — Evaluate optional local-data lifecycle features from shipped V1 evidence

**Status:** Backlog — evaluation list, not an implementation commitment

**Priority:** Post-V1 product discovery

**Origin:** E2-S5 YAGNI closure, 2026-09-15

### Optional future product candidates

Evaluate these only from evidence gathered from the shipped V1 product; their presence here promises neither implementation nor a release:

- backup/export;
- time-based retention;
- automatic expiry or background cleanup;
- per-cache-class dashboards;
- clear controls for additional data classes, only after those classes actually ship;
- model-asset management; and
- Recycle Bin contents management.

### Intentionally absent E2-S5 mechanisms

These omissions are design constraints of the focused E2-S5 implementation, not missing infrastructure to prebuild:

- no filesystem or photo adapter in the Scan-data clear path;
- no new lifecycle state or lifecycle state machine;
- no timers or retry engine;
- no speculative tables or schema expansion; and
- no redundant or experimental focus workarounds.

### Evaluation rule

Any later feature begins with a fresh product/design review based on shipped V1 evidence. It must justify its own scope and architecture rather than treating E2-S5 or this backlog entry as prior implementation authorization.

## PM-005 — Complete accessibility hardening after the recruiter-demo V1

**Status:** Backlog — accepted recruiter-demo V1 limitation

**Priority:** Post-MVP accessibility hardening

**Origin:** E5-S1 explicit product-owner scope waiver, 2026-09-20

### Problem

E5-S1 has strong deterministic WPF/UIA coverage, fresh NIC-free Core/Full
evidence, and a focused Narrator pass for the corrected Near/Review admission
path. The product owner closed the story for the bounded recruiter-facing MVP
without representing the remaining manual accessibility matrix as complete.
This is an accepted scope limitation, not a manual-tool pass and not evidence
of accessibility compliance.

### Deferred accessibility verification

- Resolve the missing Narrator announcement for Scan completion, status, and
  discovered/analyzed/unsupported/failed/canceled/outstanding counts.
- Resolve the missing Narrator announcement of the local-data fail-closed
  startup warning observed in PM-002; verify it is spoken once without
  duplicate Review/Action announcements.
- Complete the Narrator keyboard walkthrough through Action Records and both
  Local Data Control confirmation branches.
- Run Accessibility Insights FastPass across the shipped workflow.
- Run Windows SDK Inspect/UIA property checks for names, roles, values/states,
  actions, position/set-size, focus, and live regions.
- Record representative light, dark, high-contrast, reduced-animation, and
  200% text observations.
- Manually verify the current phase-independent failed-open Review announcement,
  including its same-Scan retention and new-Scan clearing behavior.

### Guardrails

- Preserve all existing source-file safety, survivor protection, explicit
  approval, Recycle Bin-only execution, truthful status, and keyboard behavior.
- Do not add a custom narration or accessibility framework unless measured
  evidence demonstrates that built-in WPF/UIA semantics cannot satisfy a
  concrete requirement.
- Do not describe E5-S1, V1, or the application as accessibility-complete or
  accessibility-compliant until this ticket closes.
- Keep every unavailable, partial, failed, and passed observation distinct; do
  not infer a result from automated coverage or a different manual path.

### Completion criteria

Every deferred item above has reproducible evidence against one identified
candidate; concrete defects are fixed without weakening safety or keyboard
behavior; the complete manual record distinguishes PASS, failure, and
not-performed states; and an independent review confirms the resulting
accessibility-complete claim is supported.

### MVP disposition

PM-005 does not block the recruiter-demo V1 or E5-S2 through E5-S5. E5-S5 must
record it as an accepted limitation in the release decision. PM-005 blocks any
accessibility-complete or accessibility-compliance claim until its completion
criteria are met.

## PM-006 — Make Review content reachable at default window size

**Status:** Backlog — observed usability limitation on the V1 candidate

**Priority:** Post-V1 layout repair; revisit sooner if a supported display
cannot reach explicit Approval even after resizing

**Origin:** Owner observation during `b0c4c13` packaged manual verification,
2026-09-30

At the default PhotoSift window size, some Review content was clipped and
could not be reached by scrolling. Maximizing the window and scrolling inside
the lower Approval pane exposed the control; both Near and Exact packaged
actions completed safely. Make Comparison, selection controls, survivor
disclosure, and Approval reachable at the default 960×720 window and common
DPI settings without requiring maximize. Preserve explicit approval and
virtualized list behavior. Verify in the actual WPF app at multiple window
sizes, not only through layout unit tests.

## PM-007 — Support Windows dark mode

**Status:** Backlog

**Priority:** Post-V1 presentation

**Origin:** Owner observation on a dark-mode Windows 11 PC, 2026-10-16

`App.xaml` used `ThemeMode="System"`, so the Fluent controls turned dark under
Windows dark mode while `MainWindow` and the dialogs kept painting surfaces and
text with `SystemColors` brushes, which stay light outside High Contrast. The
Workflow tab showed white text and dark-translucent buttons on white panels, so
the cards looked empty. The V1 fix pins `ThemeMode="Light"`. Dark mode needs
the windows to take their surface, text and border colors from the Fluent theme
resources (the dark tokens in the UX `DESIGN.md`) while keeping the High
Contrast behavior that the `SystemColors` assertions cover today. Verify in the
real app under light, dark and High Contrast, not only in tests that build
`MainWindow` without the `App` theme.
