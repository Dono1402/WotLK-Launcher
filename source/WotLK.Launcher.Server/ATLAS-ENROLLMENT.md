# Atlas enrollment for an existing WoW account

## Identity boundary

- `account` is the AzerothCore/WoW identity store.
- `atlas_launcher_profile` is the authoritative Atlas Launcher identity store.
- Login never creates an Atlas profile, modern credential, session, friendship, or avatar.
- Enrollment is an explicit operation and never creates a second `account` row.

The known Playerbots accounts currently use the `rndbot` username prefix. AzerothCore
does not expose a separate durable Atlas-user category on `account`, so enrollment has
a server-side denylist for this known technical prefix. This is defense in depth only:
the existence of `atlas_launcher_profile`, not the username, remains the source of truth
for Atlas identity and social features. Public errors never disclose this classification.

## Credential representation

Existing AzerothCore credentials are stored on `account` as a 32-byte salt and a
32-byte SRP6 verifier. `SrpCredentials.VerifyLegacy` validates them with the existing
AzerothCore-compatible SHA-1 calculation over the upper-cased `USERNAME:PASSWORD`
identity and the legacy SRP group.

Modern Atlas/Hermes credentials are stored in `hermes_bnet_credentials` with
`srp_version = 2`, a 32-byte salt and a variable-length verifier. Their verifier uses
the existing `SrpCredentials.MakeModern`/`VerifyModern` implementation: upper-cased
username SHA-256, PBKDF2-HMAC-SHA512 with 15,000 iterations, then the Hermes SRP group.

Password validation always follows the existing rule: use the modern credential when
present, otherwise validate the AzerothCore legacy credential. Enrollment does not add
another cryptographic path. When a legacy account has no modern credential, the modern
verifier is derived from the already validated current password inside the enrollment
transaction.

## Transaction

`POST /api/v1/auth/enroll-existing` receives `username`, `currentPassword`, and `email`.
The device name remains in the existing `X-Atlas-Device` header. The server:

1. locks the existing account row;
2. validates the current credential;
3. confirms that no Atlas profile exists and that the account is eligible;
4. validates e-mail uniqueness;
5. creates the modern credential only when absent;
6. creates `atlas_launcher_profile` and the initial Atlas session;
7. commits once.

Any failure before commit rolls back all Atlas additions. E-mail verification dispatch
is best-effort after the committed transaction, matching normal Atlas registration.
No schema change is required; migration `0004` remains immutable and no `0005` is added.
