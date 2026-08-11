using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

public sealed class AuthorityGrantDependencySourceTests
{
    [Fact]
    public async Task Profile_source_resolves_active_exact_pin_and_closed_lifecycle_postures()
    {
        var activeRecord = AuthorityGrantApplicationTestFixture.ProfileRecord();
        var pin = new AuthorityGrantProfilePin(
            new(activeRecord.ProfileId, activeRecord.CurrentProfile.Revision),
            activeRecord.CurrentHash);
        var store = new ProfileStore { Result = new(AuthorityProfileReadStatus.Available, activeRecord, "ready") };
        var source = new AuthorityGrantProfileSource(store);

        var active = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        store.Result = new(AuthorityProfileReadStatus.Available, AuthorityGrantApplicationTestFixture.ProfileRecord(tombstoned: true), "ready");
        var disabled = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        var expiredProfile = AuthorityGrantApplicationTestFixture.Profile(expiresAt: AuthorityGrantApplicationTestFixture.Now);
        store.Result = new(AuthorityProfileReadStatus.Available, AuthorityGrantApplicationTestFixture.ProfileRecord(expiredProfile), "ready");
        var expiredPin = new AuthorityGrantProfilePin(new(expiredProfile.ProfileId, expiredProfile.Revision), AuthorityGrantApplicationTestFixture.ProfileHash(expiredProfile));
        var expired = await source.ResolveAsync(expiredPin, AuthorityGrantApplicationTestFixture.Now);

        Assert.Equal(AuthorityGrantDependencyStatus.Active, active.Status);
        Assert.Same(activeRecord.CurrentProfile, active.Profile);
        Assert.Equal(64, active.EvidenceHash.Length);
        Assert.Equal(AuthorityGrantDependencyStatus.Disabled, disabled.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Expired, expired.Status);
    }

    [Fact]
    public async Task Profile_source_requires_applied_correlated_revision_and_tombstone_evidence()
    {
        var valid = AuthorityGrantApplicationTestFixture.ProfileRecord();
        var pin = new AuthorityGrantProfilePin(new(valid.ProfileId, valid.CurrentProfile.Revision), valid.CurrentHash);
        var receipt = valid.Operations[0];
        var store = new ProfileStore();
        var source = new AuthorityGrantProfileSource(store);

        store.Result = Available(Copy(valid, operations: [receipt with { Outcome = AuthorityProfileMutationStatus.Replayed }]));
        var replayReceipt = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        store.Result = Available(Copy(valid, operations: [receipt with { OperationId = "different-operation" }]));
        var uncorrelated = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        store.Result = Available(Copy(valid, operations: [receipt with { ResultingRevision = 2 }]));
        var wrongRevision = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        store.Result = Available(Copy(valid, operations: [receipt with { RecordedAtUtc = receipt.RecordedAtUtc.AddTicks(1) }]));
        var splicedRevisionTime = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        var tombstoned = AuthorityGrantApplicationTestFixture.ProfileRecord(tombstoned: true);
        store.Result = Available(Copy(tombstoned, tombstone: tombstoned.Tombstone! with { OperationId = "other-tombstone" }));
        var badTombstone = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        var tombstoneReceipt = tombstoned.Operations[^1];
        store.Result = Available(Copy(
            tombstoned,
            operations: [tombstoned.Operations[0], tombstoneReceipt with { RecordedAtUtc = tombstoneReceipt.RecordedAtUtc.AddTicks(1) }]));
        var splicedTombstoneTime = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        var widened = AuthorityGrantApplicationTestFixture.Profile(
            revision: 2,
            ceiling: AuthorityGrantApplicationTestFixture.Ceiling(maxTargets: valid.CurrentProfile.Ceiling.MaxTargetCount + 1));
        var widenedHash = AuthorityGrantApplicationTestFixture.ProfileHash(widened);
        var widenedTime = receipt.RecordedAtUtc.AddMinutes(1);
        var widenedRecord = new AuthorityProfileRecord(
            valid.ProfileId,
            widened,
            widenedHash,
            [valid.Revisions[0], new AuthorityProfileRevisionEvidence(widened, widenedHash, "transition-profile", widenedTime)],
            null,
            [
                receipt,
                Receipt(widened, widenedHash, "transition-profile", AuthorityProfileMutationKind.TransitionStatus, 2, widenedTime),
            ]);
        store.Result = Available(widenedRecord);
        var widenedPin = new AuthorityGrantProfilePin(new(widened.ProfileId, widened.Revision), widenedHash);
        var splicedStatusTransition = await source.ResolveAsync(widenedPin, AuthorityGrantApplicationTestFixture.Now);

        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, replayReceipt.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, uncorrelated.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, wrongRevision.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, splicedRevisionTime.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, badTombstone.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, splicedTombstoneTime.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, splicedStatusTransition.Status);
    }

    [Fact]
    public async Task Profile_source_returns_stale_only_for_valid_exact_history()
    {
        var first = AuthorityGrantApplicationTestFixture.Profile();
        var second = AuthorityGrantApplicationTestFixture.Profile(revision: 2);
        var firstHash = AuthorityGrantApplicationTestFixture.ProfileHash(first);
        var secondHash = AuthorityGrantApplicationTestFixture.ProfileHash(second);
        var firstRevision = new AuthorityProfileRevisionEvidence(first, firstHash, "create-profile", AuthorityGrantApplicationTestFixture.Now.AddMinutes(-40));
        var secondRevision = new AuthorityProfileRevisionEvidence(second, secondHash, "revise-profile", AuthorityGrantApplicationTestFixture.Now.AddMinutes(-20));
        var operations = new[]
        {
            Receipt(first, firstHash, "create-profile", AuthorityProfileMutationKind.Create, 1, firstRevision.RecordedAtUtc),
            Receipt(second, secondHash, "revise-profile", AuthorityProfileMutationKind.Revise, 2, secondRevision.RecordedAtUtc),
        };
        var record = new AuthorityProfileRecord(first.ProfileId, second, secondHash, [firstRevision, secondRevision], null, operations);
        var source = new AuthorityGrantProfileSource(new ProfileStore { Result = Available(record) });
        var pin = new AuthorityGrantProfilePin(new(first.ProfileId, first.Revision), firstHash);

        var result = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);

        Assert.Equal(AuthorityGrantDependencyStatus.Stale, result.Status);
        Assert.Same(first, result.Profile);
    }

    [Fact]
    public async Task Profile_source_evidence_uses_the_correlated_causal_head_not_operation_sort_order()
    {
        var first = AuthorityGrantApplicationTestFixture.Profile();
        var second = AuthorityGrantApplicationTestFixture.Profile(revision: 2);
        var firstHash = AuthorityGrantApplicationTestFixture.ProfileHash(first);
        var secondHash = AuthorityGrantApplicationTestFixture.ProfileHash(second);
        var firstTime = AuthorityGrantApplicationTestFixture.Now.AddMinutes(-40);
        var secondTime = AuthorityGrantApplicationTestFixture.Now.AddMinutes(-20);
        var firstRevision = new AuthorityProfileRevisionEvidence(first, firstHash, "z-create", firstTime);
        var alternateFirstRevision = firstRevision with { OperationId = "y-create" };
        var secondRevision = new AuthorityProfileRevisionEvidence(second, secondHash, "a-revise", secondTime);
        var headReceipt = Receipt(second, secondHash, "a-revise", AuthorityProfileMutationKind.Revise, 2, secondTime);
        var firstRecord = new AuthorityProfileRecord(
            first.ProfileId,
            second,
            secondHash,
            [firstRevision, secondRevision],
            null,
            [headReceipt, Receipt(first, firstHash, "z-create", AuthorityProfileMutationKind.Create, 1, firstTime)]);
        var alternateRecord = new AuthorityProfileRecord(
            first.ProfileId,
            second,
            secondHash,
            [alternateFirstRevision, secondRevision],
            null,
            [headReceipt, Receipt(first, firstHash, "y-create", AuthorityProfileMutationKind.Create, 1, firstTime)]);
        var store = new ProfileStore { Result = Available(firstRecord) };
        var source = new AuthorityGrantProfileSource(store);
        var pin = new AuthorityGrantProfilePin(new(second.ProfileId, second.Revision), secondHash);

        var firstResult = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);
        store.Result = Available(alternateRecord);
        var alternateResult = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);

        Assert.Equal(AuthorityGrantDependencyStatus.Active, firstResult.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Active, alternateResult.Status);
        Assert.Equal(firstResult.EvidenceHash, alternateResult.EvidenceHash);
    }

    [Fact]
    public async Task Profile_source_rejects_reversed_revision_evidence_time()
    {
        var first = AuthorityGrantApplicationTestFixture.Profile();
        var second = AuthorityGrantApplicationTestFixture.Profile(revision: 2);
        var firstHash = AuthorityGrantApplicationTestFixture.ProfileHash(first);
        var secondHash = AuthorityGrantApplicationTestFixture.ProfileHash(second);
        var firstTime = AuthorityGrantApplicationTestFixture.Now.AddMinutes(-20);
        var secondTime = firstTime.AddTicks(-1);
        var record = new AuthorityProfileRecord(
            first.ProfileId,
            second,
            secondHash,
            [
                new AuthorityProfileRevisionEvidence(first, firstHash, "create-profile", firstTime),
                new AuthorityProfileRevisionEvidence(second, secondHash, "revise-profile", secondTime),
            ],
            null,
            [
                Receipt(first, firstHash, "create-profile", AuthorityProfileMutationKind.Create, 1, firstTime),
                Receipt(second, secondHash, "revise-profile", AuthorityProfileMutationKind.Revise, 2, secondTime),
            ]);
        var pin = new AuthorityGrantProfilePin(new(second.ProfileId, second.Revision), secondHash);
        var source = new AuthorityGrantProfileSource(new ProfileStore { Result = Available(record) });

        var result = await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now);

        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, result.Status);
    }

    [Fact]
    public async Task Profile_source_rejects_causal_profile_state_newer_than_evaluation_time()
    {
        var profile = AuthorityGrantApplicationTestFixture.Profile(
            issuedAt: AuthorityGrantApplicationTestFixture.Now.AddHours(-1),
            expiresAt: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-10));
        var record = AuthorityGrantApplicationTestFixture.ProfileRecord(profile);
        var pin = new AuthorityGrantProfilePin(new(record.ProfileId, profile.Revision), record.CurrentHash);
        var source = new AuthorityGrantProfileSource(new ProfileStore { Result = Available(record) });
        var evaluatedAtUtc = record.Revisions[^1].RecordedAtUtc.AddMinutes(-15);

        var result = await source.ResolveAsync(pin, evaluatedAtUtc);

        Assert.True(profile.IssuedAtUtc < evaluatedAtUtc);
        Assert.True(evaluatedAtUtc < record.Revisions[^1].RecordedAtUtc);
        Assert.True(profile.ExpiresAtUtc > evaluatedAtUtc);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, result.Status);
        Assert.Null(result.Profile);
        Assert.Empty(result.EvidenceHash);
    }

    [Fact]
    public async Task Profile_source_fail_closes_invalid_unavailable_and_hostile_results()
    {
        var record = AuthorityGrantApplicationTestFixture.ProfileRecord();
        var pin = new AuthorityGrantProfilePin(new(record.ProfileId, record.CurrentProfile.Revision), record.CurrentHash);
        var store = new ProfileStore { Result = new(AuthorityProfileReadStatus.NotFound, null, "missing") };
        var source = new AuthorityGrantProfileSource(store);

        Assert.Equal(AuthorityGrantDependencyStatus.Invalid, (await source.ResolveAsync(null, AuthorityGrantApplicationTestFixture.Now)).Status);
        Assert.Equal(AuthorityGrantDependencyStatus.NotFound, (await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now)).Status);
        store.Result = new(AuthorityProfileReadStatus.Unavailable, null, "unavailable");
        Assert.Equal(AuthorityGrantDependencyStatus.Unavailable, (await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now)).Status);
        store.Result = null!;
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, (await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now)).Status);
        store.Result = Available(new AuthorityProfileRecord(
            null!,
            record.CurrentProfile,
            record.CurrentHash,
            record.Revisions,
            record.Tombstone,
            record.Operations));
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, (await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now)).Status);
        var malformedProfile = record.CurrentProfile with { Revision = null! };
        store.Result = Available(new AuthorityProfileRecord(
            record.ProfileId,
            record.CurrentProfile,
            record.CurrentHash,
            [record.Revisions[0] with { Profile = malformedProfile }],
            record.Tombstone,
            record.Operations));
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, (await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now)).Status);
        store.Exception = new IOException("offline");
        Assert.Equal(AuthorityGrantDependencyStatus.Unavailable, (await source.ResolveAsync(pin, AuthorityGrantApplicationTestFixture.Now)).Status);
    }

    [Fact]
    public async Task Role_source_resolves_exact_active_stale_and_disabled_postures()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var ports = new RolePorts
        {
            RevisionResult = new(ContextualRoleRevisionReadStatus.Found, role, ContextualRoleRevisionDisposition.Active, []),
            LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, AuthorityGrantApplicationTestFixture.RoleLifecycle(role)),
        };
        var source = RoleSource(ports);

        var active = await source.ResolveAsync(pin);
        ports.RevisionResult = ports.RevisionResult with { Disposition = ContextualRoleRevisionDisposition.Replaced };
        var stale = await source.ResolveAsync(pin);
        ports.RevisionResult = ports.RevisionResult with { Disposition = ContextualRoleRevisionDisposition.Active };
        ports.LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, AuthorityGrantApplicationTestFixture.RoleLifecycle(role, ContextualRoleLifecycleState.Disabled));
        var disabled = await source.ResolveAsync(pin);

        Assert.Equal(AuthorityGrantDependencyStatus.Active, active.Status);
        Assert.Equal(64, active.EvidenceHash.Length);
        Assert.Equal(AuthorityGrantApplicationTestFixture.WorkspaceId, active.WorkspaceId);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ready, active.SourceStatus);
        Assert.Equal(3, ports.LifecycleReads);
        Assert.Equal(AuthorityGrantDependencyStatus.Stale, stale.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Disabled, disabled.Status);
    }

    [Fact]
    public async Task Role_source_requires_canonical_bounded_operation_evidence_and_exact_shapes()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var lifecycle = AuthorityGrantApplicationTestFixture.RoleLifecycle(role);
        var ports = new RolePorts
        {
            RevisionResult = new(ContextualRoleRevisionReadStatus.Found, role, ContextualRoleRevisionDisposition.Active, []),
            LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, lifecycle with { LastOperationId = new string('x', 121) }),
        };
        var source = RoleSource(ports);

        var oversized = await source.ResolveAsync(pin);
        ports.LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, lifecycle with { LastOperationId = "unsafe.operation" });
        var unsafeId = await source.ResolveAsync(pin);
        ports.LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, lifecycle with { CurrentIdentity = new("other-role", 1) });
        var malformed = await source.ResolveAsync(pin);

        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, oversized.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, unsafeId.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, malformed.Status);
    }

    [Fact]
    public async Task Role_source_maps_closed_read_failures_without_following_substitutions()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var ports = new RolePorts { RevisionResult = new(ContextualRoleRevisionReadStatus.NotFound, null, ContextualRoleRevisionDisposition.Unknown, []) };
        var source = RoleSource(ports);

        Assert.Equal(AuthorityGrantDependencyStatus.Invalid, (await source.ResolveAsync(null)).Status);
        Assert.Equal(AuthorityGrantDependencyStatus.NotFound, (await source.ResolveAsync(pin)).Status);
        ports.RevisionResult = new(ContextualRoleRevisionReadStatus.Unavailable, null, ContextualRoleRevisionDisposition.Unknown, []);
        Assert.Equal(AuthorityGrantDependencyStatus.Unavailable, (await source.ResolveAsync(pin)).Status);
        ports.RevisionException = new IOException("offline");
        Assert.Equal(AuthorityGrantDependencyStatus.Unavailable, (await source.ResolveAsync(pin)).Status);
    }

    [Fact]
    public async Task Role_source_fails_closed_for_workspace_source_and_post_probe_lifecycle_drift()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var lifecycle = AuthorityGrantApplicationTestFixture.RoleLifecycle(role);
        var ports = new RolePorts
        {
            RevisionResult = new(ContextualRoleRevisionReadStatus.Found, role, ContextualRoleRevisionDisposition.Active, []),
            LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, lifecycle),
        };

        var wrongWorkspaceRole = ContextualRoleRevisionContentHash.Apply(role with
        {
            WorkspaceApplicability = new(["workspace-sha256:" + new string('b', ContextualRoleLimits.Sha256HexCharacters)]),
        });
        ports.RevisionResult = ports.RevisionResult with { Revision = wrongWorkspaceRole };
        var wrongWorkspace = await RoleSource(ports).ResolveAsync(new ContextualRoleRevisionPin(wrongWorkspaceRole.Identity, wrongWorkspaceRole.ContentHash));
        Assert.Equal(0, ports.SourceReads);

        ports.RevisionResult = ports.RevisionResult with { Revision = role };
        ports.ProbeResult = new(ContextualRoleInstructionSourceProbeStatus.Substituted);
        var substituted = await RoleSource(ports).ResolveAsync(pin);

        ports.ProbeResult = new(ContextualRoleInstructionSourceProbeStatus.Ready);
        ports.LifecycleResults.Enqueue(new(ContextualRoleLifecycleReadStatus.Found, lifecycle));
        ports.LifecycleResults.Enqueue(new(ContextualRoleLifecycleReadStatus.Found, lifecycle with { State = ContextualRoleLifecycleState.Disabled, UpdatedAtUtc = lifecycle.UpdatedAtUtc.AddMinutes(1) }));
        var drifted = await RoleSource(ports).ResolveAsync(pin);

        Assert.Equal(AuthorityGrantDependencyStatus.Disabled, wrongWorkspace.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch, wrongWorkspace.SourceStatus);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, substituted.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Substituted, substituted.SourceStatus);
        Assert.Equal(AuthorityGrantDependencyStatus.Stale, drifted.Status);
        Assert.Equal(ContextualRoleLifecycleState.Disabled, drifted.Lifecycle!.State);
    }

    [Fact]
    public async Task Role_source_evidence_is_deterministic_and_cancellation_propagates()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var ports = new RolePorts
        {
            RevisionResult = new(ContextualRoleRevisionReadStatus.Found, role, ContextualRoleRevisionDisposition.Active, []),
            LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, AuthorityGrantApplicationTestFixture.RoleLifecycle(role)),
        };
        var source = RoleSource(ports);

        var first = await source.ResolveAsync(pin);
        var second = await source.ResolveAsync(pin);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(first.EvidenceHash, second.EvidenceHash);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ResolveAsync(pin, cancellation.Token));
    }

    [Fact]
    public async Task Role_source_rejects_revision_and_hash_substitution_before_lifecycle_or_source_reads()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var ports = new RolePorts
        {
            RevisionResult = new(ContextualRoleRevisionReadStatus.Found, role, ContextualRoleRevisionDisposition.Active, []),
            LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, AuthorityGrantApplicationTestFixture.RoleLifecycle(role)),
        };
        var source = RoleSource(ports);

        var revisionSubstitution = await source.ResolveAsync(new ContextualRoleRevisionPin(new(role.Identity.RoleId, role.Identity.Revision + 1), role.ContentHash));
        var hashSubstitution = await source.ResolveAsync(new ContextualRoleRevisionPin(role.Identity, AuthorityGrantApplicationTestFixture.Hash64('f')));

        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, revisionSubstitution.Status);
        Assert.Equal(AuthorityGrantDependencyStatus.Ambiguous, hashSubstitution.Status);
        Assert.Equal(0, ports.LifecycleReads);
        Assert.Equal(0, ports.SourceReads);
    }

    [Fact]
    public async Task Role_source_is_reentrant_under_the_caller_owned_shared_fence()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var ports = new RolePorts
        {
            RevisionResult = new(ContextualRoleRevisionReadStatus.Found, role, ContextualRoleRevisionDisposition.Active, []),
            LifecycleResult = new(ContextualRoleLifecycleReadStatus.Found, AuthorityGrantApplicationTestFixture.RoleLifecycle(role)),
        };
        var transaction = new SerializingCapabilityAuthorityTransaction();
        var source = RoleSource(ports, transaction);

        var result = await transaction.ExecuteAsync(token => source.ResolveAsync(pin, token));

        Assert.Equal(AuthorityGrantDependencyStatus.Active, result.Status);
    }

    private static AuthorityGrantRoleSource RoleSource(RolePorts ports, ICapabilityAuthorityTransaction? transaction = null)
        => new(AuthorityGrantApplicationTestFixture.WorkspaceId, ports, ports, ports, transaction ?? new StubCapabilityAuthorityTransaction());

    private static AuthorityProfileReadResult Available(AuthorityProfileRecord record)
        => new(AuthorityProfileReadStatus.Available, record, "ready");

    private static AuthorityProfileRecord Copy(
        AuthorityProfileRecord record,
        IReadOnlyList<AuthorityProfileOperationReceipt>? operations = null,
        AuthorityProfileTombstone? tombstone = null)
        => new(record.ProfileId, record.CurrentProfile, record.CurrentHash, record.Revisions, tombstone ?? record.Tombstone, operations ?? record.Operations);

    private static AuthorityProfileOperationReceipt Receipt(
        EmbodySense.Core.Common.Authority.Models.AuthorityProfile profile,
        EmbodySense.Core.Common.Authority.AuthorityProfileHash hash,
        string operationId,
        AuthorityProfileMutationKind kind,
        int revision,
        DateTimeOffset time)
        => new(operationId, hash.Value[7..], kind, AuthorityProfileMutationStatus.Applied, profile.ProfileId, revision, AuthorityGrantApplicationTestFixture.Actor(), AuthorityGrantApplicationTestFixture.Purpose(), time);

    private sealed class ProfileStore : IAuthorityProfileStore
    {
        public AuthorityProfileReadResult Result { get; set; } = null!;
        public Exception? Exception { get; set; }

        public Task<AuthorityProfileReadResult> ReadAsync(string profileId, CancellationToken cancellationToken = default)
            => Exception is null ? Task.FromResult(Result) : Task.FromException<AuthorityProfileReadResult>(Exception);

        public Task<AuthorityProfileMutationResult> MutateAsync(AuthorityProfileMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RolePorts : IContextualRoleRevisionReader, IContextualRoleLifecycleReader, IContextualRoleInstructionSourceProbe
    {
        public ContextualRoleRevisionReadResult RevisionResult { get; set; } = null!;
        public ContextualRoleLifecycleReadResult LifecycleResult { get; set; } = null!;
        public ContextualRoleInstructionSourceProbeResult ProbeResult { get; set; } = new(ContextualRoleInstructionSourceProbeStatus.Ready);
        public Queue<ContextualRoleLifecycleReadResult> LifecycleResults { get; } = new();
        public Exception? RevisionException { get; set; }
        public int LifecycleReads { get; private set; }
        public int SourceReads { get; private set; }

        public Task<ContextualRoleRevisionReadResult> ReadAsync(ContextualRoleRevisionReadRequest request, CancellationToken cancellationToken = default)
            => RevisionException is null ? Task.FromResult(RevisionResult) : Task.FromException<ContextualRoleRevisionReadResult>(RevisionException);

        public Task<ContextualRoleLifecycleReadResult> ReadLifecycleAsync(ContextualRoleLifecycleReadRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LifecycleReads++;
            return Task.FromResult(LifecycleResults.Count > 0 ? LifecycleResults.Dequeue() : LifecycleResult);
        }

        public Task<ContextualRoleInstructionSourceProbeResult> ProbeAsync(ContextualRoleInstructionSourceReference source, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceReads++;
            return Task.FromResult(ProbeResult);
        }
    }
}
