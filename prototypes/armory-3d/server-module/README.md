# Atlas Armory Collector

Deployed after subsequent explicit authorization on 2026-09-05. The initial
preparation-only restriction was lifted for this deployment and one game-server
restart. Collection is enabled for Flowmage only (`OnlyGuid = 19092`). No installed
launcher changes or launcher release are part of this module. Live collection was
subsequently authorized and deployed on 2026-09-05; see Live Deployment below.

## Contract

- The default configuration is disabled. The current pilot explicitly enables it
  and restricts collection with `OnlyGuid = 19092`.
- `OnPlayerBeforeLogout` captures non-bot players before session cleanup.
- `AtlasArmory.LiveEnable` is separately opt-in (off by default, on for the pilot).
  Per-player state in `CustomData` schedules a first capture after five seconds in
  world, then one every 60 seconds. Equipment visibility hooks only mark a change;
  `OnPlayerAfterUpdate` captures once the change has settled for two seconds, with
  at least five seconds between live captures. Continuous changes still get a
  snapshot at 60 seconds. Logout always takes a final snapshot independently.
- No global player map, background access to `Player`, or synchronous SQL is used.
  Monotonic scheduling tolerates stalled updates without catching up in a burst.
  The asynchronous upsert retains the newer capture even if DB workers finish in
  reverse order. Player-owned state is destroyed with its player.
- One asynchronous MySQL upsert stores a self-contained JSON snapshot in the characters
  database. There is no synchronous database access in the player hook.
- Numbers come from the game engine, not from item-bonus sums. Spell power and critical
  chance remain separate for the six magic schools.
- Hit is a general bonus, not target-specific accuracy. Spell-specific conditional
  modifiers are not simulated. Expertise is the main-hand field. Haste is the effective
  global speed modifier, including slows. Buffs and shapeshift form remain part of the snapshot.
- Equipment slots 0 through 18 only; no bags, bank, credentials, or account identifiers.
- The database includes the private character GUID to identify the row. The local importer
  validates it but removes it from the public cache. This is not a public armory API.
- Native character saves and this asynchronous snapshot are separate transactions.
  Consumers must use the snapshot's own equipment, identity and capture time, not splice
  in newer inventory rows. The local importer accepts only `login`, `equipment`,
  `periodic`, or `logout`. A mismatch to the 3D export triggers a complete validated
  export; incompatible statistics are never applied to the old model.

## Verification

Tested against headers for Arthas candidate revision
`ee60100e422b65bedbfab649e24f2c95794c8014` without changing its source or build outputs.
The check uploads this directory to a separate temporary directory, then runs:

```sh
python3 check_compile.py /path/to/existing/candidate/build
```

This uses the candidate's existing `atlas_friends.cpp` compilation command for include
paths and flags, discards output and dependency-generation arguments, and checks both
module translation units with `-fsyntax-only`. Standalone C++ tests exercise haste and
SQL JSON construction. The latter uses only SELECT inside a READ ONLY transaction on
the existing `arthas-mysql` container. It does not execute the migration or any upsert.

Passed: two translation units, haste tests and typed JSON round trip. Node and browser
tests cover cache validation, class categories and schools. The deployment also passed
an isolated additive worldserver link, dependency checks and `worldserver --version`.
The new process is running and accepting game connections. The first real combat
snapshot was written on Flowmage's logout at `2026-09-05T07:28:32.094Z` and imported
at `07:28:47Z`. Full identity and equipped instances match the local 3D baseline.
This confirms runtime hook execution and the actual database write. A side-by-side
comparison with the in-game character sheet has not been performed.

The real record contains spell power 8, spell critical chance 6.3601127% for all six
schools, general spell hit bonus 3%, and spell haste 0%. Active talent points are
0/0/13 and form is 0. Temporary effects are included as defined by the contract.
After the import, all 27 Node tests and eight PC FR/EN viewport checks passed with
the real cache. WebGL pixels, animation, rotation, tooltips and non-blocking statistics
loading passed, as did the synthetic category tests for all ten classes and schools.

## Deployment Record

- Candidate: `/opt/arthas-next/candidates/armory-combat-20260905T070444Z`.
- Baseline: `/opt/arthas-next/candidates/dungeon-clear-8224099-20260903T062903Z`.
- Build ID: `f0e27089346ad13266ad4a8c5d2de76e34445811`.
- SHA-256: `0dd3fd0cb34ea1fbe49961230e67147b1b3de9fabc75947f16a4f699e39fac63`.
- Service: `arthas-worldserver.dungeon-clear-8224099.service`.
- Initial PID: `1388262`; start: `2026-09-05T07:17:06Z`; port 4000 ready: `07:18:06Z`.
- Backup: `/opt/arthas-next/backups/armory-combat-20260905T070444Z`.
- Override: `/etc/systemd/system/arthas-worldserver.dungeon-clear-8224099.service.d/30-atlas-armory.conf`.

This was an additive relink, not a clean full rebuild. The original module archive
was copied into an isolated overlay; only its generated loader object was replaced,
and the two new module objects were added. All six existing module registrations,
other linker inputs, runtime dependencies and pre-existing source patches were
preserved and checked. The generated loader uses the baseline CMake template.

The characters migration created only `atlas_armory_combat_snapshot`. The existing
main config received `AtlasArmory.Enable = 1` and `AtlasArmory.OnlyGuid = 19092`.
The systemd override replaces `ExecStart` and sets `TimeoutStopSec = 300`. The baseline
config, data, working directory and library RUNPATH remain in use: retain that tree.
The auth service was not restarted. No rollback was needed. Existing bot-save and
waypoint warnings were observed; this deployment does not address those features.

Import subsequent logouts with `--verified-after 2026-09-05T07:17:06Z --combat`.
An absent or incompatible snapshot must not replace the previous real native cache.

## Further Deployment

Any wider rollout requires explicit authorization. Use a new candidate build,
preserve existing modules and local patches, apply the characters migration, enable
the module for a limited pilot, then check one real logout and import before wider use.
Do not infer permission to replace the active binary from preparation/test approval.
To disable later, turn off `AtlasArmory.Enable` and reload configuration; do not drop
the table or remove unrelated modules as part of a rollback.

Sources: the matching AzerothCore `PlayerScript.h`, `WorldSession.cpp`, `Unit.cpp`,
`StatSystem.cpp` and `Player.cpp`; WotLK `PaperDollFrame.lua` for school presentation.

## Live Deployment

The user explicitly authorized installation and a server restart for this update.
The previous deployment record above is historical, not the current executable.
An intervening modules update was detected and preserved as the new baseline.

- Candidate: `/opt/arthas-next/candidates/armory-live-20260905T1310Z`.
- Baseline: `/opt/arthas-next/candidates/modules-update-20260905T1016Z`.
- Baseline source revision: `edfd79c4839c`, branch `atlas/modules-update-20260905`.
- Baseline Build ID: `0793bcdb2a6486ac94767ce4056bd162049a083d`.
- Live Build ID: `783c2392c09372f7efd31fad50373137628b06dc`.
- SHA-256: `b91d39f1d05f62d93d2ba8bcc2e009e238de2f729c5e6a1113ed608feed40589`.
- Process started: `2026-09-05T13:15:01Z`, PID `1461668`.
- Port 4000 and stable process confirmed: `2026-09-05T13:15:59Z`.
- Backup: `/opt/arthas-next/backups/armory-live-20260905T1310Z`.
- New service drop-in: `50-atlas-armory-live.conf` in the same unit's drop-in directory.

`build_candidate.py` replaces only `atlas_armory.cpp.o` in a copy of the active
module archive. The seven existing module registrations, loader, other linker
inputs and dynamic dependencies are unchanged. The baseline source/build files
were not modified. Runtime config and working directory still use the modules-update
candidate; only `AtlasArmory.LiveEnable = 1` was added to its config. Existing
`Enable = 1` and `OnlyGuid = 19092` remain. No new migration or native save changes.
The authserver was not restarted (PID `323656` before and after).

Verification: both C++ units pass syntax checks against current active headers;
standalone C++ tests cover scheduling, debounce, rate limits, stalled updates,
reconnection, haste and five READ ONLY MySQL JSON/order round trips. All 44 Node
tests pass, including live import with identity/date/equipment checks and periodic
statistics updates without repeated exports. FR/EN browser hot-swap tests pass:
nonblank animated model, old model retained while loading, atomic replacement,
camera/school/tooltip preservation and recovery after a missing resource.

At `2026-09-05T13:17:05Z`, Flowmage was still offline and the last valid capture was
the staff-removal logout at `12:49:53.678Z` (12 equipped items). Therefore live hooks
are deployed and enabled, but their first real online capture still needs a login.
The prior logout-based staff removal was confirmed successful by the user.
Existing bot pet-spell duplicate-key and waypoint errors remain unrelated to this
collector; no armory error was observed during deployment. Full-load performance
and a comparison with the in-game character sheet remain unmeasured.
