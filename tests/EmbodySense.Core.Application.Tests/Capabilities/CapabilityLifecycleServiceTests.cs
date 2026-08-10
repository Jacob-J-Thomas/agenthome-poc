using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityLifecycleServiceTests
{
    [Fact]
    public async Task Preview_captures_baseline_and_dependents_and_audits_intent_and_revision_evidence()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var state = new CapabilityLifecycleState(manifest.Descriptor, manifest.Checksum, true, false, 1, "activate", DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        var baseline = new CapabilityLifecycleBaseline(state, 4, 2);
        var index = new StubCapabilityDependentIndex();
        var baselineSource = new StubCapabilityLifecycleBaselineSource { Baseline = baseline };
        var preview = Preview(manifest, CapabilityLifecyclePreviewStatus.Ready, CapabilityLifecycleOperationKind.Upgrade);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = preview };
        var audit = new RecordingCapabilityAuditLog();
        var service = new CapabilityLifecycleService(index, baselineSource, new StubCapabilityLifecycleArtifactEvidenceSource(), store, audit, new StubCapabilityAuthorityTransaction());
        var request = new CapabilityLifecyclePreviewRequest(preview.OperationId, preview.Kind, manifest.Descriptor.Id, manifest.Descriptor, manifest.Checksum);

        var result = await service.PreviewAsync(request);

        Assert.Same(preview, result);
        Assert.Same(request, store.PreviewRequest);
        Assert.Same(baseline, store.Baseline);
        Assert.Equal(manifest.Descriptor.Id, baselineSource.LastCapabilityId);
        Assert.Equal([AuditSchema.Actions.CapabilityLifecycleIntent, AuditSchema.Actions.CapabilityLifecyclePreview], audit.Events.Select(item => item.Action));
        Assert.Equal(preview.PreviewHash, audit.Events[1].Metadata["previewHash"]);
    }

    [Theory]
    [InlineData(CapabilityLifecycleOperationKind.Enable, CapabilityLifecycleMutationStatus.Applied, "capability.lifecycle.mutation", "succeeded")]
    [InlineData(CapabilityLifecycleOperationKind.Upgrade, CapabilityLifecycleMutationStatus.Applied, "capability.lifecycle.mutation", "succeeded")]
    [InlineData(CapabilityLifecycleOperationKind.Rollback, CapabilityLifecycleMutationStatus.Applied, "capability.lifecycle.rollback", "succeeded")]
    [InlineData(CapabilityLifecycleOperationKind.Disable, CapabilityLifecycleMutationStatus.Conflict, "capability.lifecycle.conflict", "conflict")]
    [InlineData(CapabilityLifecycleOperationKind.Remove, CapabilityLifecycleMutationStatus.Blocked, "capability.lifecycle.mutation", "denied")]
    public async Task Mutation_recaptures_dependents_audits_terminal_outcome_and_repairs_pending_marker(CapabilityLifecycleOperationKind kind, CapabilityLifecycleMutationStatus status, string expectedAction, string expectedOutcome)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var preview = Preview(manifest, CapabilityLifecyclePreviewStatus.Ready, kind);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = preview, MutationResult = new CapabilityLifecycleMutationResult(status, null, 4, true, "terminal") };
        var index = new StubCapabilityDependentIndex();
        var state = new CapabilityLifecycleState(manifest.Descriptor, manifest.Checksum, true, false, 1, "activate", DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        var baseline = new CapabilityLifecycleBaseline(state, 4, 2);
        var baselineSource = new StubCapabilityLifecycleBaselineSource { Baseline = baseline };
        var audit = new RecordingCapabilityAuditLog();
        var service = new CapabilityLifecycleService(index, baselineSource, new StubCapabilityLifecycleArtifactEvidenceSource(), store, audit, new StubCapabilityAuthorityTransaction());

        var result = await service.MutateAsync(preview);

        Assert.Equal(status, result.Status);
        Assert.Same(preview, store.MutatedPreview);
        Assert.Same(baseline, store.MutatedBaseline);
        Assert.Equal(manifest.Descriptor.Id, baselineSource.LastCapabilityId);
        Assert.Equal(2, index.CaptureCount);
        Assert.Equal([expectedAction, AuditSchema.Actions.CapabilityLifecycleFinal], audit.Events.Select(item => item.Action));
        Assert.All(audit.Events, item => Assert.Equal(expectedOutcome, item.Outcome));
        Assert.Equal(1, store.AuditMarks);
        Assert.False(result.OutcomeAuditPending);
    }

    [Theory]
    [InlineData(CapabilityLifecycleOperationKind.Enable)]
    [InlineData(CapabilityLifecycleOperationKind.Upgrade)]
    [InlineData(CapabilityLifecycleOperationKind.Rollback)]
    public async Task Exact_terminal_artifact_preview_can_be_recovered_after_evidence_loss_and_repairs_pending_audit(CapabilityLifecycleOperationKind kind)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var originalPreview = Preview(manifest, CapabilityLifecyclePreviewStatus.Ready, kind);
        var recoveredPreview = originalPreview with { Status = CapabilityLifecyclePreviewStatus.Replayed };
        var request = new CapabilityLifecyclePreviewRequest(originalPreview.OperationId, kind, manifest.Descriptor.Id, kind is CapabilityLifecycleOperationKind.Enable or CapabilityLifecycleOperationKind.Upgrade ? manifest.Descriptor : null, kind is CapabilityLifecycleOperationKind.Enable or CapabilityLifecycleOperationKind.Upgrade ? manifest.Checksum : null);
        var evidence = new StubCapabilityLifecycleArtifactEvidenceSource { Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.NotFound, "deleted") };
        var index = new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Unavailable, string.Empty, [], "unavailable") };
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = recoveredPreview, MutationResult = new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Replayed, null, 4, true, "terminal replay", CapabilityLifecycleMutationStatus.Applied) };
        var audit = new RecordingCapabilityAuditLog();
        var service = new CapabilityLifecycleService(index, new StubCapabilityLifecycleBaselineSource(), evidence, store, audit, new StubCapabilityAuthorityTransaction());

        var recovered = await service.PreviewAsync(request);
        var result = await service.MutateAsync(recovered);

        Assert.Same(recoveredPreview, recovered);
        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, result.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, result.ReplayedOutcome);
        Assert.False(result.OutcomeAuditPending);
        Assert.Same(request, store.PreviewRequest);
        Assert.Same(recoveredPreview, store.MutatedPreview);
        Assert.Null(evidence.Descriptor);
        Assert.Equal(2, index.CaptureCount);
        Assert.Equal(4, audit.Events.Count);
        Assert.All(audit.Events, item => Assert.Equal(item.Action == AuditSchema.Actions.CapabilityLifecycleIntent ? AuditSchema.Outcomes.Started : AuditSchema.Outcomes.Succeeded, item.Outcome));
        Assert.Equal(1, store.AuditMarks);
    }

    [Fact]
    public async Task Replayed_audited_operation_does_not_duplicate_final_audit_or_marker()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var preview = Preview(manifest, CapabilityLifecyclePreviewStatus.Replayed, CapabilityLifecycleOperationKind.Disable);
        var store = new StubCapabilityLifecycleMutationStore { MutationResult = new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Replayed, null, 5, false, "replayed") };
        var audit = new RecordingCapabilityAuditLog();

        var result = await new CapabilityLifecycleService(new StubCapabilityDependentIndex(), new StubCapabilityLifecycleBaselineSource(), new StubCapabilityLifecycleArtifactEvidenceSource(), store, audit, new StubCapabilityAuthorityTransaction()).MutateAsync(preview);

        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, result.Status);
        Assert.Single(audit.Events);
        Assert.Equal(0, store.AuditMarks);
    }

    [Fact]
    public async Task Discard_audits_terminal_retirement_and_repairs_its_pending_receipt()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var preview = Preview(manifest, CapabilityLifecyclePreviewStatus.Replayed, CapabilityLifecycleOperationKind.Disable);
        var store = new StubCapabilityLifecycleMutationStore
        {
            MutationResult = new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Discarded, null, 5, true, "discarded")
        };
        var audit = new RecordingCapabilityAuditLog();
        var service = new CapabilityLifecycleService(new StubCapabilityDependentIndex(), new StubCapabilityLifecycleBaselineSource(), new StubCapabilityLifecycleArtifactEvidenceSource(), store, audit, new StubCapabilityAuthorityTransaction());

        var result = await service.DiscardAsync(preview);

        Assert.Equal(CapabilityLifecycleMutationStatus.Discarded, result.Status);
        Assert.False(result.OutcomeAuditPending);
        Assert.Same(preview, store.DiscardedPreview);
        Assert.Equal([AuditSchema.Actions.CapabilityLifecycleDiscard, AuditSchema.Actions.CapabilityLifecycleFinal], audit.Events.Select(item => item.Action));
        Assert.All(audit.Events, item => Assert.Equal(AuditSchema.Outcomes.Succeeded, item.Outcome));
        Assert.Equal(1, store.AuditMarks);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.DiscardAsync(null!));
    }

    [Fact]
    public async Task Upgrade_preview_delegates_artifact_authority_to_authenticated_store()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var evidence = new StubCapabilityLifecycleArtifactEvidenceSource { Evidence = new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.NotFound, "not proved") };
        var index = new StubCapabilityDependentIndex();
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = Preview(manifest, CapabilityLifecyclePreviewStatus.NotFound, CapabilityLifecycleOperationKind.Upgrade) };
        var audit = new RecordingCapabilityAuditLog();
        var request = new CapabilityLifecyclePreviewRequest("unproved-upgrade", CapabilityLifecycleOperationKind.Upgrade, manifest.Descriptor.Id, manifest.Descriptor, manifest.Checksum);

        var result = await new CapabilityLifecycleService(index, new StubCapabilityLifecycleBaselineSource(), evidence, store, audit, new StubCapabilityAuthorityTransaction()).PreviewAsync(request);

        Assert.Equal(CapabilityLifecyclePreviewStatus.NotFound, result.Status);
        Assert.Equal(2, index.CaptureCount);
        Assert.Same(request, store.PreviewRequest);
        Assert.Null(evidence.Descriptor);
        Assert.Equal([AuditSchema.Actions.CapabilityLifecycleIntent, AuditSchema.Actions.CapabilityLifecyclePreview], audit.Events.Select(item => item.Action));
    }

    [Theory]
    [InlineData(CapabilityLifecycleMutationStatus.Conflict, AuditSchema.Actions.CapabilityLifecycleConflict, AuditSchema.Outcomes.Conflict)]
    [InlineData(CapabilityLifecycleMutationStatus.Blocked, AuditSchema.Actions.CapabilityLifecycleMutation, AuditSchema.Outcomes.Denied)]
    [InlineData(CapabilityLifecycleMutationStatus.NotFound, AuditSchema.Actions.CapabilityLifecycleMutation, AuditSchema.Outcomes.Failed)]
    public async Task Terminal_non_applied_replay_preserves_outcome_for_audit_and_clears_repaired_marker(CapabilityLifecycleMutationStatus terminalStatus, string expectedAction, string expectedOutcome)
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var preview = Preview(manifest, CapabilityLifecyclePreviewStatus.Replayed, CapabilityLifecycleOperationKind.Disable);
        var store = new StubCapabilityLifecycleMutationStore { MutationResult = new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Replayed, null, 5, true, "replayed", terminalStatus) };
        var audit = new RecordingCapabilityAuditLog();

        var result = await new CapabilityLifecycleService(new StubCapabilityDependentIndex(), new StubCapabilityLifecycleBaselineSource(), new StubCapabilityLifecycleArtifactEvidenceSource(), store, audit, new StubCapabilityAuthorityTransaction()).MutateAsync(preview);

        Assert.Equal(CapabilityLifecycleMutationStatus.Replayed, result.Status);
        Assert.Equal(terminalStatus, result.ReplayedOutcome);
        Assert.False(result.OutcomeAuditPending);
        Assert.Equal([expectedAction, AuditSchema.Actions.CapabilityLifecycleFinal], audit.Events.Select(item => item.Action));
        Assert.All(audit.Events, item => Assert.Equal(expectedOutcome, item.Outcome));
        Assert.Equal(1, store.AuditMarks);
    }

    [Fact]
    public async Task Failed_audit_marker_repair_keeps_pending_state_visible()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var preview = Preview(manifest, CapabilityLifecyclePreviewStatus.Replayed, CapabilityLifecycleOperationKind.Disable);
        var store = new StubCapabilityLifecycleMutationStore
        {
            MutationResult = new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Replayed, null, 5, true, "replayed", CapabilityLifecycleMutationStatus.Applied),
            AuditMarkResult = CapabilityLifecycleAuditMarkStatus.Unavailable,
        };

        var result = await new CapabilityLifecycleService(new StubCapabilityDependentIndex(), new StubCapabilityLifecycleBaselineSource(), new StubCapabilityLifecycleArtifactEvidenceSource(), store, new RecordingCapabilityAuditLog(), new StubCapabilityAuthorityTransaction()).MutateAsync(preview);

        Assert.True(result.OutcomeAuditPending);
        Assert.Equal(1, store.AuditMarks);
    }

    [Fact]
    public async Task Preview_rejects_a_forged_null_capability_identity_without_calling_authority_ports()
    {
        var index = new StubCapabilityDependentIndex();
        var baseline = new StubCapabilityLifecycleBaselineSource();
        var store = new StubCapabilityLifecycleMutationStore();
        var audit = new RecordingCapabilityAuditLog();
        var request = new CapabilityLifecyclePreviewRequest("invalid-capability", CapabilityLifecycleOperationKind.Disable, null!);

        var result = await new CapabilityLifecycleService(index, baseline, new StubCapabilityLifecycleArtifactEvidenceSource(), store, audit, new StubCapabilityAuthorityTransaction()).PreviewAsync(request);

        Assert.Equal(CapabilityLifecyclePreviewStatus.Invalid, result.Status);
        Assert.Equal(0, index.CaptureCount);
        Assert.Null(baseline.LastCapabilityId);
        Assert.Null(store.PreviewRequest);
        Assert.Equal([AuditSchema.Actions.CapabilityLifecycleIntent, AuditSchema.Actions.CapabilityLifecyclePreview], audit.Events.Select(item => item.Action));
        Assert.All(audit.Events, item => Assert.Equal("invalid", item.Target));
    }

    [Fact]
    public void Constructor_rejects_missing_authority_ports()
    {
        var index = new StubCapabilityDependentIndex();
        var baseline = new StubCapabilityLifecycleBaselineSource();
        var store = new StubCapabilityLifecycleMutationStore();
        var audit = new RecordingCapabilityAuditLog();
        var artifact = new StubCapabilityLifecycleArtifactEvidenceSource();
        var authority = new StubCapabilityAuthorityTransaction();
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleService(null!, baseline, artifact, store, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleService(index, null!, artifact, store, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleService(index, baseline, null!, store, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleService(index, baseline, artifact, null!, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleService(index, baseline, artifact, store, null!, authority));
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleService(index, baseline, artifact, store, audit, null!));
    }

    [Fact]
    public async Task Dependent_change_during_baseline_proof_is_finalized_as_unavailable()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        var initial = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, manifest.Checksum.Value, [], "initial");
        var changed = initial with { Hash = CapabilityArtifactTestData.Manifest(content: "changed"u8.ToArray()).Checksum.Value, Detail = "changed" };
        var index = new StubCapabilityDependentIndex();
        index.Snapshots.Enqueue(initial);
        index.Snapshots.Enqueue(changed);
        var expected = Preview(manifest, CapabilityLifecyclePreviewStatus.Unavailable, CapabilityLifecycleOperationKind.Disable);
        var store = new StubCapabilityLifecycleMutationStore { PreviewResult = expected };
        var service = new CapabilityLifecycleService(index, new StubCapabilityLifecycleBaselineSource(), new StubCapabilityLifecycleArtifactEvidenceSource(), store, new RecordingCapabilityAuditLog(), new StubCapabilityAuthorityTransaction());

        var result = await service.PreviewAsync(new CapabilityLifecyclePreviewRequest("sidecar-changed", CapabilityLifecycleOperationKind.Disable, manifest.Descriptor.Id));

        Assert.Same(expected, result);
        Assert.Equal(2, index.CaptureCount);
        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, store.PreviewDependents!.Status);
        Assert.Empty(store.PreviewDependents.Dependents);
        Assert.Contains("changed", store.PreviewDependents.Detail, StringComparison.Ordinal);
    }

    private static CapabilityLifecyclePreview Preview(CapabilityArtifactManifest manifest, CapabilityLifecyclePreviewStatus status, CapabilityLifecycleOperationKind kind) => new(status, "sha256:workspace", "lifecycle-operation", kind, manifest.Descriptor.Id, 3, 2, manifest.Checksum.Value, manifest.Checksum.Value, [], "preview", 4, 2, kind is CapabilityLifecycleOperationKind.Enable or CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback ? manifest.Descriptor : null, kind is CapabilityLifecycleOperationKind.Enable or CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback ? manifest.Checksum : null);
}
