using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Authority;

public sealed class AuthorityGrantStoreTests : IDisposable
{
    private const string CrossProcessMode = "EMBODYSENSE_AUTHORITY_GRANT_STORE_MODE";
    private const string CrossProcessWorkspace = "EMBODYSENSE_AUTHORITY_GRANT_STORE_WORKSPACE";
    private const string CrossProcessTrustRoot = "EMBODYSENSE_AUTHORITY_GRANT_STORE_TRUST_ROOT";
    private const string CrossProcessMarker = "EMBODYSENSE_AUTHORITY_GRANT_STORE_MARKER";
    private const string CrossProcessResult = "EMBODYSENSE_AUTHORITY_GRANT_STORE_RESULT";
    private static readonly DateTimeOffset _now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, false) } };
    private readonly TestWorkspace _trustRoot = new();
    private readonly FileCapabilityCatalogTrustProvider _trustProvider;

    public AuthorityGrantStoreTests()
    {
        _trustProvider = new FileCapabilityCatalogTrustProvider(_trustRoot.RootPath);
    }

    public void Dispose() => _trustRoot.Dispose();

    [Fact]
    public async Task Create_restart_and_readback_preserve_exact_grant_lineage_and_operation_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        var observation = await store.ReadForMutationAsync(grant.GrantId, "create-grant", Hash('1'));
        var mutation = Mutation(observation.StoreGeneration, grant, Evidence(grant, "create-grant", AuthorityGrantOperationKind.Create, Hash('1')));

        var committed = await store.CommitAsync(mutation);
        var reopened = await Store(paths).ReadAsync(grant.GrantId);

        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, observation.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(2, committed.StoreGeneration);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, reopened.Status);
        Assert.Equal(grant, reopened.Snapshot!.CurrentGrant);
        Assert.Single(reopened.Snapshot.Revisions);
        Assert.Single(reopened.Snapshot.Operations);
        Assert.Equal("create-grant", reopened.Snapshot.Operations[0].OperationId);
        var persisted = await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath);
        Assert.Contains("\"grants\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"grantOperations\"", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_replay_is_stable_while_changed_intent_and_stale_generation_conflict()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        var create = Mutation(1, grant, Evidence(grant, "create-grant", AuthorityGrantOperationKind.Create, Hash('1')));
        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, (await store.CommitAsync(create)).Status);

        var replay = await store.CommitAsync(create);
        var changed = await store.CommitAsync(create with { Operation = create.Operation with { RequestHash = Hash('2') } });
        var suspended = Successor(grant, AuthorityGrantLifecycleStatus.Suspended);
        var stale = await store.CommitAsync(Mutation(1, suspended, Evidence(suspended, "suspend-grant", AuthorityGrantOperationKind.Suspend, Hash('3'))));
        var committed = await store.CommitAsync(Mutation(2, suspended, Evidence(suspended, "suspend-grant", AuthorityGrantOperationKind.Suspend, Hash('3'))));

        Assert.Equal(AuthorityGrantStoreCommitStatus.Replayed, replay.Status);
        Assert.Equal(grant, replay.Snapshot!.CurrentGrant);
        Assert.Equal(AuthorityGrantStoreCommitStatus.OperationConflict, changed.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.StoreConflict, stale.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(AuthorityGrantLifecycleStatus.Suspended, committed.Snapshot!.CurrentGrant.Status);
        Assert.Equal(2, committed.Snapshot.Revisions.Count);
    }

    [Fact]
    public async Task Receipt_only_replay_never_leaks_a_grant_created_later()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        var receipt = Receipt(grant.GrantId, "missing-before-create", Hash('4'));
        var receiptMutation = new AuthorityGrantStoreMutation(1, null, receipt);

        var retained = await store.CommitAsync(receiptMutation);
        var beforeCreate = await store.ReadForMutationAsync(grant.GrantId, receipt.OperationId, receipt.RequestHash);
        var create = Mutation(2, grant, Evidence(grant, "create-after-receipt", AuthorityGrantOperationKind.Create, Hash('5')));
        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, (await store.CommitAsync(create)).Status);
        store = Store(paths);
        var replay = await store.CommitAsync(receiptMutation);
        var replayRead = await store.ReadForMutationAsync(grant.GrantId, receipt.OperationId, receipt.RequestHash);
        var current = await store.ReadAsync(grant.GrantId);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, retained.Status);
        Assert.Null(retained.Snapshot);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, beforeCreate.Status);
        Assert.NotNull(beforeCreate.ExistingOperation);
        Assert.Null(beforeCreate.Snapshot);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Replayed, replay.Status);
        Assert.Null(replay.Snapshot);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, replayRead.Status);
        Assert.NotNull(replayRead.ExistingOperation);
        Assert.Null(replayRead.Snapshot);
        Assert.Equal(grant, current.Snapshot!.CurrentGrant);
        Assert.Equal(2, current.Snapshot.Operations.Count);
    }

    [Fact]
    public async Task Replace_boundary_conflict_receipt_is_durable_and_restart_replayable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(1, grant, Evidence(grant, "create-before-replace-conflict", AuthorityGrantOperationKind.Create, Hash('1'))))).Status);
        var receipt = Receipt(grant.GrantId, "replace-boundary-conflict", Hash('2')) with
        {
            Kind = AuthorityGrantOperationKind.Replace,
            Outcome = AuthorityGrantOperationOutcome.Conflict,
            FailureCode = AuthorityGrantOperationFailureCode.BoundaryConflict
        };
        var mutation = new AuthorityGrantStoreMutation(2, null, receipt);

        var committed = await store.CommitAsync(mutation);
        var replayed = await Store(paths).CommitAsync(mutation);
        var read = await Store(paths).ReadForMutationAsync(grant.GrantId, receipt.OperationId, receipt.RequestHash);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(receipt, committed.StoredOperation!.Evidence);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(receipt, replayed.StoredOperation!.Evidence);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, read.Status);
        Assert.Equal(grant, read.Snapshot!.CurrentGrant);
        Assert.Equal(receipt, read.ExistingOperation!.Evidence);
        Assert.Equal(2, read.Snapshot.Operations.Count);
    }

    [Fact]
    public async Task Receipt_only_storage_rejects_malformed_authority_denied_and_unavailable_attempts()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        _ = await CreateProfileAsync(store);
        var grantId = GrantId("bounded-grant");
        var denied = Receipt(grantId, "denied-receipt", Hash('6')) with
        {
            Outcome = AuthorityGrantOperationOutcome.Denied,
            FailureCode = AuthorityGrantOperationFailureCode.AuthorityDenied
        };
        var unavailable = Receipt(grantId, "unavailable-receipt", Hash('7')) with
        {
            Outcome = AuthorityGrantOperationOutcome.Unavailable,
            FailureCode = AuthorityGrantOperationFailureCode.StoreUnavailable
        };

        var deniedResult = await store.CommitAsync(new AuthorityGrantStoreMutation(1, null, denied));
        var unavailableResult = await store.CommitAsync(new AuthorityGrantStoreMutation(1, null, unavailable));
        var read = await store.ReadForMutationAsync(grantId, denied.OperationId, denied.RequestHash);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Unavailable, deniedResult.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Unavailable, unavailableResult.Status);
        Assert.Null(read.ExistingOperation);
        Assert.Equal(1, read.StoreGeneration);
    }

    [Fact]
    public async Task Receipt_only_storage_rejects_dependency_receipts_and_not_found_for_an_existing_target()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(1, grant, Evidence(grant, "create-before-invalid-receipts", AuthorityGrantOperationKind.Create, Hash('6'))))).Status);

        var notFound = Receipt(grant.GrantId, "not-found-existing-target", Hash('7'));
        var dependencyUnavailable = Receipt(GrantId("missing-dependency-target"), "dependency-unavailable", Hash('8')) with
        {
            FailureCode = AuthorityGrantOperationFailureCode.ProfileUnavailable
        };
        var mismatchedBoundary = Receipt(GrantId("missing-boundary-target"), "mismatched-boundary", Hash('9')) with
        {
            FailureCode = AuthorityGrantOperationFailureCode.BoundaryConflict
        };
        var mismatchedLimit = Receipt(GrantId("missing-limit-target"), "mismatched-limit", Hash('a')) with
        {
            Outcome = AuthorityGrantOperationOutcome.LimitExceeded
        };

        var notFoundResult = await store.CommitAsync(new AuthorityGrantStoreMutation(2, null, notFound));
        var dependencyResult = await store.CommitAsync(new AuthorityGrantStoreMutation(2, null, dependencyUnavailable));
        var boundaryResult = await store.CommitAsync(new AuthorityGrantStoreMutation(2, null, mismatchedBoundary));
        var limitResult = await store.CommitAsync(new AuthorityGrantStoreMutation(2, null, mismatchedLimit));
        var read = await store.ReadForMutationAsync(grant.GrantId, notFound.OperationId, notFound.RequestHash);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Unavailable, notFoundResult.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Unavailable, dependencyResult.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Unavailable, boundaryResult.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Unavailable, limitResult.Status);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, read.Status);
        Assert.Null(read.ExistingOperation);
        Assert.Equal(2, read.StoreGeneration);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("denied")]
    [InlineData("unavailable")]
    [InlineData("ambiguous")]
    [InlineData("operation-conflict")]
    [InlineData("dependency-not-found")]
    [InlineData("not-found-existing")]
    [InlineData("lifecycle-conflict-absent")]
    [InlineData("boundary-create-existing")]
    [InlineData("boundary-wrong-revision")]
    [InlineData("boundary-terminal")]
    [InlineData("ceiling-create-existing")]
    [InlineData("ceiling-wrong-revision")]
    [InlineData("ceiling-terminal")]
    public async Task Authenticated_receipt_state_matrix_corruption_fails_closed_after_restart(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new MutableAuthenticatedTrustProvider();
        var store = Store(paths, trust);
        _ = await CreateProfileAsync(store);
        var requiresExistingTarget = scenario is "not-found-existing"
            or "boundary-create-existing"
            or "boundary-wrong-revision"
            or "boundary-terminal"
            or "ceiling-create-existing"
            or "ceiling-wrong-revision"
            or "ceiling-terminal";
        var requiresTerminalTarget = scenario is "boundary-terminal" or "ceiling-terminal";
        var grantId = GrantId(requiresExistingTarget ? "bounded-grant" : "missing-matrix-grant");
        AuthorityGrant? current = null;
        long generation = 1;
        if (requiresExistingTarget)
        {
            var profile = (await store.ReadAsync("default-profile")).Record!;
            current = Grant(profile);
            Assert.Equal(
                AuthorityGrantStoreCommitStatus.Committed,
                (await store.CommitAsync(Mutation(generation++, current, Evidence(current, "matrix-create", AuthorityGrantOperationKind.Create, Hash('1'))))).Status);
            if (requiresTerminalTarget)
            {
                current = Successor(current, AuthorityGrantLifecycleStatus.Revoked);
                Assert.Equal(
                    AuthorityGrantStoreCommitStatus.Committed,
                    (await store.CommitAsync(Mutation(generation++, current, Evidence(current, "matrix-revoke", AuthorityGrantOperationKind.Revoke, Hash('2'))))).Status);
            }
        }

        var baselineReceipt = Receipt(grantId, "matrix-receipt", Hash('3')) with
        {
            ExpectedRevision = current?.Revision.Value ?? 1,
            Outcome = current is null ? AuthorityGrantOperationOutcome.NotFound : AuthorityGrantOperationOutcome.Conflict,
            RecordedAtUtc = _now.AddMinutes(2)
        };
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(new AuthorityGrantStoreMutation(generation, null, baselineReceipt))).Status);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath))!.AsObject();
        var operation = document["grantOperations"]!.AsArray()[^1]!.AsObject();
        switch (scenario)
        {
            case "invalid":
                operation["outcome"] = "invalid";
                operation["failureCode"] = "invalid-request";
                break;
            case "denied":
                operation["outcome"] = "denied";
                operation["failureCode"] = "authority-denied";
                break;
            case "unavailable":
                operation["outcome"] = "unavailable";
                operation["failureCode"] = "store-unavailable";
                break;
            case "ambiguous":
                operation["outcome"] = "ambiguous";
                operation["failureCode"] = "store-ambiguous";
                break;
            case "operation-conflict":
                operation["outcome"] = "conflict";
                operation["failureCode"] = "operation-conflict";
                break;
            case "dependency-not-found":
                operation["failureCode"] = "profile-unavailable";
                break;
            case "not-found-existing":
                operation["outcome"] = "not-found";
                break;
            case "lifecycle-conflict-absent":
                operation["outcome"] = "conflict";
                break;
            case "boundary-create-existing":
                operation["kind"] = "create";
                operation["expectedRevision"] = 0;
                operation["failureCode"] = "boundary-conflict";
                break;
            case "boundary-wrong-revision":
                operation["kind"] = "replace";
                operation["expectedRevision"] = 2;
                operation["failureCode"] = "boundary-conflict";
                break;
            case "boundary-terminal":
                operation["kind"] = "replace";
                operation["failureCode"] = "boundary-conflict";
                break;
            case "ceiling-create-existing":
                operation["kind"] = "create";
                operation["expectedRevision"] = 0;
                operation["failureCode"] = "ceiling-exceeded";
                operation["dependencyEvidenceHash"] = Hash('f');
                break;
            case "ceiling-wrong-revision":
                operation["expectedRevision"] = 2;
                operation["failureCode"] = "ceiling-exceeded";
                operation["dependencyEvidenceHash"] = Hash('f');
                break;
            default:
                operation["failureCode"] = "ceiling-exceeded";
                operation["dependencyEvidenceHash"] = Hash('f');
                break;
        }

        await ReplaceWithAuthenticatedDocumentAsync(paths, trust, document);

        var read = await Store(paths, trust).ReadAsync(grantId);
        var replay = await Store(paths, trust).ReadForMutationAsync(grantId, baselineReceipt.OperationId, baselineReceipt.RequestHash);

        Assert.Equal(AuthorityGrantStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.Snapshot);
        Assert.Equal(AuthorityGrantStoreReadStatus.Unavailable, replay.Status);
        Assert.Null(replay.ExistingOperation);
    }

    [Fact]
    public async Task Receipt_state_is_validated_at_its_historical_revision_not_the_later_terminal_head()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(1, grant, Evidence(grant, "historical-create", AuthorityGrantOperationKind.Create, Hash('1'))))).Status);
        var receipt = Receipt(grant.GrantId, "historical-boundary-conflict", Hash('2')) with
        {
            Kind = AuthorityGrantOperationKind.Replace,
            Outcome = AuthorityGrantOperationOutcome.Conflict,
            FailureCode = AuthorityGrantOperationFailureCode.BoundaryConflict,
            RecordedAtUtc = _now.AddSeconds(30)
        };
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(new AuthorityGrantStoreMutation(2, null, receipt))).Status);
        var revoked = Successor(grant, AuthorityGrantLifecycleStatus.Revoked);
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(3, revoked, Evidence(revoked, "historical-revoke", AuthorityGrantOperationKind.Revoke, Hash('3'))))).Status);

        var restarted = await Store(paths).ReadForMutationAsync(grant.GrantId, receipt.OperationId, receipt.RequestHash);
        var current = await Store(paths).ReadAsync(grant.GrantId);

        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, restarted.Status);
        Assert.Equal(AuthorityGrantLifecycleStatus.Active, restarted.Snapshot!.CurrentGrant.Status);
        Assert.Equal(receipt, restarted.ExistingOperation!.Evidence);
        Assert.Equal(2, restarted.Snapshot.Operations.Count);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, current.Status);
        Assert.Equal(AuthorityGrantLifecycleStatus.Revoked, current.Snapshot!.CurrentGrant.Status);
        Assert.Equal(3, current.Snapshot.Operations.Count);
    }

    [Fact]
    public async Task Oversized_receipt_revision_is_rejected_without_poisoning_restart_or_later_grant_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        var hostile = Receipt(grant.GrantId, "oversized-revision-receipt", Hash('7')) with
        {
            ExpectedRevision = (long)int.MaxValue + 1
        };

        var rejected = await store.CommitAsync(new AuthorityGrantStoreMutation(1, null, hostile));
        var afterRestart = await Store(paths).ReadForMutationAsync(grant.GrantId, hostile.OperationId, hostile.RequestHash);
        var valid = await Store(paths).CommitAsync(Mutation(1, grant, Evidence(grant, "valid-after-oversized-receipt", AuthorityGrantOperationKind.Create, Hash('8'))));
        var read = await Store(paths).ReadAsync(grant.GrantId);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Unavailable, rejected.Status);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, afterRestart.Status);
        Assert.Null(afterRestart.ExistingOperation);
        Assert.Equal(1, afterRestart.StoreGeneration);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, valid.Status);
        Assert.Equal(grant, read.Snapshot!.CurrentGrant);
        Assert.Single(read.Snapshot.Operations);
        Assert.Equal("valid-after-oversized-receipt", read.Snapshot.Operations[0].OperationId);
    }

    [Fact]
    public async Task Hostile_store_create_cannot_commit_a_non_active_first_revision()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var profile = await CreateProfileAsync(store);
        var suspended = Grant(profile, AuthorityGrantLifecycleStatus.Suspended);
        var mutation = Mutation(1, suspended, Evidence(suspended, "hostile-suspended-create", AuthorityGrantOperationKind.Create, Hash('8')));

        var result = await store.CommitAsync(mutation);
        var read = await store.ReadAsync(suspended.GrantId);

        Assert.Equal(AuthorityGrantStoreCommitStatus.StoreConflict, result.Status);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, read.Status);
    }

    [Fact]
    public async Task Commit_atomically_rechecks_the_exact_current_active_profile_pin()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var profile = await CreateProfileAsync(store);
        var suspendedProfile = profile.CurrentProfile with { Revision = ProfileRevision(2), Status = AuthorityProfileStatus.Suspended };
        Assert.Equal(
            AuthorityProfileMutationStatus.Applied,
            (await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.Revise, "suspend-profile", 1, suspendedProfile, null, null, Actor(), Reason()))).Status);
        var suspendedRead = await store.ReadAsync(profile.ProfileId.Value);
        var grant = Grant(suspendedRead.Record!);

        var result = await store.CommitAsync(Mutation(2, grant, Evidence(grant, "create-with-suspended-profile", AuthorityGrantOperationKind.Create, Hash('9'))));

        Assert.Equal(AuthorityGrantStoreCommitStatus.StoreConflict, result.Status);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, (await store.ReadAsync(grant.GrantId)).Status);
    }

    [Fact]
    public async Task Operation_identifiers_are_workspace_global_across_profiles_and_grants()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var profile = await CreateProfileAsync(store, "shared-operation");
        var grant = Grant(profile);
        var colliding = Mutation(1, grant, Evidence(grant, "shared-operation", AuthorityGrantOperationKind.Create, Hash('a')));

        var readCollision = await store.ReadForMutationAsync(grant.GrantId, "shared-operation", Hash('a'));
        var commitCollision = await store.CommitAsync(colliding);
        var committed = await store.CommitAsync(Mutation(1, grant, Evidence(grant, "grant-operation", AuthorityGrantOperationKind.Create, Hash('b'))));
        var profileCollision = await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.TransitionStatus, "grant-operation", 1, null, profile.ProfileId, AuthorityProfileStatus.Suspended, Actor(), Reason()));

        Assert.Equal(AuthorityGrantStoreReadStatus.OperationConflict, readCollision.Status);
        Assert.Null(readCollision.ExistingOperation);
        Assert.Equal(AuthorityGrantStoreCommitStatus.OperationConflict, commitCollision.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Conflict, profileCollision.Status);
    }

    [Fact]
    public async Task Cross_grant_operation_binding_preserves_the_requested_existing_target_snapshot()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var profile = await CreateProfileAsync(store);
        var requested = Grant(profile);
        var other = Grant(profile, grantId: "other-bounded-grant");
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(1, requested, Evidence(requested, "create-requested-grant", AuthorityGrantOperationKind.Create, Hash('1'))))).Status);
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(2, other, Evidence(other, "operation-owned-by-other", AuthorityGrantOperationKind.Create, Hash('2'))))).Status);

        var read = await store.ReadForMutationAsync(requested.GrantId, "operation-owned-by-other", Hash('2'));
        var requestedSuccessor = Successor(requested, AuthorityGrantLifecycleStatus.Suspended);
        var exactHashCollision = await store.CommitAsync(Mutation(
            3,
            requestedSuccessor,
            Evidence(requestedSuccessor, "operation-owned-by-other", AuthorityGrantOperationKind.Suspend, Hash('2'))));
        var changedHashCollision = await store.CommitAsync(Mutation(
            3,
            requestedSuccessor,
            Evidence(requestedSuccessor, "operation-owned-by-other", AuthorityGrantOperationKind.Suspend, Hash('3'))));

        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, read.Status);
        Assert.Equal(requested, read.Snapshot!.CurrentGrant);
        Assert.Equal(other.GrantId, read.ExistingOperation!.GrantId);
        Assert.Equal(AuthorityGrantStoreCommitStatus.OperationConflict, exactHashCollision.Status);
        Assert.Equal(requested, exactHashCollision.Snapshot!.CurrentGrant);
        Assert.Equal(other.GrantId, exactHashCollision.StoredOperation!.GrantId);
        Assert.Equal(AuthorityGrantStoreCommitStatus.OperationConflict, changedHashCollision.Status);
        Assert.Equal(requested, changedHashCollision.Snapshot!.CurrentGrant);
        Assert.Equal(other.GrantId, changedHashCollision.StoredOperation!.GrantId);
    }

    [Fact]
    public async Task Signed_direct_successor_recovers_exactly_after_trust_anchor_advance_failure()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var normal = Store(paths);
        var profile = await CreateProfileAsync(normal);
        var grant = Grant(profile);
        var mutation = Mutation(1, grant, Evidence(grant, "recover-grant", AuthorityGrantOperationKind.Create, Hash('c')));
        var failingTrust = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };

        var interrupted = await Store(paths, failingTrust).CommitAsync(mutation);
        var recovered = await Store(paths).CommitAsync(mutation);
        var read = await Store(paths).ReadAsync(grant.GrantId);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Replayed, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, read.Status);
        Assert.Equal(grant, read.Snapshot!.CurrentGrant);
        Assert.Single(read.Snapshot.Operations);
    }

    [Theory]
    [InlineData(UnexpectedAdvanceMode.NoOp)]
    [InlineData(UnexpectedAdvanceMode.Stale)]
    [InlineData(UnexpectedAdvanceMode.WrongWorkspace)]
    [InlineData(UnexpectedAdvanceMode.WrongCurrentGeneration)]
    [InlineData(UnexpectedAdvanceMode.WrongCurrentDigest)]
    [InlineData(UnexpectedAdvanceMode.WrongPreviousGeneration)]
    [InlineData(UnexpectedAdvanceMode.WrongPreviousDigest)]
    public async Task Grant_commit_does_not_report_a_candidate_when_advance_returns_an_unproved_successor(UnexpectedAdvanceMode mode)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = await CreateProfileAsync(Store(paths));
        var grant = Grant(profile);
        var mutation = Mutation(1, grant, Evidence(grant, "reject-unproved-grant-successor", AuthorityGrantOperationKind.Create, Hash('e')));
        var unexpectedTrust = new UnexpectedAdvanceTrustProvider(_trustProvider, mode);

        var result = await Store(paths, unexpectedTrust).CommitAsync(mutation);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Ambiguous, result.Status);
        Assert.Equal(0, result.StoreGeneration);
        Assert.Null(result.StoredOperation);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData(UnexpectedAdvanceMode.NoOp)]
    [InlineData(UnexpectedAdvanceMode.Stale)]
    [InlineData(UnexpectedAdvanceMode.WrongWorkspace)]
    [InlineData(UnexpectedAdvanceMode.WrongCurrentGeneration)]
    [InlineData(UnexpectedAdvanceMode.WrongCurrentDigest)]
    [InlineData(UnexpectedAdvanceMode.WrongPreviousGeneration)]
    [InlineData(UnexpectedAdvanceMode.WrongPreviousDigest)]
    public async Task Grant_direct_successor_recovery_does_not_expose_a_candidate_when_advance_returns_an_unproved_successor(UnexpectedAdvanceMode mode)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = await CreateProfileAsync(Store(paths));
        var grant = Grant(profile);
        var mutation = Mutation(1, grant, Evidence(grant, "reject-unproved-grant-recovery", AuthorityGrantOperationKind.Create, Hash('f')));
        var interruptedTrust = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };
        Assert.Equal(AuthorityGrantStoreCommitStatus.Ambiguous, (await Store(paths, interruptedTrust).CommitAsync(mutation)).Status);
        var unexpectedTrust = new UnexpectedAdvanceTrustProvider(_trustProvider, mode);

        var result = await Store(paths, unexpectedTrust).CommitAsync(mutation);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Ambiguous, result.Status);
        Assert.Equal(0, result.StoreGeneration);
        Assert.Null(result.StoredOperation);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Receipt_only_direct_successor_recovers_exactly_after_trust_anchor_advance_failure()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var normal = Store(paths);
        _ = await CreateProfileAsync(normal);
        var grantId = GrantId("missing-recovery-grant");
        var receipt = Receipt(grantId, "recover-missing-receipt", Hash('d'));
        var mutation = new AuthorityGrantStoreMutation(1, null, receipt);
        var failingTrust = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };

        var interrupted = await Store(paths, failingTrust).CommitAsync(mutation);
        var recovered = await Store(paths).CommitAsync(mutation);
        var replayRead = await Store(paths).ReadForMutationAsync(grantId, receipt.OperationId, receipt.RequestHash);
        var current = await Store(paths).ReadAsync(grantId);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Replayed, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
        Assert.Equal(receipt, recovered.StoredOperation!.Evidence);
        Assert.Null(recovered.Snapshot);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, replayRead.Status);
        Assert.Equal(receipt, replayRead.ExistingOperation!.Evidence);
        Assert.Null(replayRead.Snapshot);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, current.Status);
        Assert.Null(current.Snapshot);
    }

    [Fact]
    public async Task External_process_writers_serialize_one_receipt_commit_and_one_exact_replay()
    {
        using var workspace = new TestWorkspace();
        using var signals = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        _ = await CreateProfileAsync(Store(paths));
        var firstResultPath = Path.Combine(signals.RootPath, "first-result.txt");
        var secondResultPath = Path.Combine(signals.RootPath, "second-result.txt");
        using var first = StartCrossProcessHost("commit-receipt", workspace.RootPath, _trustRoot.RootPath, string.Empty, firstResultPath);
        using var second = StartCrossProcessHost("commit-receipt", workspace.RootPath, _trustRoot.RootPath, string.Empty, secondResultPath);

        await WaitForSuccessfulExitAsync(first);
        await WaitForSuccessfulExitAsync(second);
        var outcomes = new[] { await File.ReadAllTextAsync(firstResultPath), await File.ReadAllTextAsync(secondResultPath) };
        var grantId = GrantId("cross-process-missing-grant");
        var receipt = Receipt(grantId, "cross-process-receipt", Hash('1'));
        var read = await Store(paths).ReadForMutationAsync(grantId, receipt.OperationId, receipt.RequestHash);

        Assert.Single(outcomes, outcome => string.Equals(outcome, AuthorityGrantStoreCommitStatus.Committed.ToString(), StringComparison.Ordinal));
        Assert.Single(outcomes, outcome => string.Equals(outcome, AuthorityGrantStoreCommitStatus.Replayed.ToString(), StringComparison.Ordinal));
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, read.Status);
        Assert.Equal(receipt, read.ExistingOperation!.Evidence);
        Assert.Null(read.Snapshot);
    }

    [Theory]
    [InlineData("crash-after-proof", AuthorityGrantStoreCommitStatus.Committed)]
    [InlineData("crash-after-primary", AuthorityGrantStoreCommitStatus.Replayed)]
    [InlineData("crash-after-trust", AuthorityGrantStoreCommitStatus.Replayed)]
    [InlineData("crash-after-result", AuthorityGrantStoreCommitStatus.Replayed)]
    public async Task External_process_loss_at_each_commit_boundary_recovers_exactly_once(
        string mode,
        AuthorityGrantStoreCommitStatus expectedRecoveryStatus)
    {
        using var workspace = new TestWorkspace();
        using var signals = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        _ = await CreateProfileAsync(Store(paths));
        var markerPath = Path.Combine(signals.RootPath, $"{mode}.txt");
        using var process = StartCrossProcessHost(mode, workspace.RootPath, _trustRoot.RootPath, markerPath, string.Empty);
        try
        {
            await WaitForPathAsync(markerPath);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            _ = await process.StandardOutput.ReadToEndAsync();
            _ = await process.StandardError.ReadToEndAsync();
        }

        var grantId = GrantId("cross-process-missing-grant");
        var receipt = Receipt(grantId, "cross-process-receipt", Hash('1'));
        var recovered = await Store(paths).CommitAsync(new AuthorityGrantStoreMutation(1, null, receipt));
        var read = await Store(paths).ReadForMutationAsync(grantId, receipt.OperationId, receipt.RequestHash);

        Assert.Equal(expectedRecoveryStatus, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
        Assert.Equal(receipt, recovered.StoredOperation!.Evidence);
        Assert.Null(recovered.Snapshot);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, read.Status);
        Assert.Equal(receipt, read.ExistingOperation!.Evidence);
        Assert.Null(read.Snapshot);
    }

    [Fact]
    public async Task Cross_process_grant_store_host()
    {
        var mode = Environment.GetEnvironmentVariable(CrossProcessMode);
        if (mode is not ("commit-receipt" or "crash-after-proof" or "crash-after-primary" or "crash-after-trust" or "crash-after-result"))
        {
            return;
        }

        var workspaceRoot = Environment.GetEnvironmentVariable(CrossProcessWorkspace)!;
        var trustRoot = Environment.GetEnvironmentVariable(CrossProcessTrustRoot)!;
        var markerPath = Environment.GetEnvironmentVariable(CrossProcessMarker)!;
        var resultPath = Environment.GetEnvironmentVariable(CrossProcessResult)!;
        var paths = new WorkspacePaths(workspaceRoot);
        var trust = new FileCapabilityCatalogTrustProvider(trustRoot);
        var grantId = GrantId("cross-process-missing-grant");
        var receipt = Receipt(grantId, "cross-process-receipt", Hash('1'));
        var mutation = new AuthorityGrantStoreMutation(1, null, receipt);
        if (mode is "crash-after-proof" or "crash-after-primary")
        {
            var barrier = new ExternalCrashDurabilityBarrier(markerPath, mode == "crash-after-proof" ? 1 : 2);
            _ = await new AuthorityProfileStore(paths, trust, new FixedTimeProvider(_now), barrier).CommitAsync(mutation);
            return;
        }

        ICapabilityCatalogTrustProvider effectiveTrust = mode == "crash-after-trust"
            ? new ExternalCrashAfterAdvanceTrustProvider(trust, markerPath)
            : trust;
        var result = await new AuthorityProfileStore(paths, effectiveTrust, new FixedTimeProvider(_now)).CommitAsync(mutation);
        if (mode == "crash-after-result")
        {
            await File.WriteAllTextAsync(markerPath, result.Status.ToString());
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
    }

    [Fact]
    public async Task Concurrent_same_operation_has_one_commit_and_one_exact_replay()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = await CreateProfileAsync(Store(paths));
        var grant = Grant(profile);
        var mutation = Mutation(1, grant, Evidence(grant, "concurrent-grant", AuthorityGrantOperationKind.Create, Hash('d')));
        var first = Task.Run(() => Store(paths).CommitAsync(mutation));
        var second = Task.Run(() => Store(paths).CommitAsync(mutation));

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, value => value.Status == AuthorityGrantStoreCommitStatus.Committed);
        Assert.Single(results, value => value.Status == AuthorityGrantStoreCommitStatus.Replayed);
        var read = await Store(paths).ReadAsync(grant.GrantId);
        Assert.Single(read.Snapshot!.Revisions);
        Assert.Single(read.Snapshot.Operations);
    }

    [Fact]
    public async Task Paused_profile_change_wins_before_grant_commit_and_forces_revalidation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = await CreateProfileAsync(Store(paths));
        var grant = Grant(profile);
        var mutation = Mutation(1, grant, Evidence(grant, "grant-after-profile-race", AuthorityGrantOperationKind.Create, Hash('0')));
        var authority = new CapabilityAuthorityTransaction(paths);
        var retained = await authority.AcquireValidatedLeaseAsync(_ => Task.FromResult(true));
        Assert.NotNull(retained);
        var profileProbe = new ProbingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var delayedGrantAuthority = new DelayedCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var profileStore = new AuthorityProfileStore(paths, _trustProvider, new FixedTimeProvider(_now), authorityTransaction: profileProbe);
        var grantStore = new AuthorityProfileStore(paths, _trustProvider, new FixedTimeProvider(_now), authorityTransaction: delayedGrantAuthority);

        var profileChange = Task.Run(() => profileStore.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.TransitionStatus, "race-profile-suspend", 1, null, profile.ProfileId, AuthorityProfileStatus.Suspended, Actor(), Reason())));
        var grantCommit = Task.Run(() => grantStore.CommitAsync(mutation));
        await Task.WhenAll(profileProbe.Attempted.Task, delayedGrantAuthority.Attempted.Task);
        await retained!.DisposeAsync();
        Assert.Equal(AuthorityProfileMutationStatus.Applied, (await profileChange).Status);
        delayedGrantAuthority.Release();

        Assert.Equal(AuthorityGrantStoreCommitStatus.StoreConflict, (await grantCommit).Status);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, (await Store(paths).ReadAsync(grant.GrantId)).Status);
    }

    [Theory]
    [InlineData(CapabilityAuthorityTransactionFault.CancelAfterCallback)]
    [InlineData(CapabilityAuthorityTransactionFault.IoAfterCallback)]
    public async Task Completed_grant_reads_survive_authority_fence_teardown_failure(
        CapabilityAuthorityTransactionFault fault)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        _ = await CreateProfileAsync(Store(paths));
        var grantId = GrantId("missing-after-authority-callback");
        var transaction = new FaultingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths), fault);
        var store = new AuthorityProfileStore(paths, _trustProvider, new FixedTimeProvider(_now), authorityTransaction: transaction);

        var read = await store.ReadAsync(grantId);
        var mutationRead = await store.ReadForMutationAsync(grantId, "missing-operation", Hash('1'));

        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, read.Status);
        Assert.Equal(1, read.StoreGeneration);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, mutationRead.Status);
        Assert.Equal(1, mutationRead.StoreGeneration);
    }

    [Theory]
    [InlineData(CapabilityAuthorityTransactionFault.CancelAfterCallback)]
    [InlineData(CapabilityAuthorityTransactionFault.IoAfterCallback)]
    public async Task Completed_grant_commit_survives_authority_fence_teardown_failure(
        CapabilityAuthorityTransactionFault fault)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        _ = await CreateProfileAsync(Store(paths));
        var grantId = GrantId("missing-after-commit-callback");
        var receipt = Receipt(grantId, "commit-before-fence-failure", Hash('2'));
        var transaction = new FaultingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths), fault);
        var store = new AuthorityProfileStore(paths, _trustProvider, new FixedTimeProvider(_now), authorityTransaction: transaction);

        var committed = await store.CommitAsync(new AuthorityGrantStoreMutation(1, null, receipt));
        var restarted = await Store(paths).ReadForMutationAsync(grantId, receipt.OperationId, receipt.RequestHash);

        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(receipt, committed.StoredOperation!.Evidence);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, restarted.Status);
        Assert.Equal(receipt, restarted.ExistingOperation!.Evidence);
    }

    [Fact]
    public async Task Authority_cancellation_before_grant_callbacks_propagates_without_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        _ = await CreateProfileAsync(Store(paths));
        var grantId = GrantId("missing-before-authority-callback");
        var receipt = Receipt(grantId, "never-started-operation", Hash('3'));
        var transaction = new FaultingCapabilityAuthorityTransaction(
            new CapabilityAuthorityTransaction(paths),
            CapabilityAuthorityTransactionFault.CancelBeforeCallback);
        var store = new AuthorityProfileStore(paths, _trustProvider, new FixedTimeProvider(_now), authorityTransaction: transaction);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(grantId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadForMutationAsync(grantId, receipt.OperationId, receipt.RequestHash));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitAsync(new AuthorityGrantStoreMutation(1, null, receipt)));

        var unchanged = await Store(paths).ReadForMutationAsync(grantId, receipt.OperationId, receipt.RequestHash);
        Assert.Equal(AuthorityGrantStoreReadStatus.NotFound, unchanged.Status);
        Assert.Null(unchanged.ExistingOperation);
        Assert.Equal(1, unchanged.StoreGeneration);
    }

    [Fact]
    public async Task Null_and_noncanonical_grant_reads_fail_before_authority_access()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var grantId = GrantId("bounded-grant");

        var nullRead = await store.ReadAsync((AuthorityGrantId)null!);
        var invalidMutationRead = await store.ReadForMutationAsync(grantId, ".noncanonical", Hash('4'));

        Assert.Equal(AuthorityGrantStoreReadStatus.Unavailable, nullRead.Status);
        Assert.Equal(0, nullRead.StoreGeneration);
        Assert.Equal(AuthorityGrantStoreReadStatus.Unavailable, invalidMutationRead.Status);
        Assert.Equal(0, invalidMutationRead.StoreGeneration);
        Assert.False(File.Exists(new WorkspacePaths(workspace.RootPath).AuthorityProfilesDocumentPath));
    }

    [Theory]
    [InlineData("ceiling-create-absent")]
    [InlineData("ceiling-narrow-existing")]
    [InlineData("limit")]
    public async Task Supported_receipt_only_dispositions_are_durable_and_replayable(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var existing = scenario == "ceiling-narrow-existing";
        var grantId = GrantId(existing ? "bounded-grant" : $"missing-{scenario}");
        long generation = 1;
        if (existing)
        {
            var grant = Grant(profile);
            Assert.Equal(
                AuthorityGrantStoreCommitStatus.Committed,
                (await store.CommitAsync(Mutation(generation++, grant, Evidence(grant, "create-before-supported-receipt", AuthorityGrantOperationKind.Create, Hash('5'))))).Status);
        }

        var receipt = Receipt(grantId, $"supported-{scenario}", Hash('6')) with { RecordedAtUtc = _now.AddMinutes(2) };
        receipt = scenario switch
        {
            "ceiling-create-absent" => receipt with
            {
                Kind = AuthorityGrantOperationKind.Create,
                ExpectedRevision = 0,
                Outcome = AuthorityGrantOperationOutcome.Conflict,
                FailureCode = AuthorityGrantOperationFailureCode.CeilingExceeded,
                DependencyEvidenceHash = Hash('f')
            },
            "ceiling-narrow-existing" => receipt with
            {
                Outcome = AuthorityGrantOperationOutcome.Conflict,
                FailureCode = AuthorityGrantOperationFailureCode.CeilingExceeded,
                DependencyEvidenceHash = Hash('f')
            },
            _ => receipt with
            {
                Outcome = AuthorityGrantOperationOutcome.LimitExceeded,
                FailureCode = AuthorityGrantOperationFailureCode.LimitExceeded
            }
        };

        var committed = await store.CommitAsync(new AuthorityGrantStoreMutation(generation, null, receipt));
        var replayed = await Store(paths).CommitAsync(new AuthorityGrantStoreMutation(generation, null, receipt));

        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(receipt, replayed.StoredOperation!.Evidence);
    }

    [Fact]
    public async Task Failed_direct_successor_reconciliation_is_ambiguous_across_profile_and_grant_reads()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        _ = await CreateProfileAsync(Store(paths));
        var grantId = GrantId("pending-successor-grant");
        var receipt = Receipt(grantId, "pending-successor-receipt", Hash('7'));
        var mutation = new AuthorityGrantStoreMutation(1, null, receipt);
        var interruptedTrust = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };
        Assert.Equal(AuthorityGrantStoreCommitStatus.Ambiguous, (await Store(paths, interruptedTrust).CommitAsync(mutation)).Status);

        var profileTrust = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };
        var profileRead = await new AuthorityProfileStore(paths, profileTrust, new FixedTimeProvider(_now)).ReadAsync("default-profile");
        var grantTrust = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };
        var grantRead = await Store(paths, grantTrust).ReadAsync(grantId);
        var mutationTrust = new FailingCapabilityCatalogTrustProvider(_trustProvider) { FailNextAdvance = true };
        var mutationRead = await Store(paths, mutationTrust).ReadForMutationAsync(grantId, receipt.OperationId, receipt.RequestHash);
        var recovered = await Store(paths).CommitAsync(mutation);

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, profileRead.Status);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ambiguous, grantRead.Status);
        Assert.Equal(0, grantRead.StoreGeneration);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ambiguous, mutationRead.Status);
        Assert.Equal(0, mutationRead.StoreGeneration);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Replayed, recovered.Status);
    }

    [Fact]
    public async Task Last_proved_profile_state_is_ambiguous_for_grants_absent_from_that_generation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(1, grant, Evidence(grant, "create-before-primary-corruption", AuthorityGrantOperationKind.Create, Hash('8'))))).Status);
        await File.WriteAllTextAsync(paths.AuthorityProfilesDocumentPath, "{\"schemaVersion\":1");

        var read = await Store(paths).ReadAsync(grant.GrantId);
        var mutationRead = await Store(paths).ReadForMutationAsync(grant.GrantId, "unretained-operation", Hash('9'));

        Assert.Equal(AuthorityGrantStoreReadStatus.Ambiguous, read.Status);
        Assert.Equal(1, read.StoreGeneration);
        Assert.Null(read.Snapshot);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ambiguous, mutationRead.Status);
        Assert.Equal(1, mutationRead.StoreGeneration);
        Assert.Null(mutationRead.Snapshot);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("truncated")]
    public async Task Duplicate_unknown_and_truncated_grant_ledgers_fail_closed(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, (await store.CommitAsync(Mutation(1, grant, Evidence(grant, "create-before-corruption", AuthorityGrantOperationKind.Create, Hash('a'))))).Status);
        var original = await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath);
        var corrupted = scenario switch
        {
            "duplicate" => original.Replace("\"grantOperations\": [", "\"grantOperations\": [],\n  \"grantOperations\": [", StringComparison.Ordinal),
            "unknown" => original.Insert(original.IndexOf('{') + 1, "\n  \"unknownGrantField\": true,"),
            _ => "{\"schemaVersion\":1"
        };
        Assert.NotEqual(original, corrupted);
        await File.WriteAllTextAsync(paths.AuthorityProfilesDocumentPath, corrupted);
        await File.WriteAllTextAsync(paths.AuthorityProfilesProofPath, corrupted);

        var read = await Store(paths).ReadAsync(grant.GrantId);

        Assert.Equal(AuthorityGrantStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.Snapshot);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("reason")]
    [InlineData("time")]
    public async Task Authenticated_committed_grant_attribution_corruption_fails_closed(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new MutableAuthenticatedTrustProvider();
        var store = Store(paths, trust);
        var profile = await CreateProfileAsync(store);
        var grant = Grant(profile);
        Assert.Equal(
            AuthorityGrantStoreCommitStatus.Committed,
            (await store.CommitAsync(Mutation(1, grant, Evidence(grant, "create-before-attribution-corruption", AuthorityGrantOperationKind.Create, Hash('1'))))).Status);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath))!.AsObject();
        var operation = document["grantOperations"]![0]!.AsObject();
        switch (scenario)
        {
            case "actor":
                operation["actorId"] = "other-actor";
                break;
            case "reason":
                operation["reason"] = "Different bounded operator attribution.";
                break;
            default:
                operation["recordedAtUtc"] = _now.AddSeconds(1);
                break;
        }

        await ReplaceWithAuthenticatedDocumentAsync(paths, trust, document);

        var read = await Store(paths, trust).ReadAsync(grant.GrantId);

        Assert.Equal(AuthorityGrantStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.Snapshot);
    }

    [Fact]
    public async Task Authenticated_old_shape_without_grant_collections_requires_explicit_cleanup()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var profile = await CreateProfileAsync(store);
        await ReplaceWithAuthenticatedOldShapeAsync(paths);

        var profileRead = await Store(paths).ReadAsync(profile.ProfileId.Value);
        var grantRead = await Store(paths).ReadAsync(GrantId("bounded-grant"));

        Assert.Equal(AuthorityProfileReadStatus.Unavailable, profileRead.Status);
        Assert.Equal(AuthorityGrantStoreReadStatus.Unavailable, grantRead.Status);
    }

    private AuthorityProfileStore Store(WorkspacePaths paths, ICapabilityCatalogTrustProvider? trustProvider = null)
        => new(paths, trustProvider ?? _trustProvider, new FixedTimeProvider(_now));

    private async Task<AuthorityProfileRecord> CreateProfileAsync(AuthorityProfileStore store, string operationId = "create-profile")
    {
        var profile = Profile();
        var result = await store.MutateAsync(new AuthorityProfileMutation(AuthorityProfileMutationKind.Create, operationId, 0, profile, null, null, Actor(), Reason()));
        Assert.Equal(AuthorityProfileMutationStatus.Applied, result.Status);
        return result.Record!;
    }

    private async Task ReplaceWithAuthenticatedOldShapeAsync(WorkspacePaths paths)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(paths.AuthorityProfilesDocumentPath))!.AsObject();
        document.Remove("grants");
        document.Remove("grantOperations");
        document["generation"] = document["generation"]!.GetValue<long>() + 1;
        document["contentDigest"] = string.Empty;
        document["authenticationTag"] = string.Empty;
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, _jsonOptions))).Value;
        var identity = document["workspaceIdentity"]!.GetValue<string>();
        var generation = document["generation"]!.GetValue<long>();
        document["contentDigest"] = digest;
        document["authenticationTag"] = await _trustProvider.AuthenticateArtifactAsync(identity, generation, digest);
        var trust = await _trustProvider.ReadAsync(identity);
        Assert.NotNull(trust);
        _ = await _trustProvider.AdvanceAsync(identity, trust!.CurrentGeneration, trust.CurrentContentDigest, generation, digest);
        var json = JsonSerializer.Serialize(document, _jsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(paths.AuthorityProfilesDocumentPath, json);
        await File.WriteAllTextAsync(paths.AuthorityProfilesProofPath, json);
    }

    private static async Task ReplaceWithAuthenticatedDocumentAsync(
        WorkspacePaths paths,
        MutableAuthenticatedTrustProvider trust,
        JsonObject document)
    {
        document["contentDigest"] = string.Empty;
        document["authenticationTag"] = string.Empty;
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, _jsonOptions))).Value;
        var identity = document["workspaceIdentity"]!.GetValue<string>();
        var generation = document["generation"]!.GetValue<long>();
        document["contentDigest"] = digest;
        document["authenticationTag"] = MutableAuthenticatedTrustProvider.AuthenticationTag;
        trust.SetCurrent(identity, generation, digest);
        var json = JsonSerializer.Serialize(document, _jsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(paths.AuthorityProfilesDocumentPath, json);
        await File.WriteAllTextAsync(paths.AuthorityProfilesProofPath, json);
    }

    private static AuthorityGrantStoreMutation Mutation(long generation, AuthorityGrant grant, AuthorityGrantOperationEvidence evidence)
        => new(generation, grant, evidence);

    private static AuthorityGrantOperationEvidence Evidence(
        AuthorityGrant grant,
        string operationId,
        AuthorityGrantOperationKind kind,
        string requestHash)
        => new(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            operationId,
            requestHash,
            kind,
            AuthorityGrantOperationOutcome.Committed,
            AuthorityGrantOperationFailureCode.None,
            grant.GrantId,
            grant.Revision.Value - 1L,
            new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
            grant.ChangedByActorId,
            grant.Reason,
            Hash('e'),
            kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace ? Hash('f') : null,
            grant.RecordedAtUtc);

    private static AuthorityGrantOperationEvidence Receipt(AuthorityGrantId grantId, string operationId, string requestHash)
        => new(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            operationId,
            requestHash,
            AuthorityGrantOperationKind.Narrow,
            AuthorityGrantOperationOutcome.NotFound,
            AuthorityGrantOperationFailureCode.LifecycleConflict,
            grantId,
            1,
            null,
            Actor(),
            Reason(),
            Hash('e'),
            null,
            _now);

    private static AuthorityGrant Grant(
        AuthorityProfileRecord profile,
        AuthorityGrantLifecycleStatus status = AuthorityGrantLifecycleStatus.Active,
        string grantId = "bounded-grant")
    {
        var binding = Binding(profile);
        return AuthorityGrantHash.Apply(new AuthorityGrant(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            GrantId(grantId),
            GrantRevision(1),
            null,
            null,
            status,
            binding,
            Ceiling(),
            new AuthorityGrantBoundary(_now.AddMinutes(-5), _now.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            Actor(),
            Reason(),
            _now,
            string.Empty));
    }

    private static AuthorityGrant Successor(AuthorityGrant current, AuthorityGrantLifecycleStatus status)
        => AuthorityGrantHash.Apply(current with
        {
            Revision = GrantRevision(current.Revision.Value + 1),
            PredecessorRevision = current.Revision,
            PredecessorContentHash = current.ContentHash,
            Status = status,
            RecordedAtUtc = current.RecordedAtUtc.AddMinutes(1),
            ContentHash = string.Empty
        });

    private static AuthorityGrantBinding Binding(AuthorityProfileRecord profile)
    {
        var profilePin = new AuthorityGrantProfilePin(new AuthorityProfileReference(profile.ProfileId, profile.CurrentProfile.Revision), profile.CurrentHash);
        var rolePin = new AuthorityGrantRolePin(new ContextualRoleRevisionIdentity("bounded-helper", 1), Hash('1'));
        var loopRevision = GovernedLoopRevisionReference.Create(1, "bounded-loop", "revision-1", Hash('2'));
        var loopPin = GovernedLoopRevisionPublicationPinFactory.Create(1, loopRevision, "publish-loop", Hash('3'));
        return new AuthorityGrantBinding(profilePin, rolePin, loopPin);
    }

    private static AuthorityProfile Profile()
        => new(
            AuthorityProfile.CurrentSchemaVersion,
            ProfileId("default-profile"),
            ProfileRevision(1),
            AuthorityProfileStatus.Active,
            Purpose("Bound one governed loop to exact non-self-granting authority."),
            new AuthorityProvenance(Actor(), AuthorityProvenanceKind.UserDeclaration),
            _now.AddHours(-1),
            _now.AddHours(2),
            Ceiling(),
            []);

    private static AuthorityCeiling Ceiling()
        => new([], [], 0, CapabilitySideEffectClass.None, false, false, false);

    private static AuthorityGrantId GrantId(string value)
    {
        Assert.True(AuthorityGrantId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static AuthorityGrantRevision GrantRevision(int value)
    {
        Assert.True(AuthorityGrantRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parsed, out _));
        return parsed!;
    }

    private static AuthorityProfileId ProfileId(string value)
    {
        Assert.True(AuthorityProfileId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static AuthorityProfileRevision ProfileRevision(int value)
    {
        Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parsed, out _));
        return parsed!;
    }

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("user-owner", out var parsed, out _));
        return parsed!;
    }

    private static AuthorityPurpose Reason()
    {
        Assert.True(AuthorityPurpose.TryParse("Delegate bounded work for one exact governed loop revision.", out var parsed, out _));
        return parsed!;
    }

    private static AuthorityPurpose Purpose(string value)
    {
        Assert.True(AuthorityPurpose.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static string Hash(char value) => new(value, 64);

    private static Process StartCrossProcessHost(
        string mode,
        string workspaceRoot,
        string trustRoot,
        string markerPath,
        string resultPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        EmbodySense.Core.Persistence.Tests.Verification.CoverageChildProcessAssembly.AddVstestArguments(
            startInfo,
            typeof(AuthorityGrantStoreTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Authority.AuthorityGrantStoreTests.Cross_process_grant_store_host");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessMode] = mode;
        startInfo.Environment[CrossProcessWorkspace] = workspaceRoot;
        startInfo.Environment[CrossProcessTrustRoot] = trustRoot;
        startInfo.Environment[CrossProcessMarker] = markerPath;
        startInfo.Environment[CrossProcessResult] = resultPath;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The authority-grant store child process did not start.");
    }

    private static async Task WaitForSuccessfulExitAsync(Process process)
    {
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, $"Authority-grant store child failed with exit code {process.ExitCode}. stdout: {output} stderr: {error}");
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(30), $"Authority-grant store child did not publish `{path}`.");
            await Task.Delay(10);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class ExternalCrashDurabilityBarrier(string markerPath, int targetFlushCount) : ICapabilityCatalogDurabilityBarrier
    {
        private int _flushCount;

        public void BeforeDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void AfterDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
        {
        }

        public async ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
        {
            if (Interlocked.Increment(ref _flushCount) != targetFlushCount)
            {
                return;
            }

            await File.WriteAllTextAsync(markerPath, destinationPath);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class ExternalCrashAfterAdvanceTrustProvider(
        ICapabilityCatalogTrustProvider inner,
        string markerPath) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

        public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
            => inner.ReadAsync(workspaceIdentity, cancellationToken);

        public Task<CapabilityCatalogTrustState> InitializeAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<string> AuthenticateArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<bool> VerifyArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            string authenticationTag,
            CancellationToken cancellationToken = default)
            => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

        public async Task<CapabilityCatalogTrustState> AdvanceAsync(
            string workspaceIdentity,
            long expectedGeneration,
            string expectedContentDigest,
            long newGeneration,
            string newContentDigest,
            CancellationToken cancellationToken = default)
        {
            var advanced = await inner.AdvanceAsync(
                workspaceIdentity,
                expectedGeneration,
                expectedContentDigest,
                newGeneration,
                newContentDigest,
                cancellationToken);
            await File.WriteAllTextAsync(markerPath, "trust-advanced", cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return advanced;
        }
    }

    private sealed class DelayedCapabilityAuthorityTransaction(ICapabilityAuthorityTransaction inner) : ICapabilityAuthorityTransaction
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            Attempted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.ExecuteAsync(operation, cancellationToken);
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
            => inner.AcquireValidatedLeaseAsync(validator, cancellationToken);
    }

    private sealed class MutableAuthenticatedTrustProvider : ICapabilityCatalogTrustProvider
    {
        internal const string AuthenticationTag = "authenticated-test-artifact";

        private CapabilityCatalogTrustState? _state;

        public int MaximumAuthenticationTagUtf8Bytes => 64;

        public void RequireDisjointWorkspace(string workspaceRootPath)
        {
        }

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
            => Task.FromResult(_state);

        public Task<CapabilityCatalogTrustState> InitializeAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
        {
            _state = new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null);
            return Task.FromResult(_state);
        }

        public Task<string> AuthenticateArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AuthenticationTag);

        public Task<bool> VerifyArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            string authenticationTag,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(authenticationTag, AuthenticationTag, StringComparison.Ordinal));

        public Task<CapabilityCatalogTrustState> AdvanceAsync(
            string workspaceIdentity,
            long expectedGeneration,
            string expectedContentDigest,
            long newGeneration,
            string newContentDigest,
            CancellationToken cancellationToken = default)
        {
            _state = new CapabilityCatalogTrustState(workspaceIdentity, newGeneration, newContentDigest, expectedGeneration, expectedContentDigest);
            return Task.FromResult(_state);
        }

        internal void SetCurrent(string workspaceIdentity, long generation, string contentDigest)
            => _state = new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null);
    }
}
