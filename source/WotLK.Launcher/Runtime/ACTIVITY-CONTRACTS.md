# Atlas operation activity contracts

## Ownership and observation

`LauncherOperationCoordinator` remains the only owner of maintenance and Play leases.
Its `CurrentActivitySnapshot` and `ActivityChanged` event expose only lease ownership:
sequence, operation id, business operation type, active state, current user-cancellation
capability, and shutdown state. Game and addon progress remain in their domain snapshots.

`LauncherOperationKind` is the existing compatibility class used by the lease arbiter.
`LauncherOperationType` is the shared business type intended for every activity consumer.
No presentation-specific operation enum is required.

`GameRuntimeSnapshot.TerminalResult` and `AddonsRuntimeSnapshot.TerminalResult` expose an
explicit immutable `OperationTerminalResult`. Consumers must use its `Outcome`; becoming
idle is never proof of success. Cancellation differentiates `User` from `Shutdown`.

The optional display context is deliberately restricted to a stable subject id, display
name, and short message. It must never contain a path, URL, token, exception, stack trace,
or full log.

## Concurrency matrix

| Active operation | Play | Verify | Game mutation | Addons | Launcher auto-update |
|---|---:|---:|---:|---:|---:|
| Play, playable client | Busy for a second Play | Allowed | Rejected | Rejected | Rejected |
| Verify, playable client | Allowed | Busy | Busy | Busy | Busy |
| Game install/update/repair | Rejected | Busy | Busy | Busy | Busy |
| Addon mutation/batch | Rejected | Busy | Busy | Busy | Busy |
| Launcher auto-update | Rejected | Busy | Busy | Busy | Busy |

Every refusal is immediate. `TryBegin` never queues a user command. Play remains a
separate single-flight lease and is excluded from activity history.

## Domain details

- Game progress stays coalesced at about 100 ms. Verify is indeterminate until genuine
  processed/total file counts exist. Install, update, verify, and repair publish explicit
  success, failure, user cancellation where supported, and shutdown cancellation.
- Addon progress stays coalesced at about 80 ms. Install, update, repair, remove, and batch
  update use precise operation types. Remove exposes `Removing`, no byte percentage, and
  `CanUserCancel=false`.
- `AddonBatchUpdate` owns one global operation id. `ActiveAddonId` is the current child and
  `PendingAddonIds` is presentation context only. Children never acquire leases. The batch
  succeeds only when every child succeeds, is cancelled on user cancellation, and fails at
  the first child error, preserving the existing stop-on-first-error behavior.
- Late progress is accepted only while both the lease identity and operation id are current.
  An old lease cannot release, mutate, or publish a terminal for a newer operation.

## Recent history boundary

04B.0 defines the limit only: ten terminal results, memory-only, discarded on process exit.
No activity coordinator, database, JSON file, or disk persistence is introduced here.

## Launcher self-update runtime

- Startup creates one 30-second dispatcher timer. It starts only when automatic launcher
  updates are enabled. A startup check is also scheduled under that setting.
- Startup, timer and manual checks are coalesced by `LauncherSelfUpdateCoordinator`. A
  simple manifest comparison takes no maintenance lease and creates no active or recent
  Activity item. `LastCheckedAt` advances only after an exploitable manifest response and
  local comparison.
- Discovering a version publishes availability only. The historical product policy does
  not automatically begin the download.
- A real download acquires the global `LauncherAutoUpdate` lease immediately or is refused;
  it is never queued behind game or addon maintenance. The updater snapshot is the sole
  source of received bytes, total bytes, percentage, speed, ETA and phase for both legacy
  controls and V2 Activity.
- Download and candidate validation remain user-cancellable. Cancellation is disabled
  before the critical handoff. `LauncherSelfUpdateCoordinator` validates the candidate,
  then delegates transaction creation and helper launch to the 04B.3a
  `ILauncherSelfUpdateFinalizer`; it owns no swap, handshake or rollback logic.
- The current launcher requests shutdown only after the finalizer has accepted the
  transaction. A helper refusal keeps the process open, releases the lease and publishes a
  controlled failed terminal result. Terminal history remains memory-only.
- If startup recovery found an interrupted transaction, automatic checks and the 30-second
  timer are suppressed for that process lifetime. An explicit manual check remains possible;
  no version is permanently blacklisted.
