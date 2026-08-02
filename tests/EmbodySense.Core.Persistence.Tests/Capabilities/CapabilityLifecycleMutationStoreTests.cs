using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityLifecycleMutationStoreTests
{
    [Fact]
    public async Task Read_only_composition_refuses_mutation_and_mutable_composition_requires_authority_ports()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var baselineSource = new StubCapabilityLifecycleBaselineSource();
        var evidence = new StubCapabilityLifecycleArtifactEvidenceSource();
        var mutable = new CapabilityLifecycleMutationStore(paths, trust, baselineSource, evidence);
        var snapshot = await new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]).CaptureAsync();
        var request = new CapabilityLifecyclePreviewRequest("read-only-refusal", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        var preview = await mutable.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), snapshot);
        var readOnly = new CapabilityLifecycleMutationStore(paths, trust);

        Assert.Equal(CapabilityLifecyclePreviewStatus.Unavailable, (await readOnly.PreviewAsync(request with { OperationId = "read-only-preview" }, CapabilityLifecycleTestData.Baseline(), snapshot)).Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Unavailable, (await readOnly.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), snapshot)).Status);
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleMutationStore(paths, trust, null!, evidence));
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleMutationStore(paths, trust, baselineSource, null!));
    }

    [Fact]
    public async Task Upgrade_preserves_compatible_required_dependents_and_records_optional_degradation_and_history()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var source = new StubCapabilityDependentIndexSource { Dependents = [CapabilityLifecycleTestData.Dependent("required-loop", CapabilityRequirementKind.Required, "[2.0.0]"), CapabilityLifecycleTestData.Dependent("optional-skill", CapabilityRequirementKind.Optional, "[1.0.0]", CapabilityDependentKind.Skill)] };
        var index = new CapabilityDependentIndex([source]);
        var store = Store(paths);
        var request = new CapabilityLifecyclePreviewRequest("upgrade-v2", CapabilityLifecycleOperationKind.Upgrade, CapabilityLifecycleTestData.Descriptor().Id, CapabilityLifecycleTestData.Descriptor("2.0.0"), CapabilityLifecycleTestData.Digest("artifact-v2"));

        var preview = await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var applied = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var replayed = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var read = await store.ReadAsync(request.CapabilityId);

        Assert.True(preview.Status == CapabilityLifecyclePreviewStatus.Ready, preview.Detail);
        Assert.Equal([CapabilityLifecycleImpactOutcome.Preserved, CapabilityLifecycleImpactOutcome.Degraded], preview.Impacts.Select(impact => impact.Outcome));
        Assert.DoesNotContain(preview.Impacts, impact => impact.Outcome == CapabilityLifecycleImpactOutcome.Blocked);
        Assert.True(applied.Status == CapabilityLifecycleMutationStatus.Applied, applied.Detail);
        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, replayed.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, replayed.ReplayedOutcome);
        Assert.True(replayed.OutcomeAuditPending);
        Assert.Equal("2.0.0", read.State!.Descriptor.Version.Value);
        Assert.Equal(CapabilityLifecycleTestData.Baseline().State.ArtifactDigest, Assert.Single(read.History).ArtifactDigest);
        Assert.Equal("optional-skill", Assert.Single(read.Degradations).DependentIdentity);
        Assert.Equal(CapabilityLifecycleAuditMarkStatus.Applied, await store.MarkOutcomeAuditedAsync(request.OperationId));
        Assert.Equal(CapabilityLifecycleAuditMarkStatus.NoChange, await store.MarkOutcomeAuditedAsync(request.OperationId));
        Assert.False((await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync())).OutcomeAuditPending);
    }

    [Fact]
    public async Task Required_dependency_blocks_disable_while_optional_dependency_degrades_visibly()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var source = new StubCapabilityDependentIndexSource { Dependents = [CapabilityLifecycleTestData.Dependent("required-loop", CapabilityRequirementKind.Required, "*"), CapabilityLifecycleTestData.Dependent("optional-loop", CapabilityRequirementKind.Optional, "*")] };
        var index = new CapabilityDependentIndex([source]);
        var store = Store(paths);
        var request = new CapabilityLifecyclePreviewRequest("disable-v1", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);

        var preview = await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var result = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var replayed = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var read = await store.ReadAsync(request.CapabilityId);

        Assert.Contains(preview.Impacts, impact => impact.Outcome == CapabilityLifecycleImpactOutcome.Blocked);
        Assert.Equal(CapabilityLifecycleMutationStatus.Blocked, result.Status);
        Assert.True(read.State!.IsEnabled);
        Assert.Empty(read.History);
        Assert.Empty(read.Degradations);
        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, replayed.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Blocked, replayed.ReplayedOutcome);
    }

    [Fact]
    public async Task Concurrent_dependent_creation_invalidates_preview_and_preserves_prior_state()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var source = new StubCapabilityDependentIndexSource();
        var index = new CapabilityDependentIndex([source]);
        var store = Store(paths);
        var request = new CapabilityLifecyclePreviewRequest("remove-v1", CapabilityLifecycleOperationKind.Remove, CapabilityLifecycleTestData.Descriptor().Id);
        var preview = await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        source.Dependents = [CapabilityLifecycleTestData.Dependent("late-required", CapabilityRequirementKind.Required, "*")];

        var result = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var read = await store.ReadAsync(request.CapabilityId);

        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, result.Status);
        Assert.False(read.State!.IsRemoved);
        Assert.Empty(read.History);
        var replayed = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, replayed.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, replayed.ReplayedOutcome);
    }

    [Fact]
    public async Task Catalog_or_activation_baseline_drift_conflicts_against_preview_bound_revisions()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var baselineSource = new StubCapabilityLifecycleBaselineSource();
        var store = Store(paths, baselineSource);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var baseline = CapabilityLifecycleTestData.Baseline();
        var request = new CapabilityLifecyclePreviewRequest("baseline-bound-disable", CapabilityLifecycleOperationKind.Disable, baseline.State.Descriptor.Id);
        var preview = await store.PreviewAsync(request, baseline, await index.CaptureAsync());
        baselineSource.Baseline = baseline with { CatalogRevision = baseline.CatalogRevision + 1 };

        var conflict = await store.MutateAsync(preview, baselineSource.Baseline, await index.CaptureAsync());
        var read = await store.ReadAsync(request.CapabilityId);

        Assert.Equal(baseline.CatalogRevision, preview.BaselineCatalogRevision);
        Assert.Equal(baseline.ActivationRevision, preview.BaselineActivationRevision);
        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, conflict.Status);
        Assert.True(read.State!.IsEnabled);
        Assert.Empty(read.History);
        var replayed = await store.MutateAsync(preview, null, new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Unavailable, string.Empty, [], "unavailable"));
        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, replayed.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, replayed.ReplayedOutcome);
    }

    [Fact]
    public async Task Upgrade_and_rollback_reprove_before_first_mutation_but_terminal_replay_survives_evidence_loss()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var evidence = new StubCapabilityLifecycleArtifactEvidenceSource();
        var store = Store(paths, artifactEvidenceSource: evidence);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var baseline = CapabilityLifecycleTestData.Baseline();
        var id = baseline.State.Descriptor.Id;
        var upgradeRequest = new CapabilityLifecyclePreviewRequest("reprove-upgrade", CapabilityLifecycleOperationKind.Upgrade, id, CapabilityLifecycleTestData.Descriptor("2.0.0"), CapabilityLifecycleTestData.Digest("artifact-v2"));
        var upgrade = await store.PreviewAsync(upgradeRequest, baseline, await index.CaptureAsync());
        var generationBeforeUpgrade = (await store.ReadAsync(id)).LifecycleRevision;
        evidence.Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.NotFound, "deleted");

        Assert.Equal(CapabilityLifecycleMutationStatus.NotFound, (await store.MutateAsync(upgrade, baseline, await index.CaptureAsync())).Status);
        Assert.Equal(generationBeforeUpgrade, (await store.ReadAsync(id)).LifecycleRevision);
        evidence.Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.Proved, "restored");
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await store.MutateAsync(upgrade, baseline, await index.CaptureAsync())).Status);
        var unavailableDependents = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Unavailable, string.Empty, [], "unavailable");
        var verificationsBeforeUpgradeReplay = evidence.Verifications;
        evidence.Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.NotFound, "deleted-after-upgrade");
        var recoveredUpgrade = await store.PreviewAsync(upgradeRequest, null, unavailableDependents);
        var upgradeReplay = await store.MutateAsync(recoveredUpgrade, null, unavailableDependents);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Replayed, recoveredUpgrade.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, upgradeReplay.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, upgradeReplay.ReplayedOutcome);
        Assert.True(upgradeReplay.OutcomeAuditPending);
        Assert.Equal(verificationsBeforeUpgradeReplay, evidence.Verifications);

        evidence.Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.Proved, "restored");
        var rollbackRequest = new CapabilityLifecyclePreviewRequest("reprove-rollback", CapabilityLifecycleOperationKind.Rollback, id);
        var rollback = await store.PreviewAsync(rollbackRequest, baseline, await index.CaptureAsync());
        var generationBeforeRollback = (await store.ReadAsync(id)).LifecycleRevision;
        evidence.Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.NotFound, "withdrawn");
        Assert.Equal(CapabilityLifecycleMutationStatus.NotFound, (await store.MutateAsync(rollback, baseline, await index.CaptureAsync())).Status);
        Assert.Equal(generationBeforeRollback, (await store.ReadAsync(id)).LifecycleRevision);
        evidence.Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.Proved, "restored");
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await store.MutateAsync(rollback, baseline, await index.CaptureAsync())).Status);
        var verificationsBeforeRollbackReplay = evidence.Verifications;
        evidence.Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.Unavailable, "unavailable-after-rollback");
        var recoveredRollback = await store.PreviewAsync(rollbackRequest, null, unavailableDependents);
        var rollbackReplay = await store.MutateAsync(recoveredRollback, null, unavailableDependents);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Replayed, recoveredRollback.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, rollbackReplay.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, rollbackReplay.ReplayedOutcome);
        Assert.True(rollbackReplay.OutcomeAuditPending);
        Assert.Equal(verificationsBeforeRollbackReplay, evidence.Verifications);
        Assert.Equal("1.0.0", (await store.ReadAsync(id)).State!.Descriptor.Version.Value);
    }

    [Fact]
    public async Task Upgrade_then_rollback_restores_immediately_prior_proved_descriptor_and_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var store = Store(paths);
        var id = CapabilityLifecycleTestData.Descriptor().Id;
        var upgradeRequest = new CapabilityLifecyclePreviewRequest("upgrade-for-rollback", CapabilityLifecycleOperationKind.Upgrade, id, CapabilityLifecycleTestData.Descriptor("2.0.0"), CapabilityLifecycleTestData.Digest("artifact-v2"));
        var upgrade = await store.PreviewAsync(upgradeRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await store.MutateAsync(upgrade, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync())).Status);
        var rollbackRequest = new CapabilityLifecyclePreviewRequest("rollback-to-v1", CapabilityLifecycleOperationKind.Rollback, id);

        var rollback = await store.PreviewAsync(rollbackRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var rolledBack = await store.MutateAsync(rollback, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var read = await store.ReadAsync(id);

        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, rolledBack.Status);
        Assert.Equal("1.0.0", read.State!.Descriptor.Version.Value);
        Assert.Equal(CapabilityLifecycleTestData.Digest("artifact-v1"), read.State.ArtifactDigest);
        Assert.Equal(2, read.History.Count);
    }

    [Fact]
    public async Task Removal_retains_tombstone_and_exact_rollback_restores_prior_enablement_while_upgrade_cannot_resurrect()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var store = Store(paths);
        var id = CapabilityLifecycleTestData.Descriptor().Id;
        var removeRequest = new CapabilityLifecyclePreviewRequest("remove-for-rollback", CapabilityLifecycleOperationKind.Remove, id);
        var remove = await store.PreviewAsync(removeRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await store.MutateAsync(remove, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync())).Status);
        var tombstone = await store.ReadAsync(id);
        Assert.True(tombstone.State!.IsRemoved);
        Assert.False(tombstone.State.IsEnabled);
        var upgradeRequest = new CapabilityLifecyclePreviewRequest("resurrect-upgrade", CapabilityLifecycleOperationKind.Upgrade, id, CapabilityLifecycleTestData.Descriptor("2.0.0"), CapabilityLifecycleTestData.Digest("v2"));
        Assert.Equal(CapabilityLifecyclePreviewStatus.Invalid, (await store.PreviewAsync(upgradeRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync())).Status);
        var rollbackRequest = new CapabilityLifecyclePreviewRequest("rollback-removal", CapabilityLifecycleOperationKind.Rollback, id);
        var rollback = await store.PreviewAsync(rollbackRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());

        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await store.MutateAsync(rollback, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync())).Status);
        var restored = await store.ReadAsync(id);
        Assert.False(restored.State!.IsRemoved);
        Assert.True(restored.State.IsEnabled);
    }

    [Theory]
    [InlineData(CapabilityLifecycleOperationKind.Disable)]
    [InlineData(CapabilityLifecycleOperationKind.Remove)]
    public async Task Repeated_rollback_blocks_required_dependents_when_it_would_restore_a_disabled_or_removed_state(CapabilityLifecycleOperationKind priorOperation)
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var source = new StubCapabilityDependentIndexSource();
        var index = new CapabilityDependentIndex([source]);
        var store = Store(paths);
        var id = CapabilityLifecycleTestData.Descriptor().Id;
        var baseline = CapabilityLifecycleTestData.Baseline();
        var prior = new CapabilityLifecyclePreviewRequest($"{priorOperation.ToString().ToLowerInvariant()}-for-rollback", priorOperation, id);

        var initial = await store.PreviewAsync(prior, baseline, await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await store.MutateAsync(initial, baseline, await index.CaptureAsync())).Status);
        source.Dependents = [CapabilityLifecycleTestData.Dependent("required-loop", CapabilityRequirementKind.Required, "[1.0.0]")];

        var restore = await store.PreviewAsync(new CapabilityLifecyclePreviewRequest($"restore-{priorOperation.ToString().ToLowerInvariant()}", CapabilityLifecycleOperationKind.Rollback, id), baseline, await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleImpactOutcome.Preserved, Assert.Single(restore.Impacts).Outcome);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await store.MutateAsync(restore, baseline, await index.CaptureAsync())).Status);

        var rollback = await store.PreviewAsync(new CapabilityLifecyclePreviewRequest($"rollback-{priorOperation.ToString().ToLowerInvariant()}", CapabilityLifecycleOperationKind.Rollback, id), baseline, await index.CaptureAsync());
        var blocked = await store.MutateAsync(rollback, baseline, await index.CaptureAsync());
        var state = await store.ReadAsync(id);

        Assert.Equal(CapabilityLifecycleImpactOutcome.Blocked, Assert.Single(rollback.Impacts).Outcome);
        Assert.Equal(CapabilityLifecycleMutationStatus.Blocked, blocked.Status);
        Assert.True(state.State!.IsEnabled);
        Assert.False(state.State.IsRemoved);
    }

    [Fact]
    public async Task Preview_is_bound_to_physical_workspace_and_operation_identity()
    {
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        var first = Store(Prepare(firstWorkspace));
        var second = Store(Prepare(secondWorkspace));
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var request = new CapabilityLifecyclePreviewRequest("workspace-bound", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        var preview = await first.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        _ = await second.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());

        var forged = await second.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var reused = await first.PreviewAsync(request with { Kind = CapabilityLifecycleOperationKind.Remove }, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());

        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, forged.Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Conflict, reused.Status);
    }

    [Fact]
    public async Task Unavailable_or_forged_dependents_fail_closed_before_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var source = new StubCapabilityDependentIndexSource { Failure = new IOException("unavailable") };
        var index = new CapabilityDependentIndex([source]);
        var request = new CapabilityLifecyclePreviewRequest("fail-closed", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        var store = Store(paths);

        Assert.Equal(CapabilityLifecyclePreviewStatus.Unavailable, (await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync())).Status);
        source.Failure = null;
        source.Dependents = [CapabilityLifecycleTestData.Dependent("duplicate", CapabilityRequirementKind.Required, "*"), CapabilityLifecycleTestData.Dependent("duplicate", CapabilityRequirementKind.Optional, "*")];
        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, (await index.CaptureAsync()).Status);
        Assert.Equal(CapabilityLifecycleReadStatus.NotFound, (await store.ReadAsync(request.CapabilityId)).Status);
    }

    [Fact]
    public async Task Interrupted_candidate_commit_converges_and_remains_mutation_resumable()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var baselineSource = new StubCapabilityLifecycleBaselineSource();
        var artifactEvidence = new StubCapabilityLifecycleArtifactEvidenceSource();
        var normal = new CapabilityLifecycleMutationStore(paths, trust, baselineSource, artifactEvidence);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var firstRequest = new CapabilityLifecyclePreviewRequest("seed-preview", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        _ = await normal.PreviewAsync(firstRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var barrier = new FailingCapabilityLifecycleDurabilityBarrier { DestinationSuffix = "lifecycle.json" };
        var failing = new CapabilityLifecycleMutationStore(paths, trust, baselineSource, artifactEvidence, durabilityBarrier: barrier);
        var secondRequest = new CapabilityLifecyclePreviewRequest("interrupted-preview", CapabilityLifecycleOperationKind.Remove, firstRequest.CapabilityId);

        var interrupted = await failing.PreviewAsync(secondRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var recovered = await normal.ReadAsync(firstRequest.CapabilityId);
        var resumed = await normal.PreviewAsync(secondRequest, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var applied = await normal.MutateAsync(resumed, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var current = await normal.ReadAsync(firstRequest.CapabilityId);

        Assert.Equal(CapabilityLifecyclePreviewStatus.Unavailable, interrupted.Status);
        Assert.Equal(CapabilityLifecycleReadStatus.RecoveredLastProved, recovered.Status);
        Assert.False(recovered.State!.IsRemoved);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Replayed, resumed.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, applied.Status);
        Assert.Equal(CapabilityLifecycleReadStatus.Available, current.Status);
        Assert.True(current.State!.IsRemoved);
    }

    [Fact]
    public async Task Recovered_proof_predating_first_registration_preserves_unproved_registration_status_and_converges()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var baselineSource = new StubCapabilityLifecycleBaselineSource();
        var artifactEvidence = new StubCapabilityLifecycleArtifactEvidenceSource();
        var barrier = new FailingCapabilityLifecycleDurabilityBarrier { DestinationSuffix = "lifecycle.json" };
        var failing = new CapabilityLifecycleMutationStore(paths, trust, baselineSource, artifactEvidence, durabilityBarrier: barrier);
        var normal = new CapabilityLifecycleMutationStore(paths, trust, baselineSource, artifactEvidence);
        var snapshot = await new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]).CaptureAsync();
        var request = new CapabilityLifecyclePreviewRequest("interrupted-first-registration", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);

        Assert.Equal(CapabilityLifecyclePreviewStatus.Unavailable, (await failing.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), snapshot)).Status);
        var recovered = await normal.ReadAsync(request.CapabilityId);
        Assert.Equal(CapabilityLifecycleReadStatus.RecoveredLastProved, recovered.Status);
        Assert.Null(recovered.State);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Replayed, (await normal.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), snapshot)).Status);
        Assert.Equal(CapabilityLifecycleReadStatus.Available, (await normal.ReadAsync(request.CapabilityId)).Status);
    }

    [Fact]
    public async Task Tampered_aggregate_and_preview_are_rejected_without_using_unkeyed_content()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var store = Store(paths);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var request = new CapabilityLifecyclePreviewRequest("tamper-proof", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        var preview = await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var root = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityLifecycleDocumentPath))!.AsObject();
        root["generation"] = 99;
        await File.WriteAllTextAsync(paths.CapabilityLifecycleDocumentPath, root.ToJsonString());

        var mutation = await store.MutateAsync(preview with { PreviewHash = CapabilityLifecycleTestData.Digest("forged").Value }, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var recovered = await store.ReadAsync(request.CapabilityId);

        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, mutation.Status);
        Assert.Equal(CapabilityLifecycleReadStatus.RecoveredLastProved, recovered.Status);
    }

    [Fact]
    public async Task Mutation_rejects_caller_substitution_of_persisted_revision_and_blocking_impacts()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var source = new StubCapabilityDependentIndexSource { Dependents = [CapabilityLifecycleTestData.Dependent("required-loop", CapabilityRequirementKind.Required, "*")] };
        var index = new CapabilityDependentIndex([source]);
        var store = Store(paths);
        var request = new CapabilityLifecyclePreviewRequest("forged-preview-fields", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        var captured = await index.CaptureAsync();
        var preview = await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), captured);

        var forgedRevision = new CapabilityLifecyclePreview(preview.Status, preview.WorkspaceIdentity, preview.OperationId, preview.Kind, preview.CapabilityId, preview.LifecycleRevision + 1, preview.DependentSetRevision, preview.DependentSetHash, preview.PreviewHash, preview.Impacts, preview.Detail);
        var forgedImpacts = new CapabilityLifecyclePreview(preview.Status, preview.WorkspaceIdentity, preview.OperationId, preview.Kind, preview.CapabilityId, preview.LifecycleRevision, preview.DependentSetRevision, preview.DependentSetHash, preview.PreviewHash, [], preview.Detail);
        var forgedIdentity = preview with { CapabilityId = null! };
        var forgedDependents = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, captured.Hash, [CapabilityLifecycleTestData.Dependent("hidden-dependent", CapabilityRequirementKind.Required, "*"), .. captured.Dependents], "forged");
        var dependentUnavailable = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), forgedDependents);
        var revisionConflict = await store.MutateAsync(forgedRevision, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var impactConflict = await store.MutateAsync(forgedImpacts, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var identityInvalid = await store.MutateAsync(forgedIdentity, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var blocked = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());

        Assert.Equal(CapabilityLifecycleMutationStatus.Unavailable, dependentUnavailable.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, revisionConflict.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Conflict, impactConflict.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Invalid, identityInvalid.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Blocked, blocked.Status);
        Assert.True((await store.ReadAsync(request.CapabilityId)).State!.IsEnabled);
    }

    [Fact]
    public async Task First_registration_normalizes_external_store_revision_into_lifecycle_revision_zero()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var store = Store(paths);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var baseline = CapabilityLifecycleTestData.Baseline() with { State = CapabilityLifecycleTestData.Baseline().State with { Revision = 47 } };
        var request = new CapabilityLifecyclePreviewRequest("normalize-registration-revision", CapabilityLifecycleOperationKind.Upgrade, baseline.State.Descriptor.Id, CapabilityLifecycleTestData.Descriptor("2.0.0"), CapabilityLifecycleTestData.Digest("artifact-v2"));

        var preview = await store.PreviewAsync(request, baseline, await index.CaptureAsync());
        var applied = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), await index.CaptureAsync());
        var read = await store.ReadAsync(request.CapabilityId);

        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, applied.Status);
        Assert.Equal(0, Assert.Single(read.History).Revision);
        Assert.Equal(2, read.State!.Revision);
    }

    [Fact]
    public async Task Closed_request_contract_and_audit_marker_fail_without_partial_state()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var store = Store(paths);
        var snapshot = await new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]).CaptureAsync();
        var id = CapabilityLifecycleTestData.Descriptor().Id;
        var invalidIdentity = await store.PreviewAsync(new CapabilityLifecyclePreviewRequest(string.Empty, CapabilityLifecycleOperationKind.Disable, id), null, snapshot);
        var missingUpgradeTarget = await store.PreviewAsync(new CapabilityLifecyclePreviewRequest("missing-upgrade-target", CapabilityLifecycleOperationKind.Upgrade, id), null, snapshot);
        var unexpectedDisableTarget = await store.PreviewAsync(new CapabilityLifecyclePreviewRequest("unexpected-disable-target", CapabilityLifecycleOperationKind.Disable, id, CapabilityLifecycleTestData.Descriptor(), CapabilityLifecycleTestData.Digest("unexpected")), null, snapshot);
        var unknown = await store.PreviewAsync(new CapabilityLifecyclePreviewRequest("unknown-capability", CapabilityLifecycleOperationKind.Disable, id), null, snapshot);
        var rollback = await store.PreviewAsync(new CapabilityLifecyclePreviewRequest("rollback-without-history", CapabilityLifecycleOperationKind.Rollback, id), CapabilityLifecycleTestData.Baseline(), snapshot);

        Assert.Equal(CapabilityLifecyclePreviewStatus.Invalid, invalidIdentity.Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Invalid, missingUpgradeTarget.Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Invalid, unexpectedDisableTarget.Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.NotFound, unknown.Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.NotFound, rollback.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Invalid, (await store.MutateAsync(invalidIdentity, CapabilityLifecycleTestData.Baseline(), snapshot)).Status);
        Assert.Equal(CapabilityLifecycleAuditMarkStatus.NotFound, await store.MarkOutcomeAuditedAsync(string.Empty));
        Assert.Equal(CapabilityLifecycleAuditMarkStatus.NotFound, await store.MarkOutcomeAuditedAsync("unknown-operation"));
    }

    [Fact]
    public async Task Cancellation_is_propagated_by_every_lifecycle_io_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var store = Store(paths);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var snapshot = await index.CaptureAsync();
        var request = new CapabilityLifecyclePreviewRequest("cancel-lifecycle-io", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        var preview = await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), snapshot);
        var terminal = await store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), snapshot);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, terminal.Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadAsync(request.CapabilityId, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.PreviewAsync(new CapabilityLifecyclePreviewRequest("cancel-preview-io", CapabilityLifecycleOperationKind.Disable, request.CapabilityId), null, snapshot, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), snapshot, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.MarkOutcomeAuditedAsync(request.OperationId, cancellation.Token));
    }

    [Fact]
    public async Task Missing_lifecycle_root_is_structured_as_unavailable_at_every_io_boundary()
    {
        using var missingWorkspace = new TestWorkspace();
        var missing = Store(new WorkspacePaths(missingWorkspace.RootPath));
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var snapshot = await index.CaptureAsync();
        var request = new CapabilityLifecyclePreviewRequest("missing-lifecycle-root", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);

        Assert.Equal(CapabilityLifecycleReadStatus.Unavailable, (await missing.ReadAsync(request.CapabilityId)).Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Unavailable, (await missing.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), snapshot)).Status);
        Assert.Equal(CapabilityLifecycleAuditMarkStatus.Unavailable, await missing.MarkOutcomeAuditedAsync(request.OperationId));

        using var preparedWorkspace = new TestWorkspace();
        var prepared = Store(Prepare(preparedWorkspace));
        var preview = await prepared.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), snapshot);
        Assert.Equal(CapabilityLifecycleMutationStatus.Unavailable, (await missing.MutateAsync(preview, CapabilityLifecycleTestData.Baseline(), snapshot)).Status);
        Assert.True(EmbodySense.Core.Common.Capabilities.CapabilityId.TryParse("org.example/unknown-lifecycle", out var unknownId, out _));
        Assert.Equal(CapabilityLifecycleReadStatus.NotFound, (await prepared.ReadAsync(unknownId!)).Status);
    }

    [Fact]
    public async Task Missing_or_ambiguous_documents_cannot_reconstitute_trusted_lifecycle_state()
    {
        using var workspace = new TestWorkspace();
        var paths = Prepare(workspace);
        var store = Store(paths);
        var snapshot = await new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]).CaptureAsync();
        var request = new CapabilityLifecyclePreviewRequest("seed-document-rejection", CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleTestData.Descriptor().Id);
        _ = await store.PreviewAsync(request, CapabilityLifecycleTestData.Baseline(), snapshot);
        var canonical = await File.ReadAllTextAsync(paths.CapabilityLifecycleDocumentPath);
        var ambiguous = canonical.Replace("\"generation\": 1,", "\"generation\": 1,\n  \"generation\": 1,", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.CapabilityLifecycleDocumentPath, ambiguous);
        Assert.Equal(CapabilityLifecycleReadStatus.RecoveredLastProved, (await store.ReadAsync(request.CapabilityId)).Status);

        File.Delete(paths.CapabilityLifecycleDocumentPath);
        File.Delete(paths.CapabilityLifecycleProofPath);
        Assert.Equal(CapabilityLifecycleReadStatus.Unavailable, (await store.ReadAsync(request.CapabilityId)).Status);
        Assert.Equal(CapabilityLifecyclePreviewStatus.Unavailable, (await store.PreviewAsync(new CapabilityLifecyclePreviewRequest("reject-abandoned-trust", CapabilityLifecycleOperationKind.Disable, request.CapabilityId), null, snapshot)).Status);
        Assert.Equal(CapabilityLifecycleAuditMarkStatus.NotFound, await store.MarkOutcomeAuditedAsync(request.OperationId));
    }

    private static WorkspacePaths Prepare(TestWorkspace workspace)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        return paths;
    }

    private static CapabilityLifecycleMutationStore Store(WorkspacePaths paths, StubCapabilityLifecycleBaselineSource? baselineSource = null, StubCapabilityLifecycleArtifactEvidenceSource? artifactEvidenceSource = null) => new(paths, new TestCapabilityLifecycleTrustProvider(), baselineSource ?? new StubCapabilityLifecycleBaselineSource(), artifactEvidenceSource ?? new StubCapabilityLifecycleArtifactEvidenceSource());

}
