# Task 4 Report — Recovery Coordinator and Startup Wiring

## Status

Implemented and verified in the Golden VM Core stage. No host build, test, or application launch was used.

## Implementation

- Added `IActionRecoveryCoordinator` and the required immutable recovery record/snapshot contracts.
- Added an `ActionOwnerInbox`-owned historical recovery coordinator that:
  - loads durable Action records from `IActionRecordStore` without invoking the filesystem probe;
  - projects requested, executing, recycled, not-removed, unknown, and confirmed-present row evidence through `ActionRecordPolicy`;
  - admits explicit revalidation for unresolved requested, executing, and unknown rows only;
  - skips terminal recycled/not-removed rows and rows already durably confirmed present;
  - processes all unresolved rows in the selected request serially, with at most one Windows probe outstanding;
  - runs load, probe, and append awaits outside owner turns;
  - publishes resolved state only after a persisted or exact-retry append result;
  - retains unknown state on probe failure, cancellation, timeout, or store failure;
  - uses an attempt generation to reject late probe results;
  - keeps `ProbeOutstanding` true after timeout until the old Windows call actually settles;
  - uses an injectable deadline duration/clock boundary, with a production default of 30 seconds;
  - stops new admission and publication at disposal without awaiting stuck database or Windows I/O.
- Kept historical recovery separate from live `ActionCoordinator`; the recovery coordinator has no dependency on `IRecycleBinExecutor`, Review authority, or cleanup admission.
- Added `ActionRecovery` to `PhotoSiftSnapshot` and `RevalidateUnknownAsync(Guid, CancellationToken)` to `IPhotoSiftWorkflow`.
- Wired `PhotoSiftWorkflow` to start the coordinator's SQLite-only load after construction, coalesce immutable recovery publications onto the shell owner, and forward only the explicit revalidation command.
- Wired `CompositionRoot` with `stores.ActionRecords`, `WindowsActionRevalidationProbe`, recovery ownership, and disposal before the shared SQLite owner.
- Preserved the accepted five-argument `PhotoSiftWorkflow` constructor and four-argument owned-workflow lifetime constructor used by E1-S5 regression/scenario coverage.
- Updated the three existing scenario workflow doubles mechanically for the new abstract workflow command.

## TDD Evidence

### RED 1 — coordinator absent

- VM run: `20260914-102405-core-3c07635e`
- Expected guest compiler failure:
  - `ActionRecoveryCoordinatorTests.cs(194,18): error CS0246`
  - `ActionRecoveryCoordinator` did not exist.

### RED 2 — coordinator race behavior

- VM run: `20260914-102906-core-b8872d8c`
- Named guest assertion failures:
  - `DeadlinePublishesUnknownAndLateCompletionIsIgnoredUntilCallSettles`
  - `DisposeCompletesWithoutWaitingForStuckProbeAndStopsLatePublication`
- These established that late settlement is distinct from command timeout completion and that disposal must gate persistence before publication.

### RED 3 — shell API and startup wiring absent

- VM run: `20260914-103301-core-9b99a0f0`
- Expected guest compiler failures for missing recovery constructor parameter, snapshot property, and explicit workflow command.

### Regression REDs

- VM run `20260914-103441-core-d3d4d9da` caught removal of the accepted five-parameter workflow constructor through five named E1-S5 tests.
- VM run `20260914-103742-core-73e7552a` caught removal of the accepted four-parameter owned-workflow constructor through `CloseSettlesActionThenReviewThenSharedStoresWhileDispatcherCanStillAcceptWork`.
- Both compatibility contracts were restored with delegating overloads.

### RED 4 — disposal/deadline crossing

- VM run: `20260914-104711-core-ad2776b6`
- Named guest assertion failure: `DisposeBeforeDeadlineStopsTimeoutPersistence`.
- Expiration is now an owner-admitted transition; disposal invalidation prevents a later timeout append.

## Verification

Final fresh GREEN:

- VM run: `20260914-105140-core-1570c735`
- Core relay exit: `0`
- Commands: `12`
- Domain: 203 passed
- Application: 477 passed
- Analysis: 22 passed
- Scenarios: 174 passed
- Total reported tests: 876 passed, 0 failed, 0 skipped
- `git diff --check`: clean

## Self-review

- Confirmed no `IRecycleBinExecutor`, `IApprovedActionAuthority`, or `IReviewCoordinator` reference exists in the recovery coordinator.
- Confirmed startup calls only `LoadAsync`; the probe is reachable only through `RevalidateUnknownAsync`.
- Confirmed `ActionRecordPolicy` consumes every requested item and only durable recycled rows contribute recovered bytes.
- Confirmed terminal and confirmed-present rows cannot be admitted for probing.
- Confirmed the timeout fact is appended before its unknown projection and a confirmed-present fact is appended before resolved projection.
- Confirmed a timed-out probe cannot be replaced until its actual late settlement, and the late result is neither appended nor published.
- Confirmed disposal clears queued publications and returns after its owner turn without joining probe/load/append tasks.

## Concerns

None. Task 5 still owns user-facing Action Record rendering and the full restart/crash exit harness; those are intentionally outside Task 4.
