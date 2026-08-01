# Windows credential-value provider

## Boundary

`WindowsCredentialValueProvider` implements the schema-1 `ICredentialValueProvider` port with Windows Credential Manager generic credentials. It is a storage capability only. Possessing a reference, reaching the provider, or matching its provider ID grants no broker authority, capability assignment, consent, or permission.

The provider is not wired into runtime, broker, CLI, or Web behavior by this slice. Later lifecycle and broker work must retain the callback-only boundary and apply its own authority proof before invoking this provider.

## Ownership and target derivation

Credential Manager protects and persists the generic credential under the current Windows user's logon boundary with local-machine persistence. EmbodySense supplies neither a machine-wide credential nor an application-managed encryption key. The operating system owns profile protection, encryption-at-rest behavior, and the native credential database. Processes already able to act as the user, debuggers with sufficient rights, administrators, and compromised operating-system components remain outside the protection this provider can supply.

The private native target is derived from a length-delimited UTF-8 workspace-scope identity and the schema-1 credential reference ID, then reduced with SHA-256 to `EmbodySense:v1:<digest>`. Raw workspace and reference text is not placed in the native target. The target and native handles stay private to `Core.Persistence`; public results and metadata contain neither.

## Process-memory handling

Create and replace accept an exact-size synchronous span callback. Use exposes a temporary read buffer only to one synchronous trusted consumer. Provider-owned managed buffers and the native buffer returned by `CredReadW` are overwritten before release wherever the API exposes writable memory. Callback failures, partial writes, cancellation before commit, and count mismatches return closed, value-free failures.

Zeroing narrows retention but cannot prove that the CLR, operating system, a debugger, a crash dump, or hardware never made an additional copy. Callbacks must not retain pointers or otherwise evade the span lifetime. Values must not be converted to strings, arguments, environment variables, URLs, logs, exceptions, workspace files, DTOs, evidence, or model-visible output.

## Limits and availability

The schema-1 port permits values up to 65,536 bytes so other providers can define their own secure bounds. Windows Credential Manager generic credentials accept at most 2,560 bytes; this provider returns `LimitExceeded` before invoking the source callback above that bound. Empty values remain invalid under the schema-1 port.

On non-Windows platforms, or when Credential Manager cannot prove current-user access because the profile is locked, unavailable, corrupt, or ambiguous, the provider returns stable `Unavailable` posture. It does not read environment variables and has no plaintext, encrypted-file, workspace, or automatic-import fallback. A future platform must configure a separate secure provider explicitly.

## Atomicity, concurrency, and crash posture

Operations for one derived target use a bounded named mutex so cooperating processes in the current Windows session serialize create, use, replace, and delete. Credential Manager remains the cross-process system of record. Create performs a missing check under that lock. Replace first proves the prior value is readable, performs the native write, and reads back the candidate. A failed or unverifiable write proves the prior value still matches or attempts a rollback and proves it; otherwise the result is `OutcomeUncertain`. This makes an ordinary replacement failure preserve the prior proved usable value from the provider caller's perspective.

The mutex is abandoned automatically if a process crashes. No plaintext journal, staging path, recovery file, reparse-point traversal, or application ACL is introduced. Native Credential Manager calls are the crash boundary; after interruption, health/use must re-prove the observed state. Processes that bypass the provider and directly mutate the same native target are treated as concurrent hostile state and can force a closed or uncertain outcome.

## Backup, restore, and deletion

EmbodySense does not export, escrow, roam, or back up credential values. Windows profile or credential backup behavior is controlled by the operating system and is not a supported cross-machine transfer mechanism. Restoring a workspace does not restore its credential values; missing values must be supplied again through future authorized lifecycle behavior.

Delete calls Credential Manager and then reads the target to prove absence. A failed delete with a proved remaining readable value returns `Unavailable`; an outcome that cannot be proved returns `OutcomeUncertain`. Successful logical deletion cannot promise forensic erasure of operating-system storage, backups, snapshots, crash dumps, or hardware remnants.

## Secure fake

Provider contract and adversarial tests use a deterministic in-memory fake store behind the same public callback-only provider. The fake clones owned bytes, overwrites replaced/deleted/disposed buffers, serializes target operations through the production lock, and can script outage, corruption, partial mutation, rollback failure, and deletion uncertainty. It is test infrastructure, not a production fallback or a general read-secret API.
