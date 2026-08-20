using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Admission;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;
using System.Text.Json;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialRunMaterializerTests
{
    private static readonly DateTimeOffset _admittedAtUtc = GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(1);
    private static readonly DateTimeOffset _auditAtUtc = GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(2);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Materialize_uses_only_receipt_run_identity_and_atomically_persists_exact_trigger_evidence(bool includeConversation)
    {
        var context = await ContextAsync(includeConversation);
        var store = new RecordingRunStore();
        var audit = new RecordingAuditRecorder();
        var identities = new RecordingEventIdentityGenerator();
        var materializer = CreateMaterializer(store, audit, identities);

        var result = await materializer.MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Ready, result.Status);
        Assert.True(result.IsReady());
        var run = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal(context.Receipt.Evidence.Binding.RunId, run.Id);
        Assert.Equal(context.Invocation.InvokingConversation, run.InvokingConversation);
        Assert.Equal(context.Invocation.ContentHash, run.SequentialInvocationSnapshot?.ContentHash);
        Assert.Equal(context.AdapterBinding.ContentHash, run.SequentialAdapterBinding?.ContentHash);
        Assert.Equal(context.Receipt.ContentHash, run.SequentialAdapterBinding?.AdmissionReceiptHash);
        Assert.Equal(context.Receipt.ContentHash, run.SequentialAdapterBinding?.AdmissionReceipt.ContentHash);
        Assert.NotSame(context.Receipt, run.SequentialAdapterBinding?.AdmissionReceipt);
        Assert.Equal(context.Receipt.Evidence.GrantProfile, run.SequentialAdapterBinding?.AdmissionReceipt.Evidence.GrantProfile);
        Assert.Equal(context.Receipt.Evidence.GrantBoundary, run.SequentialAdapterBinding?.AdmissionReceipt.Evidence.GrantBoundary);
        Assert.Equal(context.Receipt.Evidence.GrantDependencyEvidenceHash, run.SequentialAdapterBinding?.AdmissionReceipt.Evidence.GrantDependencyEvidenceHash);
        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Equal(2, identities.CallCount);
        var created = Assert.IsType<CustomLoopRunRecord>(store.CreatedCandidates.Single());
        var admitted = Assert.Single(created.Events);
        Assert.Equal(CustomLoopRunEventKind.Admitted, admitted.Kind);
        Assert.Equal(context.Plan.Nodes[0].NodeId, admitted.SequentialNodeEvidence?.NodeId);
        Assert.Equal(CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, admitted.SequentialNodeEvidence?.Kind);
        Assert.Equal(CustomLoopSequentialNodeDisposition.Completed, admitted.SequentialNodeEvidence?.Disposition);
        Assert.True(CustomLoopSequentialNodeEvidenceHash.Matches(admitted.SequentialNodeEvidence));
        Assert.True(CustomLoopSequentialOutcomeArtifactHash.Matches(admitted));
        Assert.Equal([CustomLoopRunEventKind.Admitted, CustomLoopRunEventKind.AdmissionAuditCompleted], run.Events.Select(item => item.Kind));
        Assert.True(CustomLoopRunValidator.ValidateForDispatch(run).IsValid);

        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(_admittedAtUtc, auditEvent.TimestampUtc);
        Assert.Equal(context.Receipt.Intent.ActorId.Value, auditEvent.Actor);
        Assert.Equal(AuditSchema.Actions.LoopRunAdmission, auditEvent.Action);
        Assert.Equal(run.Id, auditEvent.Target);
        Assert.Equal(AuditSchema.Outcomes.Succeeded, auditEvent.Outcome);
        Assert.Equal(context.AdapterBinding.ContentHash, auditEvent.Metadata["adapter_binding_hash"]);
        Assert.Equal(context.Artifact.ArtifactHash, auditEvent.Metadata["graph_artifact_hash"]);
        Assert.Equal(
            GovernedLoopSequentialAuditOperationId.ForAdmission(context.Receipt.ContentHash, context.AdapterBinding.ContentHash),
            Assert.Single(audit.OperationIds));
        Assert.Equal(context.AdapterBinding.ContentHash, Assert.Single(audit.EvidenceHashes));
    }

    [Fact]
    public async Task Tool_enabled_materialization_preserves_exact_canonical_roots_and_fenced_assignments()
    {
        var context = await ContextAsync(allowWorkspaceTools: true);

        var result = await CreateMaterializer(new RecordingRunStore(), new RecordingAuditRecorder()).MaterializeAsync(context.Request);

        Assert.True(result.IsReady());
        var run = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal(
            ["org.embodysense/conversation-turn", "org.embodysense/model-inference", "org.embodysense/workspace-command"],
            run.CapabilityAdmission.Evidence
                .Where(item => string.Equals(item.Outcome, "Selected", StringComparison.Ordinal))
                .Select(item => item.SelectedIdentity?.Id.Value)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [EmbodySense.Core.Common.Loops.Models.Custom.CustomLoopToolAssignment.List, EmbodySense.Core.Common.Loops.Models.Custom.CustomLoopToolAssignment.Read, EmbodySense.Core.Common.Loops.Models.Custom.CustomLoopToolAssignment.Search],
            run.AdmittedDefinition.ToolAssignments);
        Assert.True(CustomLoopRunValidator.ValidateForDispatch(run).IsValid);
    }

    [Fact]
    public async Task Exact_replay_performs_no_second_create_audit_or_marker_write()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore();
        var audit = new RecordingAuditRecorder();
        var identities = new RecordingEventIdentityGenerator();
        var materializer = CreateMaterializer(store, audit, identities);

        var first = await materializer.MaterializeAsync(context.Request);
        var replay = await materializer.MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Ready, first.Status);
        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Replayed, replay.Status);
        Assert.True(replay.IsReady());
        Assert.Same(first.Run, replay.Run);
        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Single(audit.Events);
        Assert.Equal(2, identities.CallCount);
    }

    [Fact]
    public async Task Concurrent_exact_create_is_reconciled_from_the_store_result_before_execution()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore { ForcedCreateStatus = CustomLoopRunStoreStatus.AlreadyCreated };
        var audit = new RecordingAuditRecorder();

        var result = await CreateMaterializer(store, audit).MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Replayed, result.Status);
        Assert.True(result.IsReady());
        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Single(audit.Events);
        Assert.True(CustomLoopRunValidator.HasCompleteAdmissionAudit(result.Run));
    }

    [Fact]
    public async Task Create_response_failure_after_commit_reconciles_exact_run_before_audit_and_marker()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore { ThrowAfterFirstCreate = true };
        var audit = new RecordingAuditRecorder();
        var materializer = CreateMaterializer(store, audit);

        var result = await materializer.MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Replayed, result.Status);
        Assert.True(result.IsReady());
        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Single(audit.Events);
        Assert.Equal(context.Receipt.Evidence.Binding.RunId, result.Run?.Id);
    }

    [Fact]
    public async Task Audit_append_uncertainty_is_re_emitted_before_marker_and_never_assumed_durable()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore();
        var audit = new RecordingAuditRecorder { ThrowAfterFirstRecord = true };
        var materializer = CreateMaterializer(store, audit);

        var uncertain = await materializer.MaterializeAsync(context.Request);
        var reconciled = await materializer.MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, uncertain.Status);
        Assert.False(uncertain.IsReady());
        Assert.Single(Assert.IsType<CustomLoopRunRecord>(uncertain.Run).Events);
        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Replayed, reconciled.Status);
        Assert.True(reconciled.IsReady());
        Assert.Equal(2, audit.RecordAttemptCount);
        Assert.Single(audit.Events);
        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal(1, store.UpdateCallCount);
    }

    [Fact]
    public async Task Marker_response_failure_after_commit_is_authenticated_by_exact_replay()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore { ThrowAfterFirstUpdate = true };
        var audit = new RecordingAuditRecorder();
        var materializer = CreateMaterializer(store, audit);

        var result = await materializer.MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Replayed, result.Status);
        Assert.True(result.IsReady());
        Assert.Single(audit.Events);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.True(CustomLoopRunValidator.HasCompleteAdmissionAudit(result.Run));
    }

    [Fact]
    public async Task Marker_failure_before_commit_requires_a_later_audit_reconciliation()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore { ThrowBeforeFirstUpdate = true };
        var audit = new RecordingAuditRecorder();
        var materializer = CreateMaterializer(store, audit);

        var blocked = await materializer.MaterializeAsync(context.Request);
        var reconciled = await materializer.MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, blocked.Status);
        Assert.False(blocked.IsReady());
        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Replayed, reconciled.Status);
        Assert.True(reconciled.IsReady());
        Assert.Equal(2, audit.RecordAttemptCount);
        Assert.Equal(2, store.UpdateCallCount);
    }

    [Theory]
    [InlineData(GovernedLoopSequentialAuditRecordStatus.Conflict, GovernedLoopSequentialMaterializationStatus.AuditConflict)]
    [InlineData(GovernedLoopSequentialAuditRecordStatus.Unavailable, GovernedLoopSequentialMaterializationStatus.AuditUnavailable)]
    public async Task Non_durable_audit_dispositions_never_append_marker_or_allow_execution(
        GovernedLoopSequentialAuditRecordStatus status,
        GovernedLoopSequentialMaterializationStatus expectedStatus)
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore();
        var audit = new RecordingAuditRecorder { ForcedStatus = status };

        var result = await CreateMaterializer(store, audit).MaterializeAsync(context.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.False(result.IsReady());
        Assert.Single(Assert.IsType<CustomLoopRunRecord>(result.Run).Events);
        Assert.Equal(0, store.UpdateCallCount);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Internal_reconciliation_timeouts_return_closed_results_instead_of_leaking_cancellation()
    {
        var context = await ContextAsync();
        var createTimeout = new RecordingRunStore
        {
            ThrowAfterFirstCreate = true,
            CancelReadsAfterCreate = true,
        };
        var markerTimeout = new RecordingRunStore
        {
            ThrowBeforeFirstUpdate = true,
            CancelReadsAfterUpdate = true,
        };

        var createResult = await CreateMaterializer(createTimeout, new RecordingAuditRecorder()).MaterializeAsync(context.Request);
        var markerResult = await CreateMaterializer(markerTimeout, new RecordingAuditRecorder()).MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Unavailable, createResult.Status);
        Assert.Equal(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, markerResult.Status);
        Assert.False(createResult.IsReady());
        Assert.False(markerResult.IsReady());
    }

    [Fact]
    public async Task Existing_run_bound_to_substituted_admission_coordinates_fails_closed()
    {
        var original = await ContextAsync(surface: "web");
        var substituted = await ContextAsync(surface: "cli");
        var store = new RecordingRunStore();
        var materializer = CreateMaterializer(store, new RecordingAuditRecorder());
        Assert.True((await materializer.MaterializeAsync(original.Request)).IsReady());

        var result = await materializer.MaterializeAsync(substituted.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Conflict, result.Status);
        Assert.False(result.IsReady());
        Assert.Equal(1, store.CreateCallCount);
    }

    [Fact]
    public async Task Invalid_contracts_and_plan_substitution_do_no_durable_work()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore();
        var audit = new RecordingAuditRecorder();
        var materializer = CreateMaterializer(store, audit);
        var otherArtifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2);
        var otherPlan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(otherArtifact).Plan);

        var results = new[]
        {
            await materializer.MaterializeAsync(null),
            await materializer.MaterializeAsync(context.Request with { SchemaVersion = 2 }),
            await materializer.MaterializeAsync(context.Request with
            {
                AdapterBinding = context.AdapterBinding with { ContentHash = Hash('f') },
            }),
            await materializer.MaterializeAsync(context.Request with { Plan = otherPlan }),
        };

        Assert.All(results, result => Assert.Equal(GovernedLoopSequentialMaterializationStatus.Invalid, result.Status));
        Assert.Equal(0, store.CreateCallCount);
        Assert.Empty(audit.Events);
    }

    [Theory]
    [InlineData(CustomLoopRunStoreStatus.OperationConflict, GovernedLoopSequentialMaterializationStatus.Conflict)]
    [InlineData(CustomLoopRunStoreStatus.NonterminalRunExists, GovernedLoopSequentialMaterializationStatus.NonterminalRunExists)]
    [InlineData(CustomLoopRunStoreStatus.LimitExceeded, GovernedLoopSequentialMaterializationStatus.LimitExceeded)]
    [InlineData((CustomLoopRunStoreStatus)0, GovernedLoopSequentialMaterializationStatus.Unavailable)]
    public async Task Store_creation_outcomes_remain_closed_and_never_audit(
        CustomLoopRunStoreStatus storeStatus,
        GovernedLoopSequentialMaterializationStatus expectedStatus)
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore { ForcedCreateStatus = storeStatus };
        var audit = new RecordingAuditRecorder();

        var result = await CreateMaterializer(store, audit).MaterializeAsync(context.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.False(result.IsReady());
        Assert.Empty(audit.Events);
        Assert.Equal(0, store.UpdateCallCount);
    }

    [Fact]
    public async Task Store_read_failure_and_disagreeing_indexes_fail_closed_before_create()
    {
        var context = await ContextAsync();
        var throwingStore = new RecordingRunStore { ReadException = new IOException("offline") };
        var unavailable = await CreateMaterializer(throwingStore, new RecordingAuditRecorder()).MaterializeAsync(context.Request);

        var seededStore = new RecordingRunStore();
        var materializer = CreateMaterializer(seededStore, new RecordingAuditRecorder());
        var ready = await materializer.MaterializeAsync(context.Request);
        var exact = Assert.IsType<CustomLoopRunRecord>(ready.Run);
        seededStore.RunReadOverride = exact with { Id = "run-other" };
        var conflict = await materializer.MaterializeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Unavailable, unavailable.Status);
        Assert.Equal(GovernedLoopSequentialMaterializationStatus.Conflict, conflict.Status);
        Assert.Equal(0, throwingStore.CreateCallCount);
        Assert.Equal(1, seededStore.CreateCallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_observed_before_any_store_access()
    {
        var context = await ContextAsync();
        var store = new RecordingRunStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateMaterializer(store, new RecordingAuditRecorder()).MaterializeAsync(context.Request, cancellation.Token));

        Assert.Equal(0, store.ReadCallCount);
        Assert.Equal(0, store.CreateCallCount);
    }

    private static GovernedLoopSequentialRunMaterializer CreateMaterializer(
        RecordingRunStore store,
        RecordingAuditRecorder audit,
        RecordingEventIdentityGenerator? identities = null)
        => new(store, audit, identities ?? new RecordingEventIdentityGenerator(), new FixedTimeProvider(_auditAtUtc));

    internal static async Task<TestContext> ContextAsync(
        bool includeConversation = true,
        string surface = "web",
        bool allowWorkspaceTools = false,
        int inferenceCount = 1,
        IReadOnlyList<string>? inferenceIds = null,
        Func<ContextualRoleRevisionPin, GovernedLoopGraphRevisionArtifact>? artifactFactory = null)
    {
        var seedHarness = GovernedLoopAdmissionTestHarness.Create();
        var seedOutcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seedHarness.CreateService().AdmitAsync(seedHarness.Request)).Outcome);
        var seedReceipt = Assert.IsType<GovernedLoopAdmissionReceipt>(seedOutcome.Receipt);
        var artifact = artifactFactory?.Invoke(seedReceipt.Intent.Role)
            ?? GovernedLoopSequentialApplicationTestFixture.LinearArtifact(
                inferenceCount,
                inferenceIds,
                owningRole: seedReceipt.Intent.Role,
                allowWorkspaceTools: allowWorkspaceTools);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            artifact.RevisionArtifact.Revision,
            "publish-sequential",
            Hash('7'));
        var contextSnapshot = CustomLoopContextSnapshot.CreateEmpty(GovernedLoopSequentialApplicationTestFixture.Now);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            "Execute the exact admitted request.",
            new CustomLoopModelSnapshot("provider", "model"),
            includeConversation
                ? new CustomLoopConversationReference(
                    Hash('8'),
                    "version-1",
                    GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(-1))
                : null,
            contextSnapshot.CapturedAtUtc,
            contextSnapshot.SourceManifest,
            string.Empty));
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            "admit-sequential",
            invocation.ContentHash,
            string.Empty,
            publication,
            seedReceipt.Intent.AuthorityGrant,
            seedReceipt.Intent.ActorId,
            surface));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            seedReceipt.Intent.WorkspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            publication,
            admissionRequest.AuthorityGrant,
            artifact.Graph.OwningRole,
            admissionRequest.ActorId,
            admissionRequest.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var capabilityAdmission = CapabilityAdmission(artifact, intent.WorkspaceId);
        var execution = GovernedLoopExecutionBinding.Create(
            1,
            "run-sequential",
            publication.Revision,
            1);
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            seedReceipt.Evidence.GrantProfile,
            new AuthorityGrantBoundary(
                _admittedAtUtc.AddHours(-1),
                _admittedAtUtc.AddHours(1),
                seedReceipt.Evidence.GrantBoundary.CompletionConstraint),
            seedReceipt.Evidence.GrantDependencyEvidenceHash,
            seedReceipt.Evidence.EffectiveAuthority,
            capabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, seedReceipt.Evidence.EffectiveAuthority, capabilityAdmission),
            _admittedAtUtc,
            string.Empty));
        var receiptDraft = new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            evidence,
            _admittedAtUtc,
            string.Empty);
        var receipt = GovernedLoopAdmissionContractHash.Apply(receiptDraft);
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            intent.WorkspaceId,
            execution,
            admissionRequest.OperationId,
            receipt,
            receipt.ContentHash,
            admissionRequest.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        return new TestContext(
            artifact,
            plan,
            invocation,
            admissionRequest,
            receipt,
            adapterBinding,
            new GovernedLoopSequentialMaterializationRequest(
                GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
                admissionRequest,
                receipt,
                artifact,
                plan,
                invocation,
                adapterBinding));
    }

    private static CapabilityAdmissionSnapshot CapabilityAdmission(
        GovernedLoopGraphRevisionArtifact artifact,
        string workspaceId)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var any, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _));
        var dependencies = artifact.Graph.AuthorityCeiling.CapabilityIds
            .Order(StringComparer.Ordinal)
            .Select(value =>
            {
                Assert.True(CapabilityId.TryParse(value, out var id, out _));
                return new CapabilityDependency(id!, any!);
            })
            .ToArray();
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            dependencies,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        return TestCapabilityAdmissionFactory.Create(manifest, GovernedLoopSequentialApplicationTestFixture.Now) with
        {
            WorkspaceScopeId = workspaceId,
        };
    }

    private static string Hash(char value) => GovernedLoopSequentialApplicationTestFixture.Hash(value);

    internal sealed record TestContext(
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopSequentialPlan Plan,
        GovernedLoopSequentialInvocationSnapshot Invocation,
        GovernedLoopAdmissionRequest AdmissionRequest,
        GovernedLoopAdmissionReceipt Receipt,
        GovernedLoopSequentialAdapterBinding AdapterBinding,
        GovernedLoopSequentialMaterializationRequest Request);

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class RecordingEventIdentityGenerator : IGovernedLoopSequentialEventIdentityGenerator
    {
        public int CallCount { get; private set; }

        public string NewEventId() => $"event-sequential-{++CallCount}";
    }

    internal sealed class RecordingAuditRecorder : IGovernedLoopSequentialAuditRecorder
    {
        private readonly Dictionary<string, (string EvidenceHash, string EventJson)> _records = new(StringComparer.Ordinal);

        public bool ThrowAfterFirstRecord { get; init; }

        public int RecordAttemptCount { get; private set; }

        public List<AuditEvent> Events { get; } = [];

        public List<string> OperationIds { get; } = [];

        public List<string> EvidenceHashes { get; } = [];

        public GovernedLoopSequentialAuditRecordStatus? ForcedStatus { get; init; }

        public Task<GovernedLoopSequentialAuditRecordResult> RecordOnceAsync(
            string operationId,
            string evidenceHash,
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordAttemptCount++;
            OperationIds.Add(operationId);
            EvidenceHashes.Add(evidenceHash);
            if (ForcedStatus is { } forced)
            {
                return Task.FromResult(new GovernedLoopSequentialAuditRecordResult(forced, "forced"));
            }

            var eventJson = JsonSerializer.Serialize(auditEvent);
            if (_records.TryGetValue(operationId, out var existing))
            {
                return Task.FromResult(existing == (evidenceHash, eventJson)
                    ? new GovernedLoopSequentialAuditRecordResult(GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded, "replayed")
                    : new GovernedLoopSequentialAuditRecordResult(GovernedLoopSequentialAuditRecordStatus.Conflict, "conflict"));
            }

            _records.Add(operationId, (evidenceHash, eventJson));
            Events.Add(auditEvent);
            return ThrowAfterFirstRecord && RecordAttemptCount == 1
                ? Task.FromException<GovernedLoopSequentialAuditRecordResult>(new IOException("audit response lost"))
                : Task.FromResult(new GovernedLoopSequentialAuditRecordResult(GovernedLoopSequentialAuditRecordStatus.Recorded, "recorded"));
        }
    }

    internal sealed class RecordingRunStore : ICustomLoopRunStore
    {
        private CustomLoopRunRecord? _run;

        public bool ThrowAfterFirstCreate { get; init; }

        public bool ThrowBeforeFirstUpdate { get; init; }

        public bool ThrowAfterFirstUpdate { get; init; }

        public bool CancelReadsAfterCreate { get; init; }

        public bool CancelReadsAfterUpdate { get; init; }

        public Exception? ReadException { get; init; }

        public CustomLoopRunStoreStatus? ForcedCreateStatus { get; init; }

        public CustomLoopRunRecord? RunReadOverride { get; set; }

        public int ReadCallCount { get; private set; }

        public int CreateCallCount { get; private set; }

        public int UpdateCallCount { get; private set; }

        public List<CustomLoopRunRecord> CreatedCandidates { get; } = [];

        public Task<CustomLoopRunStoreResult> CreateAsync(
            CustomLoopRunRecord run,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCallCount++;
            CreatedCandidates.Add(run);
            if (ForcedCreateStatus is { } status)
            {
                if (status == CustomLoopRunStoreStatus.AlreadyCreated)
                {
                    _run = run;
                }

                return Task.FromResult(new CustomLoopRunStoreResult(
                    status,
                    status is CustomLoopRunStoreStatus.NonterminalRunExists or CustomLoopRunStoreStatus.AlreadyCreated ? run : null,
                    null));
            }

            if (_run is not null)
            {
                return Task.FromResult(CustomLoopRunStoreResult.AlreadyCreated(_run));
            }

            _run = run;
            return ThrowAfterFirstCreate && CreateCallCount == 1
                ? Task.FromException<CustomLoopRunStoreResult>(new IOException("create response lost"))
                : Task.FromResult(CustomLoopRunStoreResult.Created(run));
        }

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        {
            Read(cancellationToken);
            return Task.FromResult(RunReadOverride ?? (_run is not null && string.Equals(_run.Id, runId, StringComparison.Ordinal) ? _run : null));
        }

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(
            string admissionOperationId,
            CancellationToken cancellationToken = default)
        {
            Read(cancellationToken);
            return Task.FromResult(_run is not null && string.Equals(_run.AdmissionOperationId, admissionOperationId, StringComparison.Ordinal) ? _run : null);
        }

        public Task<CustomLoopRunStoreResult> UpdateAsync(
            CustomLoopRunRecord run,
            int expectedLifecycleVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCallCount++;
            if (ThrowBeforeFirstUpdate && UpdateCallCount == 1)
            {
                return Task.FromException<CustomLoopRunStoreResult>(new IOException("marker rejected before commit"));
            }

            if (_run is null)
            {
                return Task.FromResult(CustomLoopRunStoreResult.NotFound());
            }

            if (_run.LifecycleVersion != expectedLifecycleVersion)
            {
                return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(_run, expectedLifecycleVersion));
            }

            _run = run;
            return ThrowAfterFirstUpdate && UpdateCallCount == 1
                ? Task.FromException<CustomLoopRunStoreResult>(new IOException("marker response lost"))
                : Task.FromResult(CustomLoopRunStoreResult.Updated(run));
        }

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private void Read(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            if (CancelReadsAfterCreate && CreateCallCount > 0
                || CancelReadsAfterUpdate && UpdateCallCount > 0)
            {
                throw new OperationCanceledException("internal timeout");
            }

            if (ReadException is not null)
            {
                throw ReadException;
            }
        }
    }
}
