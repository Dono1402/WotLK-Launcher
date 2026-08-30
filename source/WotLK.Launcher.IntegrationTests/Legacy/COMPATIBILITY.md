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

## Local shell baseline (02B)

- `OpenGameFolderButton_Click` first saves the current UI fields. That save
  normalizes `InstallPath`, persists the settings, creates the configured
  directory when it is absent, then starts that path with shell execution.
- `OpenLogsButton_Click` creates the legacy settings directory, writes the
  current in-memory log box to `launcher.log` when the file is absent, then
  starts Explorer with `/select,"<log path>"`.
- Neither legacy handler catches a shell, path, access or creation error, and
  neither displays a dedicated error message for an absent target.
- The V2 checkpoint intentionally does not redirect these handlers. Its local
  actions use the same configured paths but never create, normalize, persist or
  repair them. This preserves the v1.1.0 path behavior while the legacy window
  remains the default.

## Client verification extraction (02C)

- `LoadManifestAsync` now delegates to `GameManifestClient`; the authorized
  `HttpClient`, URL, JSON options and HTTP failure behavior are unchanged.
- `Load/SaveInstalledManifestHistory` now delegate to
  `InstalledManifestStore`, retaining `client-manifest-cache.json`, UTF-8
  without BOM, the same ignored read failures and the existing write check.
- `FindMissingOrChanged*`, `CompareManifestFiles` and the SHA-256 loop now live
  in `GameFileVerifier`. `MainWindow` keeps thin wrappers for the unchanged
  install/update pipeline.
- The fast cache deliberately still compares remote metadata with cached
  metadata without proving that each local file exists. The installed-version
  shortcut also remains. Exhaustive manual rehashing belongs to 02C.1.
- `GameVerificationCoordinator` is only a non-queuing, single-flight guard for
  verification. It has no user cancellation API and uses only the runtime
  lifetime token. Its compatibility role is replaced by the global operation
  lease introduced atomically in 02D.0; it must not be expanded to addons,
  downloads or launcher auto-update.

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
