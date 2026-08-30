# Legacy compatibility baseline (02A.0)

This matrix documents the behavior of the v1.1.0 window before runtime extraction.
It is a characterization baseline, not the target coordinator design.

| Interaction | v1.1.0 baseline characterized in 02A.0 | Target checkpoint |
| --- | --- | --- |
| Play / Play | No dedicated single-flight guard. A second click can enter `PlayGameAsync` before the first launch finishes. | 02F.3 adds the independent Play single-flight lock. |
| Play / Verify | Allowed while the local client is playable. Verify does not put the window in global busy mode. | Preserved, with immutable snapshots. |
| Play / Install or Update | The home Play button is disabled while the shared download cancellation source is active. The main button becomes Cancel. | 02D.0 makes refusal immediate and explicit. |
| Play / Addons | Addon work uses the same global cancellation source and disables Play through `SetBusy`. | 02D.0 plus the Play lock formalize the rule. |
| Play / launcher auto-update | A manual launcher update disables Play through `SetBusy`; the periodic check itself is read-only. | 02H.2 shares the global coordinator for mutation. |
| Verify / Install or Update | The verify handler refuses while the global cancellation source exists. A verification already in flight does not yet reserve a maintenance lease. | 02C and 02D.0 forbid mutation while Verify is active. |
| Verify / Addons | Addons navigation remains enabled during an in-flight verification in v1.1.0. | 02D.0 closes this known legacy race. |
| Verify / launcher auto-update | No explicit compatibility contract exists beyond current control state. | 02D.0 defines immediate `Busy` refusal. |
| Install or Update / Addons / launcher auto-update | Mutating UI paths share `_downloadCancellation`; current controls disable competing entry points and expose Cancel on the active path. | 02D.0 replaces this atomically with operation leases. |

`TryBegin`, dual cancellation semantics, update knowledge (`Unknown`, `Checking`,
`Known`, `Unavailable`), immutable snapshots and event coalescing are deliberately
not implemented in 02A.0. Their clarified contracts apply to the later checkpoints.

## Locked contracts for later checkpoints

- `TryBegin` returns immediately with `Started`, `Busy`, `ShuttingDown` or
  `RejectedByCompatibility`. It never queues a user command. Any internal
  `WaitForIdleAsync` API remains separate from UI command handling.
- An operation lease distinguishes user cancellation (`CanUserCancel` and
  `CancelFromUser`) from lifecycle cancellation (`CancelForShutdown`). Shutdown
  can stop a verification even when the user cannot cancel it.
- `GameAction.Play` only means that the local client is playable. Update
  knowledge remains orthogonal: `Unknown`, `Checking`, `Known` or `Unavailable`.
  Only a successful manifest comparison may display "up to date".
- Play uses an independent single-flight lock. It may coexist with read-only
  Verify when the local client is playable. Mutating game, addon or launcher
  maintenance is rejected immediately while Play or Verify is active.
- `GameStateAdapter` publishes one immutable `Current` state, or updates all
  backing fields before one grouped notification. Progress may be coalesced at
  75-100 ms; start, phase, cancellation, error, success and final state are never
  delayed.
- Operation lease `Complete` and `Dispose` are idempotent. Only the lease whose
  `OperationId` is still current may release the coordinator; stale leases and
  stale callbacks cannot affect a newer operation.
