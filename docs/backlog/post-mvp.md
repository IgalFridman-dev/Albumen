# Albumen Post-MVP Engineering Backlog

## PM-001 — Harden E1-S6 real-process crash-boundary automation

**Status:** Backlog

**Priority:** Post-MVP reliability/testing

**Origin:** E1-S6 manual acceptance, 2026-09-14

### Problem

The E1-S6 real-process harness reaches five of the six required durability boundaries, but the `during-shell-callbacks` case does not recover successfully in the Golden Hyper-V VM. After the child is forcibly terminated from the real Windows Recycle Bin callback, a fresh SQLite recovery process reports `ActionRecoveryStatus.Faulted` instead of `Ready`.

The failure repeated in Architecture runs `20260914-121019-architecture-b47aabb3` and `20260914-121219-architecture-b675498d`; the second run included one 500 ms Windows crash-handoff delay. Work stopped after the approved two-attempt limit. No third timing adjustment was attempted.

Read-only follow-up identified the direct fail-closed boundary: `SqliteDatabaseOwner.RefuseRetainedSidecars()` rejects any retained SQLite `-wal`, `-shm`, or `-journal` file before opening the database. A hard process kill may legitimately leave WAL/SHM state, so elapsed time alone does not make this state admissible. The current behavior protects evidence and source photos by refusing startup mutation, but it can prevent Action Records from loading after this exact hard-crash edge case.

Owner observation, 2026-10-16: on a fresh Windows install the restored `%LOCALAPPDATA%\Albumen` folder held a
0-byte `albumen.db-wal` and a `albumen.db-shm` dated 2026-10-06. Every launch showed "Local data could not be
safely loaded" and the app offered no in-app way out. The Hyper-V VM reproduced it on clean develop `9a78e3b`: one launch
and a normal close (exit code 0) left an empty `-wal` and a `-shm`. Shutdown tracing showed every dispose step
completing, no open statements, and the writer closing last, yet the close did not remove the sidecars. An explicit
`PRAGMA wal_checkpoint(TRUNCATE)` before the writer closes made the close remove them, so `SqliteDatabaseOwner` now
does that. Why the implicit close-time checkpoint did not run is not established. A hard kill can still leave
sidecars, so the explicit recovery authority above is still needed.

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
- For corrupt bytes, foreign version-0 contents, read-only migration, and retained SQLite sidecars, confirm startup remains in `ChooseScope` and displays exactly: `Local data could not be safely loaded. Albumen will not modify files until the database is available.`
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

**Status:** Backlog — accepted recruiter-demo V1 limitation. Scheduled for Albumen 1.2 (the first
public release) by the [1.1 release plan](../superpowers/specs/2026-10-19-albumen-1.1-siglip2-release-plan.md);
E4-S5 covers only the new semantic states.

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

**Status:** Fixed in PR #10. Review and the Recycle Bin progress page scroll as
one surface with bounded, still-virtualized lists. Owner verified it in the real
app on 2026-10-17 at the default window size; the VM UI smoke and Scenarios
tests cover 960×720 and 680×520. The same PR carries owner-requested V1 polish
outside any SPEC (compact header, numbered step rail, wrapping list labels,
Scan summary expander, larger preview, Narrator-announced local-data warning);
the Narrator announcement itself has not been verified by listening (PM-005).

**Priority:** Post-V1 layout repair; revisit sooner if a supported display
cannot reach explicit Approval even after resizing

**Origin:** Owner observation during `b0c4c13` packaged manual verification,
2026-09-30

At the default Albumen window size, some Review content was clipped and
could not be reached by scrolling. Maximizing the window and scrolling inside
the lower Approval pane exposed the control; both Near and Exact packaged
actions completed safely. Make Comparison, selection controls, survivor
disclosure, and Approval reachable at the default 960×720 window and common
DPI settings without requiring maximize. Preserve explicit approval and
virtualized list behavior. Verify in the actual WPF app at multiple window
sizes, not only through layout unit tests.

## PM-007 — Support Windows dark mode

**Status:** Backlog — scheduled for Albumen 1.2 by the
[1.1 release plan](../superpowers/specs/2026-10-19-albumen-1.1-siglip2-release-plan.md).

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

## PM-008 — Ship V1 as a self-contained release

**Status:** In progress — design approved 2026-10-17; release built and clean-launch
checked on 2026-10-17 at `8562f97`; inventory owner-approved 2026-10-17; the published
ZIP was rebuilt offline in the Golden VM from the sealed cache at `e1d6a9d` and passed the
clean launch on 2026-10-17. Open: the demo-photos ZIP. Evidence: `docs/evidence/pm-008/`.

**Priority:** Before the public V1 release

**Origin:** Owner decision during GitHub polish, 2026-10-17

The V1 candidate requires the .NET 10 Desktop Runtime to be installed first, which is a
barrier for someone trying the app from a GitHub release. Bundle the pinned 10.0.11 runtime
and the WPF part of the Windows Desktop runtime in the release folder so the user only
extracts the ZIP and runs `Start-Albumen.cmd`. Design:
`docs/superpowers/specs/2026-10-17-pm-008-self-contained-release-design.md`.

### Completion criteria

- Tooling tests and the release dry run pass on GitHub CI.
- One Golden VM capture on the owner's PC: identical publishes and the closure check pass.
- The owner approves the new redistribution inventory.
- Acceptance on a runtime-free Windows 11 VM with no network: launch, an Exact and a Near
  review, one Recycle Bin move and restore, and the tamper check still rejects a changed file.
- A small demo-photos ZIP ships next to the app ZIP.


## PM-009 — Make the UI understandable and attractive

**Status:** Done 2026-10-19 — all code merged into develop (PRs #17, #19, #22, #24 and the
follow-ups PM-010 to PM-016, last code merge `e6b6a74`), CI green, Golden VM Full passed on
`e6b6a74` (run `20261019-161036-full-8896cdac`, 1,888/1,888), owner's High Contrast
click-through passed. The README screenshots moved to the v1.0.0 release work. Evidence:
`docs/evidence/pm-009/verification.md`.

**Priority:** Post-V1 presentation

**Origin:** Owner feedback from testing V1 with real photos, 2026-10-17

V1 works but reads like an engineering tool: internal terms on screen, one photo at a time in
Review, and no visual hierarchy. Restyle the app and rebuild Review around thumbnails and
side-by-side comparison without changing the safety design. Design:
`docs/superpowers/specs/2026-10-17-pm-009-ui-overhaul-design.md`; plan:
`docs/superpowers/plans/2026-10-17-pm-009-ui-overhaul.md`.

### Completion criteria

- All three PRs merged with GitHub CI green.
- Golden VM Full run passes on the final commit.
- Owner click-through on Windows 11 with real photos, including High Contrast.
- README screenshots updated.

## PM-010 — Show look-alike photos as one set instead of many pairs

**Status:** Done in PR #20 (merged 2026-10-17); Golden VM Full passed on `e6b6a74` with PM-009.
The owner's real-burst check is not recorded on its own.

**Priority:** Before V1.0.0 (owner decision 2026-10-17 to ship V1.0.0 with the new UI)

**Origin:** Owner testing with real photos, 2026-10-17

Several shots of one moment appear as many two-photo groups because every Uncertain relationship
is published as its own pair. Design:
`docs/superpowers/specs/2026-10-17-pm-010-look-alike-sets-design.md`.

### Completion criteria

- Application tests for sets, Near-only groups, loose chains and determinism pass on GitHub CI.
- Owner checks a real burst folder and confirms one moment shows as one set (or a set plus pairs).
- Golden VM Full run before release.

## PM-011 — Treat truncated JPEGs as "couldn't be checked", not as look-alikes

**Status:** Done in PR #23 (merged 2026-10-17); Golden VM Full passed on `e6b6a74` with PM-009.
Found in the Hyper-V Review pass of PR #22.

**Priority:** Before V1.0.0 (owner decision)

**Origin:** VM fixture "Broken photo.jpg" (the first 2 KB of a 1200 × 900 JPEG)

WIC decodes the truncated file without an error and fills the missing area with gray. The
partial image gets a signature, is related (Uncertain) to unrelated photos, and joined a set of
three resized copies of another photo, turning what would have been a clean Near set into an
Uncertain set of four. Only a file with non-image bytes was reported as "couldn't be checked".

### Completion criteria

- Analysis detects a JPEG that ends before its image data is complete and reports it as
  Could Not Analyze with a plain reason, so it never gets a signature or joins a set.
- A test with a truncated JPEG fixture passes on GitHub CI; Golden VM Full run before release.

### Fix

The preview decoder walks a JPEG's segments and compressed data before decoding and reports
"the photo file is incomplete" (Could Not Analyze) when the file ends before its end-of-image
marker. Data after that marker, such as a motion-photo video, is accepted. Accepted
limitation: a JPEG whose image data is complete but whose end marker was never written is also
reported, because it cannot be told apart from a file cut right after its last scan.

## PM-012 — Show a marked photo as marked in every set it belongs to

**Status:** Done in PR #25 (merged 2026-10-18); Golden VM Full passed on `e6b6a74` with PM-009.

**Priority:** Before V1.0.0 (owner decision 2026-10-18)

**Origin:** Owner test of PR #24 on the desktop with real photos, 2026-10-18

A photo can sit in a Look-alike set and in a leftover Maybe similar pair. Marking it in one
place already counts everywhere (the second mark is refused and every set keeps a survivor),
but the other sets still show it as "Kept". The user sees no reason to open a pair whose other
photo is the only one that can stay.

### Completion criteria

- A member marked in another set reads "Marked for removal in Set N" everywhere it appears,
  can't be marked again there, and Compare still works.
- A set whose remaining photos can't change (every other member is marked elsewhere) shows
  that on its card and in the set list.
- Scenario tests for both; no change to the survivor rule or the approval.

## PM-013 — Keep reviewing after a cleanup without scanning again

**Status:** Done in PR #26 (merged 2026-10-18); Golden VM Full passed on `e6b6a74` with PM-009.
Marks on photos that were not removed are not carried over; a set touched by an uncertain
outcome is closed until the next scan; after a faulted cleanup only "Scan again" is offered.

**Priority:** Before V1.0.0 (owner decision 2026-10-18)

**Origin:** Owner test of PR #24 on the desktop with real photos, 2026-10-18

Approving a cleanup seals the review, so removing more photos needs a new scan. The owner wants
to go back to the sets after a cleanup, with the removed photos gone and any set that lost a
photo dropped, and approve another cleanup. Design:
`docs/superpowers/specs/2026-10-18-post-test-fixes-design.md`.

### Completion criteria

- After a cleanup ends, "Continue reviewing" reopens Review on the same scan. Removed photos
  are checked on disk and shown as gone; a set with one photo left disappears from the lists.
- Each further cleanup is a fresh sealed approval with a fresh check of every file, its own
  journal rows, and at least one survivor per set, exactly like the first.
- Application tests for the reopen, the dropped sets and a second approval; Golden VM Full run
  before release.

## PM-014 — Re-check the folder after a cleanup removed photos

**Status:** Done in PR #25 (merged 2026-10-18); Golden VM Full passed on `e6b6a74` with PM-009.

**Priority:** Before V1.0.0 (owner decision 2026-10-18)

**Origin:** Owner test of PR #24 on the desktop with real photos, 2026-10-18

The folder page keeps the counts from the check that ran when the folder was chosen. After a
cleanup removed photos, going back to the folder page still shows the old counts.

### Completion criteria

- Going to the folder page after a cleanup that removed at least one photo from the current
  folder re-checks the folder by itself; the page says why while it checks.
- Application test for the automatic re-check and for the case where nothing was removed.

## PM-015 — The folder page gets the prototype's simplicity

**Status:** Done in PR #27 (merged 2026-10-18; CI green, reviewer Ship, seen by the owner on
the desktop: "Otherwise good work"). Golden VM Full passed on `e6b6a74` with PM-009.

**Priority:** Before V1.0.0 (owner decision 2026-10-18)

**Origin:** Owner test of PR #24 on the desktop with real photos, 2026-10-18: "You showed me a
prototype like this, but end product looks different." and "We can keep the side bar being a
side bar, but I do like the simple design of the prototype." Design:
`docs/superpowers/specs/2026-10-18-post-test-fixes-design.md`.

### Completion criteria

- The folder page has the prototype's layout: one hero (illustration drawn in XAML, headline,
  one line), one card with the folder's name and path, one status strip that says one sentence
  per state, the two switches with captions, the check summary under "Details", the Scan
  button in the card's footer, and three trust lines under the card. The step rail stays.
- Pinned automation names and the smoke marker `phases=5` are unchanged.
- WPF scenario tests for the strip text per state, the captions, the trust lines and the
  Scan button inside the card.

## PM-016 — Category navigation on the Review page skips the middle categories at the default window size

**Status:** Done in PR #28 (merged 2026-10-19); Golden VM Full passed on `e6b6a74` with PM-009.
Cause: the Review lists scroll by item (kept so a
10,000-photo set never realizes every card), and a wheel notch moved three items, so a list
of four categories that shows two or three jumped from the first to the last. The wheel now
moves one card per notch over every list that scrolls by item (the three Review lists and the
Cleanup list); touchpad deltas smaller than a notch add up to one. At a list's end the wheel
still goes to the page.

**Priority:** Before V1.0.0 (owner decision 2026-10-18)

**Origin:** Owner test of the PM-015 build (commit 29d416e) on the desktop, 2026-10-18: on the
Review page, at the normal (not maximized) window size, moving between the four categories
(Identical copies, Look-alikes, Maybe similar, Couldn't check) jumps from the top one to the
bottom one and skips the two in the middle; with the window enlarged it works fine.

### Completion criteria

- Every category is reachable in order by mouse wheel at the default window size (960×720).
  Keyboard navigation is untouched by reasoning, not by a test: the handler is wheel-only and
  the arrow keys go through a separate key handler.
- WPF scenario tests at the default window size with large text (font size 24, so the four cards
  overflow the list by two): one notch moves one card, three touchpad deltas of 40 move one
  card, and a notch at the list's end moves the page.

## PM-017 — Albumen 1.1: SigLIP 2 look-alikes, text search and blur

**Status:** Approved 2026-10-19; stories ready for dev after the rename (PR #33). Plan:
`docs/superpowers/specs/2026-10-19-albumen-1.1-siglip2-release-plan.md`.

**Priority:** The next release after 1.0.0

**Origin:** Owner decision in the project thread "V1.0.1 scope and blur detection", 2026-10-19

1.0.0 is the recruiter and beta build. The owner wants the app to be genuinely usable before it goes
public, which means look-alike grouping that survives a moving subject, text search over the scan,
and blur detection. One on-device SigLIP 2 model serves grouping and search; blur stays a classical
pixel measure. Accessibility (PM-005, PM-002) and dark mode (PM-007) follow in 1.2, the first public
release. Standing owner instruction for 1.1: YAGNI, clear and concise code, only what this release
needs.

### Stories and order

E4-S1 spike → E3-S6 corpus → E4-S2 profile and packaging → E4-S3 index → E4-S6 look-alike sets and
E4-S4 text search → E4-S5 acceptance and release. E3-S3 blur runs in parallel. E2-S4 is deferred to
1.2 unless decision D2 (measured embedding cost) pulls an embeddings-only version in.

### Completion criteria

- Each story closed through the story workflow with Golden VM evidence.
- The 1.1.0 ZIP rebuilt offline from the sealed cache launches on the runtime-free VM with no network
  and completes an Exact, a Look-alike and a text-search review.
- README states measured model size, per-photo CPU time and corpus precision/recall with limitations;
  PM-005 remains recorded as open.

## PM-018 — Make the scan close-ordering test deterministic

**Status:** Backlog

**Priority:** Albumen 1.1 test robustness (before the 1.1 Golden VM Full run)

**Origin:** GitHub CI on two docs-only PRs, 2026-10-19

`ScanCheckpointCoordinatorTests.CallbackArrivingDuringTerminalPersistenceCannotOverwriteTerminalTruth`
(`endEnumeration: False` case, `tests/Albumen.Application.Tests/Scan/ScanCheckpointCoordinatorTests.cs`)
timed out after 10 s in `PumpNext` on the first `Build and test (windows-latest)` run of
[#31](https://github.com/IgalFridman-dev/PhotoSift-dev/pull/31) (`550924a`) and of
[#35](https://github.com/IgalFridman-dev/PhotoSift-dev/pull/35); both changed only docs and both
passed on one re-run. The test passed on the `e6b6a74` develop push and in the Golden VM Full run.

The test branches on whether a late registration has already completed when the terminal write is
released (`admissionClosedBeforeCommit = late.IsCompleted`). The timeout happens in the branch that
waits for the late registration's checkpoint write, so on a slow runner the late callback likely
reaches neither path in time. Not established whether the race is in the test's synchronisation or
in scan finalisation itself.

### Completion criteria

- Root cause stated: test synchronisation or product ordering.
- The test decides its branch from an explicit signal, not from timing, and still proves that a
  callback arriving during terminal persistence cannot overwrite the terminal state.
- If the cause is in finalisation, the fix keeps the terminal-truth invariant and gets its own test.
- No retry, longer timeout or skip as the fix.
