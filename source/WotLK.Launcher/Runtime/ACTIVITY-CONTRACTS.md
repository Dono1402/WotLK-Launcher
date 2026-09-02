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

## Legacy launcher auto-update characterization

- Startup creates one 30-second dispatcher timer. It starts only when automatic launcher
  updates are enabled. A startup check is also scheduled under that setting.
- Periodic checks acquire `LauncherAutoUpdate` with user cancellation disabled, then load
  the manifest and hash the current executable. Errors are logged and suppressed.
- A manual update acquires the same compatibility lease with user cancellation enabled.
  It downloads one executable, exposes received bytes, optional total bytes, percentage,
  speed, and ETA directly to the legacy progress controls, validates size and SHA-256,
  writes the replacement script, starts it elevated, and shuts down the application.
- The shared lease makes checks/downloads mutually exclusive with game and addon
  maintenance and incompatible with Play. A busy attempt returns immediately.
- Legacy auto-update has no shared phase snapshot or explicit terminal result today.
  04B.3 must extract one `LauncherSelfUpdateCoordinator` that reuses this flow and exposes
  check/download/validation/application phases plus the shared terminal contract. It must
  keep a single 30-second timer and must publish success before application shutdown.
