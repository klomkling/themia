# Themia.Modules.Identity — tracked follow-ups (post-0.5.0)

Items deliberately deferred out of the 0.5.0 core slice. Surfaced during the PR #84 multi-agent
review; none is an active bug — they are hardening / consistency / architecture improvements.

## Hardening
- **`VerifyPasswordAsync` timing side-channel.** The `NotFound`/`Inactive`/`LockedOut` paths return
  before any argon2id work, so response latency distinguishes "user exists" from "does not exist".
  Mitigation: hash a throwaway password on the not-found path to equalize timing. Belongs with the
  **0.5.1 login endpoint** (the auth boundary), where uniform-message presentation also lives.
- **Atomic normalization on `User`/`Role`.** `UserName`/`NormalizedUserName` (and the email/name pairs)
  are independently settable public properties; only `UserService.Normalize` keeps them in sync. Make
  the `Normalized*` setters non-public and expose `SetUserName`/`SetEmail`/`SetName` that set both
  atomically (EF maps private setters; the Dapper mapping is explicit) — closes the desync invariant
  by construction without abandoning ORM-friendly entities.
- **`SetId` write-once guard.** `SetId` re-widens `Entity<TId>.Id`'s protected setter to public and can
  be called post-persistence (mutating hash-code identity). Guard with `if (!IsTransient) throw`.
- **Argon2 version-segment validation.** `Argon2idPasswordHasher` parses `v=19` but never checks it; a
  future format bump would be silently ignored. Validate it in `TryParse` or fold into `NeedsRehash`.
- **`user_tokens` growth.** Consumed/expired tokens are never pruned. Add a periodic cleanup (or filter
  consumed rows in the consume spec). Low impact at current scale.

## Type / API consistency
- **`ICurrentUser.TenantId` as `TenantId?`.** It currently exposes raw `string?`, downgrading the
  validated `TenantId` value object at the boundary application code actually injects. Return `TenantId?`
  (parse via `TenantId.From`) so the platform-vs-tenant distinction is well-typed end-to-end; keep
  `IsPlatform`. Also reconcile `IdentityCurrentUserAccessor.UserId` (string) vs `ICurrentUser.UserId`
  (Guid?) — two representations of the same value.

## Architecture
- **Peer-coupling package split.** `Themia.Modules.Identity` references **both** `Framework.Data.EFCore`
  and `Framework.Data.Dapper`, so a single-peer adopter drags in the other stack. Consider thin
  `Themia.Modules.Identity.EFCore` (model config) and `…Dapper` (mappings) satellite packages so the
  core stays peer-neutral, per the "selectable first-class peers" decision. Larger refactor; revisit if
  package weight matters to adopters.

## Already documented elsewhere
- **Optimistic concurrency on `User`/`Role`** — deferred in the spec (the `rowversion`/`xmin`/Dapper
  split wasn't worth it for the slice).
- **Construction-level child-table tenant isolation** — children are parent-keyed with no `tenant_id`;
  isolation is enforced at the service layer (now symmetric across read + write + token paths). A
  future option is to add `tenant_id` to children for by-construction isolation.
- **`CreateAsync` concurrent-duplicate race** — the pre-check is best-effort; a race surfaces the unique
  index violation as a provider exception rather than `UserCreationResult.Failure`. No corruption.
