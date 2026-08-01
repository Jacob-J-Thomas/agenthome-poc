using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Authority;

public sealed class AuthorityProfileStoreTests : IDisposable
{
    private readonly TestWorkspace _trustRoot = new();
    private readonly FileCapabilityCatalogTrustProvider _trustProvider;

    public AuthorityProfileStoreTests()
    {
        _trustProvider = new FileCapabilityCatalogTrustProvider(_trustRoot.RootPath);
    }

    public void Dispose()
    {
        _trustRoot.Dispose();
    }

    [Fact]
    public async Task Create_revise_transition_tombstone_and_restart_preserve_immutable_hashed_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = Profile();

        var created = await store.MutateAsync(Create(profile, "create-profile"));
        var revisedProfile = profile with { Revision = Revision(2), Purpose = Purpose("Inspect a bounded workspace after an explicit user correction.") };
        var revised = await store.MutateAsync(Revise(revisedProfile, "revise-profile"));
        var transitioned = await store.MutateAsync(Transition(profile.ProfileId, 2, AuthorityProfileStatus.Suspended, "suspend-profile"));
        var tombstoned = await store.MutateAsync(Tombstone(profile.ProfileId, 3, "tombstone-profile"));
        var restartRead = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, revised.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, transitioned.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, tombstoned.Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, restartRead.Status);
        var record = Assert.IsType<AuthorityProfileRecord>(restartRead.Record);
        Assert.Equal(3, record.Revisions.Count);
        Assert.Equal(AuthorityProfileStatus.Suspended, record.CurrentProfile.Status);
        Assert.NotNull(record.Tombstone);
        Assert.Equal(4, record.Operations.Count);
        Assert.All(record.Revisions, revision => Assert.True(AuthorityProfileHash.TryCompute(revision.Profile, out var hash, out _)));
        Assert.Equal(record.Revisions.Select(revision => revision.Hash.Value), record.Revisions.Select(revision => AuthorityProfileHash.TryCompute(revision.Profile, out var hash, out _) ? hash!.Value : null));
        Assert.DoesNotContain("private-target", await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_is_exact_while_changed_intent_stale_revision_and_resurrection_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var profile = Profile();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var mutation = Create(profile, "same-operation");

        var created = await store.MutateAsync(mutation);
        var transitioned = await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "later-operation"));
        var replayed = await store.MutateAsync(mutation);
        var changedIntent = await store.MutateAsync(Transition(profile.ProfileId, 2, AuthorityProfileStatus.Active, "same-operation"));
        var stale = await store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Retired, "stale-operation"));
        var tombstoned = await store.MutateAsync(Tombstone(profile.ProfileId, 2, "tombstone-operation"));
        var resurrection = await store.MutateAsync(Revise(profile with { Revision = Revision(3) }, "resurrect-operation"));

        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, transitioned.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Replayed, replayed.Status);
        Assert.Equal(1, replayed.Record!.CurrentProfile.Revision.Value);
        Assert.Equal(AuthorityProfileMutationStatus.Conflict, changedIntent.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Conflict, stale.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, tombstoned.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, resurrection.Status);
    }

    [Fact]
    public async Task Invalid_duplicate_and_missing_inputs_fail_closed_without_creating_operation_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = Profile();

        var invalidRead = await store.ReadAsync("not a profile id");
        var missingRead = await store.ReadAsync("missing-profile");
        var nullMutation = await store.MutateAsync(null!);
        var missingMutation = await store.MutateAsync(Transition(ProfileId("missing-profile"), 1, AuthorityProfileStatus.Suspended, "transition-missing"));
        var created = await store.MutateAsync(Create(profile, "create-once"));
        var duplicate = await store.MutateAsync(Create(profile, "create-twice"));
        var invalidCreate = await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.Create, "create-invalid-revision", 1, profile with { Revision = Revision(2) }, null, null, Actor(), Reason()));
        var invalidTransition = await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.TransitionStatus, "transition-without-status", 1, null, profile.ProfileId, null, Actor(), Reason()));
        var persisted = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, invalidRead.Status);
        Assert.Equal(AuthorityProfileReadStatus.NotFound, missingRead.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, nullMutation.Status);
        Assert.Equal(AuthorityProfileMutationStatus.NotFound, missingMutation.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, duplicate.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, invalidCreate.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Invalid, invalidTransition.Status);
        Assert.Single(persisted.Record!.Operations);
        Assert.Equal("create-once", persisted.Record.Operations[0].OperationId);
    }

    [Fact]
    public async Task Corrupt_primary_recovers_last_proved_state_and_substituted_workspace_is_unavailable()
    {
        using var source = new TestWorkspace();
        using var target = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var profile = Profile();
        var created = await Store(sourcePaths).MutateAsync(Create(profile, "create-source"));
        Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(sourcePaths).MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "advance-source"))).Status);

        var targetPaths = new WorkspacePaths(target.RootPath);
        Directory.CreateDirectory(targetPaths.AuthorityProfilesPath);
        File.Copy(sourcePaths.AuthorityProfilesDocumentPath, targetPaths.AuthorityProfilesDocumentPath, true);
        var substituted = await Store(targetPaths).ReadAsync(profile.ProfileId.Value);
        await File.WriteAllTextAsync(sourcePaths.AuthorityProfilesDocumentPath, "{partial");
        var recovered = await Store(sourcePaths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, substituted.Status);
        Assert.Equal(AuthorityProfileReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Equal(profile.ProfileId, recovered.Record!.ProfileId);
    }

    [Fact]
    public async Task Concurrent_creates_with_the_same_expected_revision_are_serialized_by_cross_process_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = Store(paths);
        var second = Store(paths);
        var results = await Task.WhenAll(first.MutateAsync(Create(Profile("first-profile"), "create-first")), second.MutateAsync(Create(Profile("second-profile"), "create-second")));

        Assert.Equal(2, results.Count(result => result.Status == AuthorityProfileMutationStatus.Applied));
        Assert.Equal(AuthorityProfileReadStatus.Available, (await first.ReadAsync("first-profile")).Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, (await second.ReadAsync("second-profile")).Status);
    }

    [Fact]
    public async Task Revision_quota_accepts_128_rejects_129_without_poisoning_read_or_tombstone_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await store.MutateAsync(Create(profile, "revision-1"))).Status);
        for (var revision = 2; revision <= AuthorityProfileStoreLimits.MaximumRevisionsPerProfile; revision++)
        {
            var status = revision % 2 == 0 ? AuthorityProfileStatus.Suspended : AuthorityProfileStatus.Active;
            var result = await store.MutateAsync(Transition(profile.ProfileId, revision - 1, status, $"revision-{revision}"));
            Assert.Equal(AuthorityProfileMutationStatus.Applied, result.Status);
        }

        var atLimit = await Store(paths).ReadAsync(profile.ProfileId.Value);
        var rejected = await store.MutateAsync(Transition(profile.ProfileId, AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, AuthorityProfileStatus.Retired, "revision-129"));
        var afterRejected = await Store(paths).ReadAsync(profile.ProfileId.Value);
        var tombstoned = await store.MutateAsync(Tombstone(profile.ProfileId, AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, "tombstone-at-revision-limit"));
        var afterTombstone = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, atLimit.Record!.Revisions.Count);
        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, rejected.Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, afterRejected.Status);
        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, afterRejected.Record!.CurrentProfile.Revision.Value);
        Assert.DoesNotContain(afterRejected.Record.Operations, operation => operation.OperationId == "revision-129");
        Assert.Equal(AuthorityProfileMutationStatus.Applied, tombstoned.Status);
        Assert.NotNull(afterTombstone.Record!.Tombstone);
        Assert.Equal(AuthorityProfileStoreLimits.MaximumRevisionsPerProfile, afterTombstone.Record.CurrentProfile.Revision.Value);
    }

    [Fact]
    public async Task Mutation_does_not_report_applied_before_both_authority_artifacts_cross_the_durability_barrier()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-barrier"))).Status);
        var barrier = new BlockingAuthorityProfileDurabilityBarrier();
        var store = new AuthorityProfileStore(paths, _trustProvider, durabilityBarrier: barrier);

        var mutation = store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-behind-barrier"));
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(mutation.IsCompleted);
        barrier.Release();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await mutation).Status);
        Assert.Equal(2, barrier.CallCount);
    }

    [Fact]
    public async Task Candidate_rename_durability_failure_returns_unavailable_and_recovers_prior_proved_profile()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-rename-failure"))).Status);
        var barrier = new BlockingAuthorityProfileDurabilityBarrier { TargetCall = 2, Failure = new IOException("Injected candidate durability failure.") };
        var store = new AuthorityProfileStore(paths, _trustProvider, durabilityBarrier: barrier);

        var mutation = store.MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-with-rename-failure"));
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(mutation.IsCompleted);
        barrier.Release();
        var failed = await mutation;
        var recovered = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, failed.Status);
        Assert.Equal(AuthorityProfileReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Equal(1, recovered.Record!.CurrentProfile.Revision.Value);
        Assert.Equal(AuthorityProfileStatus.Active, recovered.Record.CurrentProfile.Status);
        Assert.DoesNotContain(recovered.Record.Operations, operation => operation.OperationId == "transition-with-rename-failure");
    }

    [Fact]
    public async Task Trust_anchor_advance_failure_returns_unavailable_and_preserves_prior_read_only_proof()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-anchor-failure"))).Status);
        var failing = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };

        var failed = await new AuthorityProfileStore(paths, failing).MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-before-anchor-failure"));
        var recovered = await Store(paths).ReadAsync(profile.ProfileId.Value);

        Assert.Equal(AuthorityProfileMutationStatus.Unavailable, failed.Status);
        Assert.Equal(AuthorityProfileReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Equal(1, recovered.Record!.CurrentProfile.Revision.Value);
        Assert.DoesNotContain(recovered.Record.Operations, operation => operation.OperationId == "transition-before-anchor-failure");
    }

    [Fact]
    public async Task Windows_external_lock_owner_blocks_mutation_until_ownership_is_released()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = Profile();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await Store(paths).MutateAsync(Create(profile, "create-before-owner-contention"))).Status);
        using var ownership = new FileStream(paths.AuthorityProfilesLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var mutation = Store(paths).MutateAsync(Transition(profile.ProfileId, 1, AuthorityProfileStatus.Suspended, "transition-after-owner-contention"));
        await Task.Delay(100);
        Assert.False(mutation.IsCompleted);
        ownership.Dispose();

        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await mutation).Status);
    }

    private AuthorityProfileStore Store(WorkspacePaths paths) => new(paths, _trustProvider);

    private static AuthorityProfileMutation Create(AuthorityProfile profile, string operationId) => new(AuthorityProfileMutationKind.Create, operationId, 0, profile, null, null, Actor(), Reason());
    private static AuthorityProfileMutation Revise(AuthorityProfile profile, string operationId) => new(AuthorityProfileMutationKind.Revise, operationId, profile.Revision.Value - 1, profile, null, null, Actor(), Reason());
    private static AuthorityProfileMutation Transition(AuthorityProfileId id, int expectedRevision, AuthorityProfileStatus status, string operationId) => new(AuthorityProfileMutationKind.TransitionStatus, operationId, expectedRevision, null, id, status, Actor(), Reason());
    private static AuthorityProfileMutation Tombstone(AuthorityProfileId id, int expectedRevision, string operationId) => new(AuthorityProfileMutationKind.Tombstone, operationId, expectedRevision, null, id, null, Actor(), Reason());

    private static AuthorityProfile Profile(string id = "workspace-observer") => new(AuthorityProfile.CurrentSchemaVersion, ProfileId(id), Revision(1), AuthorityProfileStatus.Active, Purpose("Inspect bounded workspace state for one user-directed task."), new AuthorityProvenance(Actor(), AuthorityProvenanceKind.UserDeclaration), new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), null, new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.ReadOnly, false, false, false), []);
    private static AuthorityProfileId ProfileId(string value)
    {
        Assert.True(AuthorityProfileId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static AuthorityProfileRevision Revision(int value)
    {
        Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parsed, out _));
        return parsed!;
    }

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("user-owner", out var parsed, out _));
        return parsed!;
    }

    private static AuthorityPurpose Reason() => Purpose("User-directed lifecycle record.");
    private static AuthorityPurpose Purpose(string value)
    {
        Assert.True(AuthorityPurpose.TryParse(value, out var parsed, out _));
        return parsed!;
    }
}
