using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Admission;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed class CustomLoopOrderedRunnerTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions _rawTraceSizingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [Fact]
    public void Attempt_reservation_covers_two_maximally_escaped_outcome_events()
    {
        var output = new string('\uffff', CustomLoopLimits.MaxCanonicalModelOutputCharacters);
        var reference = new string('\uffff', CustomLoopLimits.MaxTraceReferenceCharacters);
        var observed = new CustomLoopRunEvent(1, "observed", _now, CustomLoopRunEventKind.NodeOutcomeObserved, 1, "step-1", 1, "Inference provider outcome was observed and retained as local evidence.", [], output, int.MaxValue, true, false, true, "publish", reference, reference, reference, null);
        var completed = new CustomLoopRunEvent(2, "completed", _now, CustomLoopRunEventKind.NodeAttemptCompleted, 1, "step-1", 1, "Inference attempt completed without an automatic retry.", [], output, int.MaxValue, true, false, true, "publish", reference, reference, reference, null);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        };

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(new[] { observed, completed }, options).Length;

        Assert.True(serializedBytes <= CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes, $"Worst-case mandatory outcome evidence used {serializedBytes} bytes but only {CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes} are reserved.");
    }

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        var store = new FakeRunStore(Run(Definition()));
        var resolver = new CustomLoopContextResolver();
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        var authority = new TestAuthorityProvider();

        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(null!, resolver, executor, publisher, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, null!, executor, publisher, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, resolver, null!, publisher, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, resolver, executor, null!, audit, authority));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, resolver, executor, publisher, null!, authority));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, resolver, executor, publisher, audit, null!));
    }

    [Fact]
    public async Task Canonical_adapter_dispatches_the_exact_linear_plan_and_advances_only_after_resolved_evidence()
    {
        var definition = SequentialDefinition(2, includeConversation: true);
        var admitted = Run(definition, new CustomLoopConversationReference("conversation-one", "version-one", _now));
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("first outcome"), Result("final outcome"));
        var publisher = new RecordingPublisher();
        var runtime = Runner(store, executor, publisher);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(runtime, evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        Assert.Equal(["infer-01", "infer-02"], executor.Requests.Select(item => item.StepId));
        Assert.Equal(["trigger", "infer-01", "infer-02", "exit"], evidence.Requests.Select(item => item.Dispatch.Node.NodeId));
        Assert.Equal([0, 0, 1, 2], evidence.NextStepIndicesAtRetention);
        Assert.NotEqual(admitted.AdmissionRequestHash, context.Anchor.AdapterBinding.AdmissionRequestHash);
        Assert.Equal(2, result.Run!.Checkpoint.NextStepIndex);
        var publication = Assert.Single(publisher.Requests);
        Assert.Equal("final outcome", publication.CanonicalOutput);
        Assert.True(Array.FindIndex(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01")
            < Array.FindIndex(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted && item.Detail.Contains("infer-01", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Canonical_adapter_preserves_the_fixed_tool_free_budget_posture()
    {
        var definition = SequentialDefinition(1);
        var admitted = Run(definition);
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("tool-free outcome"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, authorityProvider: new ThrowingToolAuthorityProvider()),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        Assert.False(Assert.Single(executor.Requests).AllowTools);
        Assert.Equal(0, result.Run!.Checkpoint.ToolRequestsUsed);
        Assert.DoesNotContain(result.Run.Events, item => item.ToolEvidence is not null);
    }

    [Fact]
    public async Task Canonical_exhausted_governed_tool_budget_is_applied_before_provider_dispatch()
    {
        var admitted = Run(SequentialDefinition(allowWorkspaceTools: true)) with
        {
            Checkpoint = CustomLoopRunCheckpoint.Start() with
            {
                ToolRequestsUsed = CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun,
            },
        };
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("budget-safe outcome"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        var request = Assert.Single(executor.Requests);
        Assert.False(request.AllowTools);
        Assert.Contains(request.InferenceRequest.Messages, message => message.Content.Contains("Tools: none", StringComparison.Ordinal));
        Assert.Equal(CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun, result.Run!.Checkpoint.ToolRequestsUsed);
        Assert.DoesNotContain(result.Run.Events, item => item.ToolEvidence is not null);
    }

    [Fact]
    public async Task Canonical_resume_reconciles_an_exhausted_tool_budget_outcome_without_provider_redispatch()
    {
        var admitted = Run(SequentialDefinition(allowWorkspaceTools: true)) with
        {
            Checkpoint = CustomLoopRunCheckpoint.Start() with
            {
                ToolRequestsUsed = CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun,
            },
        };
        var context = await SequentialContextAsync(admitted);
        CustomLoopRunRecord? retainedOutcome = null;
        var firstStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedOutcome is null
                    && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01"))
                {
                    retainedOutcome = candidate;
                    throw new IOException("Simulated process loss after exhausted-budget outcome retention.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result("budget-safe retained outcome"));
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(firstStore, firstExecutor), firstEvidence, firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        var resumable = ResumeReady(Assert.IsType<CustomLoopRunRecord>(retainedOutcome), "resume-exhausted-tool-budget");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(resumedStore, resumedExecutor), resumedEvidence, resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-exhausted-tool-budget",
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Single(firstExecutor.Requests);
        Assert.Equal(CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun, result.Run!.Checkpoint.ToolRequestsUsed);
        Assert.DoesNotContain(result.Run.Events, item => item.ToolEvidence is not null);
    }

    [Fact]
    public async Task Canonical_adapter_rejects_every_self_consistent_legacy_projection_substitution_before_dispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var exact = context.Run.AdmittedDefinition;
        var substitutions = new[]
        {
            exact with { DefinitionVersion = exact.DefinitionVersion + 1 },
            exact with { DisplayName = exact.DisplayName + " substituted" },
            exact with { CreatedAtUtc = exact.CreatedAtUtc.AddSeconds(-1), UpdatedAtUtc = exact.UpdatedAtUtc.AddSeconds(-1) },
            exact with { LastMutationOperationId = "substituted-projection" },
            exact with { TriggerPolicy = exact.TriggerPolicy with { IncludeInvokingConversation = !exact.TriggerPolicy.IncludeInvokingConversation } },
            exact with
            {
                InferenceSteps = exact.InferenceSteps
                    .Select((step, index) => index == 0 ? step with { Instruction = step.Instruction + " substituted" } : step)
                    .ToArray(),
            },
            exact with { ExitPolicy = exact.ExitPolicy with { MaxAdditionalIterations = 1 } },
            exact with
            {
                ToolAssignments = [CustomLoopToolAssignment.Read],
                CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest(exact.Id, [CustomLoopToolAssignment.Read]),
            },
        };

        foreach (var substitution in substitutions)
        {
            var substitutedDefinition = CustomLoopDefinitionContentHash.Apply(substitution with { ContentHash = string.Empty });
            var substitutedRun = CustomLoopAdmissionRequestHash.Apply(context.Run with
            {
                AdmittedDefinition = substitutedDefinition,
                AdmissionRequestHash = string.Empty,
            });
            var store = new FakeRunStore(substitutedRun, validateSeed: false);
            var executor = new QueueExecutor(Result("must not run"));
            var evidence = new SequentialEvidenceHarness(store, context.Evidence);
            var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
                Runner(store, executor, authorityProvider: new ThrowingToolAuthorityProvider()),
                evidence,
                evidence);

            var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
                1,
                context.Anchor,
                context.Plan,
                context.Artifact,
                AuditSchema.Actors.Web));

            Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
            Assert.Empty(executor.Requests);
            Assert.Empty(store.Writes);
            Assert.Empty(evidence.Requests);
        }
    }

    [Fact]
    public async Task Canonical_adapter_rejects_substituted_persisted_handoff_before_loading_the_ordered_run()
    {
        var admitted = Run(SequentialDefinition());
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var substitutedBinding = GovernedLoopSequentialContractHash.Apply(context.Evidence.AdapterBinding with
        {
            AdmissionRequestHash = Hash("substituted-canonical-request"),
            ContentHash = string.Empty
        });
        var evidence = new SequentialEvidenceHarness(store, context.Evidence with { AdapterBinding = substitutedBinding });
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(store.Writes);
        Assert.Empty(evidence.Requests);
    }

    [Fact]
    public async Task Canonical_adapter_rehashes_artifact_and_rejects_plan_substitution_before_dispatch()
    {
        var admitted = Run(SequentialDefinition());
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);
        var substitutedRole = context.Artifact.Graph.OwningRole with { ContentHash = Hash("substituted-role") };
        var substitutedArtifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(
            1,
            ["infer-01"],
            owningRole: substitutedRole);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, substitutedArtifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(store.Writes);
        Assert.Empty(evidence.Requests);
    }

    [Fact]
    public async Task Canonical_adapter_retains_provider_ambiguity_and_never_advances_or_retries()
    {
        var admitted = Run(SequentialDefinition());
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(new IOException("transport outcome is unknown"), Result("must not retry"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("inference_attempt_uncertain", result.Run!.FailureCode);
        Assert.Equal(0, result.Run.Checkpoint.NextStepIndex);
        Assert.Single(executor.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview, evidence.Requests[^1].Disposition);
    }

    [Fact]
    public async Task Canonical_adapter_retains_audit_failure_as_review_without_checkpoint_advancement()
    {
        var admitted = Run(SequentialDefinition());
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("observed outcome"));
        var audit = new RecordingAuditLog
        {
            FailPredicate = item => false
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence)
        {
            ForcedAuditStatus = GovernedLoopSequentialAuditRecordStatus.Unavailable,
        };
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor, audit: audit), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("canonical_outcome_audit_unavailable", result.Run!.FailureCode);
        Assert.Equal(0, result.Run.Checkpoint.NextStepIndex);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Canonical_adapter_parks_conflicting_append_once_audit_without_publication_or_checkpoint_advancement()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition(includeConversation: true), new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("observed outcome"));
        var publisher = new RecordingPublisher();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence)
        {
            ForcedAuditStatus = GovernedLoopSequentialAuditRecordStatus.Conflict,
        };
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor, publisher), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("canonical_outcome_audit_conflict", result.Run!.FailureCode);
        Assert.Equal(0, result.Run.Checkpoint.NextStepIndex);
        Assert.Single(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_tool_enabled_attempt_retains_approved_authority_and_exact_budget_evidence()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition(allowWorkspaceTools: true)));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("tool-backed outcome", toolCalls: 1));
        executor.BeforeExecute = request => AppendToolTraceAsync(store, request, 1, includeOutcomes: true, ToolApprovalDecision.Approved);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        var request = Assert.Single(executor.Requests);
        Assert.True(request.AllowTools);
        Assert.Equal(
            [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search],
            request.AuthoritySnapshot!.EffectiveAssignments.OrderBy(item => item));
        Assert.Equal(1, result.Run!.Checkpoint.ToolRequestsUsed);
        var governance = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ToolGovernanceDecided).ToolEvidence!.Governance!;
        Assert.Equal(ToolApprovalDecision.Approved, governance.ApprovalDecision);
        Assert.Equal("user-approver", governance.ApprovalDecisionBy);
    }

    [Fact]
    public async Task Canonical_tool_budget_mismatch_is_ambiguous_and_never_advances()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition(allowWorkspaceTools: true)));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("unproved tool outcome", toolCalls: 1));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("provider_result_mismatch", result.Run!.FailureCode);
        Assert.Equal(0, result.Run.Checkpoint.NextStepIndex);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview, evidence.Requests[^1].Disposition);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Canonical_authority_rejection_retains_definitive_evidence_before_provider_dispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition(allowWorkspaceTools: true)));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var admitted = context.Run.AdmittedDefinition.ToolAssignments;
        var narrowerIdentity = Authority(context.Run.AdmittedDefinition.RoleId, [CustomLoopToolAssignment.Read], [CustomLoopToolAssignment.Read]);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, authorityProvider: new FixedAuthorityProvider(narrowerIdentity)),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("invalid_inference_request", result.Run!.FailureCode);
        Assert.Empty(executor.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        var rejection = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Equal(CustomLoopRunEventKind.NodeAttemptFailed, rejection.Kind);
        Assert.Single(evidence.AuditRequests);
        Assert.NotEmpty(admitted);
    }

    [Fact]
    public async Task Canonical_capability_revalidation_failure_retains_definitive_evidence_before_provider_dispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(1)),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("canonical_run_capability_invalid", result.Run!.FailureCode);
        Assert.Empty(executor.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_resume_replays_a_retained_inference_rejection_without_provider_redispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var ledger = new SequentialAuditLedger();
        var firstStore = new FakeRunStore(context.Run);
        var firstExecutor = new QueueExecutor(Result("must not run"));
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence, ledger)
        {
            AfterAuditRecord = () => throw new IOException("Simulated process loss after the durable inference-rejection audit."),
        };
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(firstStore, firstExecutor, capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(1)),
            firstEvidence,
            firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        var retained = firstStore.Writes.Last(item => item.Status == CustomLoopRunStatus.Running
            && item.Events.Any(runEvent => runEvent.Kind == CustomLoopRunEventKind.NodeAttemptFailed
                && runEvent.StepId == "infer-01"
                && runEvent.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection }));
        Assert.Single(ledger.Records);
        var resumable = await RecoverForExplicitResumeAsync(retained, "resume-inference-rejection");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedPublisher = new RecordingPublisher();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence, ledger);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(resumedStore, resumedExecutor, resumedPublisher),
            resumedEvidence,
            resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-inference-rejection",
            AuditSchema.Actors.Cli));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("canonical_inference_rejected", result.Run!.FailureCode);
        Assert.Empty(firstExecutor.Requests);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Empty(resumedPublisher.Requests);
        Assert.Single(ledger.Records);
        Assert.Single(resumedEvidence.AuditRequests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, resumedEvidence.Requests[^1].Disposition);
    }

    [Fact]
    public async Task Restart_recovery_parks_a_retained_inference_rejection_before_its_audit_for_evidence_only_resume()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        CustomLoopRunRecord? retainedRejection = null;
        var firstStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedRejection is null
                    && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed
                        && item.StepId == "infer-01"
                        && item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection }))
                {
                    retainedRejection = candidate;
                    throw new IOException("Simulated process loss after rejection retention and before its audit.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result("must not run"));
        var ledger = new SequentialAuditLedger();
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence, ledger);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(firstStore, firstExecutor, capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(1)),
            firstEvidence,
            firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Empty(ledger.Records);
        var resumable = await RecoverForExplicitResumeAsync(Assert.IsType<CustomLoopRunRecord>(retainedRejection), "resume-inference-rejection-before-audit");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence, ledger);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(resumedStore, resumedExecutor), resumedEvidence, resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-inference-rejection-before-audit",
            AuditSchema.Actors.Cli));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("canonical_inference_rejected", result.Run!.FailureCode);
        Assert.Empty(firstExecutor.Requests);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Single(ledger.Records);
        Assert.Single(resumedEvidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_resume_replays_a_retained_exit_rejection_without_provider_or_publication_redispatch()
    {
        var conversation = new CustomLoopConversationReference("conversation-one", "version-one", _now);
        var context = await SequentialContextAsync(Run(SequentialDefinition(includeConversation: true), conversation));
        var ledger = new SequentialAuditLedger();
        var firstStore = new FakeRunStore(context.Run);
        var firstExecutor = new QueueExecutor(Result("inference outcome"));
        var firstPublisher = new RecordingPublisher();
        var auditCount = 0;
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence, ledger)
        {
            AfterAuditRecord = () => ++auditCount == 2
                ? Task.FromException(new IOException("Simulated process loss after the durable Exit-rejection audit."))
                : Task.CompletedTask,
        };
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                firstStore,
                firstExecutor,
                firstPublisher,
                capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(3)),
            firstEvidence,
            firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        var retained = firstStore.Writes.Last(item => item.Status == CustomLoopRunStatus.Running
            && item.Events.Any(runEvent => runEvent.Kind == CustomLoopRunEventKind.NodeAttemptFailed
                && runEvent.StepId == "exit"
                && runEvent.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection }));
        Assert.Equal(2, ledger.Records.Count);
        var resumable = ResumeReady(retained, "resume-exit-rejection");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedPublisher = new RecordingPublisher();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence, ledger);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(resumedStore, resumedExecutor, resumedPublisher),
            resumedEvidence,
            resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-exit-rejection",
            AuditSchema.Actors.Cli));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("canonical_exit_rejected", result.Run!.FailureCode);
        Assert.Single(firstExecutor.Requests);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Empty(firstPublisher.Requests);
        Assert.Empty(resumedPublisher.Requests);
        Assert.Equal(2, ledger.Records.Count);
        Assert.Single(resumedEvidence.AuditRequests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, resumedEvidence.Requests[^1].Disposition);
    }

    [Fact]
    public async Task Canonical_inference_deadline_retains_definitive_rejection_and_terminal_replay_never_redispatches()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition(includeConversation: true), new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var publisher = new RecordingPublisher();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var time = new FinalDispatchDeadlineTimeProvider(_now, store, reportDeadlineReached: true);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, publisher, timeProvider: time),
            evidence,
            evidence);
        var request = new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web);

        var result = await adapter.RunAsync(request);

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("run_deadline_exceeded", result.Run!.FailureCode);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        var rejection = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            Disposition: CustomLoopSequentialNodeDisposition.Rejected,
        });
        Assert.Equal("infer-01", rejection.SequentialNodeEvidence!.NodeId);
        Assert.Single(evidence.AuditRequests);

        var replay = await adapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            result.Run.LifecycleVersion,
            "resume-terminal-deadline",
            AuditSchema.Actors.Cli));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, replay.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_deterministic_exit_capability_failure_retains_rejected_exit_evidence()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("inference outcome"));
        var publisher = new RecordingPublisher();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                executor,
                publisher,
                capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(3)),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("canonical_exit_capability_check_failed", result.Run!.FailureCode);
        Assert.Single(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        var rejection = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            Disposition: CustomLoopSequentialNodeDisposition.Rejected,
            NodeId: "exit",
        });
        Assert.Equal(CustomLoopRunEventKind.NodeAttemptFailed, rejection.Kind);
        Assert.Equal(2, evidence.AuditRequests.Count);
    }

    [Fact]
    public async Task Canonical_deadline_before_deterministic_exit_is_a_run_boundary_and_never_dispatches_the_exit_node()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("inference outcome"));
        var publisher = new RecordingPublisher();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, publisher, timeProvider: new CanonicalExitBoundaryDeadlineTimeProvider(_now, store)),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("run_deadline_exceeded", result.Run!.FailureCode);
        Assert.Single(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Equal(["trigger", "infer-01"], evidence.Requests.Select(item => item.Dispatch.Node.NodeId));
        Assert.DoesNotContain(result.Run.Events, item => item.StepId == "exit");
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_adapter_honors_pre_dispatch_cancellation_without_node_or_provider_dispatch()
    {
        var admitted = Run(SequentialDefinition());
        var context = await SequentialContextAsync(admitted);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token));

        Assert.Empty(executor.Requests);
        Assert.Empty(evidence.Requests);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Canonical_caller_cancellation_during_inference_assembly_closes_rejected_evidence_without_provider_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var context = await SequentialContextAsync(Run(SequentialDefinition(allowWorkspaceTools: true)));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var publisher = new RecordingPublisher();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, publisher, authorityProvider: new CancellingAuthorityProvider(cancellation, cancelOnCall: 1)),
            evidence,
            evidence);

        var result = await adapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token);

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Cancelled, $"{result.Detail} Failure: {result.Run?.FailureCode}/{result.Run?.FailureDetail}. Validation: {string.Join("; ", store.ValidationFailures.Select(item => item.Code + ": " + item.Message))}");
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_resume_replays_a_retained_cancellation_rejection_without_provider_redispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var context = await SequentialContextAsync(Run(SequentialDefinition(allowWorkspaceTools: true)));
        var ledger = new SequentialAuditLedger();
        var firstStore = new FakeRunStore(context.Run);
        var firstExecutor = new QueueExecutor(Result("must not run"));
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence, ledger)
        {
            AfterAuditRecord = () => throw new IOException("Simulated process loss after the durable cancellation-rejection audit."),
        };
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(firstStore, firstExecutor, authorityProvider: new CancellingAuthorityProvider(cancellation, cancelOnCall: 1)),
            firstEvidence,
            firstEvidence);

        _ = await firstAdapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token);

        var retained = firstStore.Writes.Last(item => item.Status == CustomLoopRunStatus.Running
            && item.Events.Any(runEvent => runEvent.Kind == CustomLoopRunEventKind.NodeAttemptFailed
                && runEvent.StepId == "infer-01"
                && runEvent.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection }));
        Assert.Empty(firstExecutor.Requests);
        Assert.Single(ledger.Records);
        var resumable = ResumeReady(retained, "resume-cancellation-rejection");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence, ledger);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(resumedStore, resumedExecutor), resumedEvidence, resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-cancellation-rejection",
            AuditSchema.Actors.Cli));

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Single(ledger.Records);
        Assert.Single(resumedEvidence.AuditRequests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, resumedEvidence.Requests[^1].Disposition);
    }

    [Fact]
    public async Task Canonical_caller_cancellation_during_attempt_start_audit_closes_rejected_evidence_without_provider_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog
        {
            BeforeAppend = (auditEvent, token) =>
            {
                if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            },
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor, publisher, audit), evidence, evidence);

        var result = await adapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token);

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Cancelled, $"{result.Detail} Failure: {result.Run?.FailureCode}/{result.Run?.FailureDetail}. Validation: {string.Join("; ", store.ValidationFailures.Select(item => item.Code + ": " + item.Message))}");
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_caller_cancellation_after_the_final_control_refresh_closes_rejected_evidence_without_provider_invocation()
    {
        using var cancellation = new CancellationTokenSource();
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
        };
        var publisher = new RecordingPublisher();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor, publisher), evidence, evidence);

        var result = await adapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token);

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Cancelled, result.Detail);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.Empty(publisher.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        var rejection = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Contains("Caller cancellation rejected", rejection.Detail, StringComparison.Ordinal);
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_durable_cancel_after_the_final_control_refresh_closes_rejected_evidence_without_provider_invocation()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, publisher, audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? cancel = null;
        executor.BeforeProviderRequestStarted = async _ =>
        {
            cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-canonical-after-refresh", AuditSchema.Actors.Web));
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(runner, evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.True(result.Status == CustomLoopOrderedRunStatus.Cancelled, result.Detail);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.Empty(publisher.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        var rejection = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Contains("Durable cancellation rejected", rejection.Detail, StringComparison.Ordinal);
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_durable_cancel_at_the_final_dispatch_boundary_closes_rejected_evidence_without_provider_dispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("must not run"));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, publisher, audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(runner, evidence, evidence);
        CustomLoopControlResult? cancel = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
            {
                cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-canonical-final-boundary", AuditSchema.Actors.Web));
            }
        };

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.True(result.Status == CustomLoopOrderedRunStatus.Cancelled, $"{result.Detail} Failure: {result.Run?.FailureCode}/{result.Run?.FailureDetail}. Validation: {string.Join("; ", store.ValidationFailures.Select(item => item.Code + ": " + item.Message))}");
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, evidence.Requests[^1].Disposition);
        Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Single(evidence.AuditRequests);
    }

    [Fact]
    public async Task Canonical_durable_pause_at_the_final_dispatch_boundary_resumes_with_the_next_attempt()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var ledger = new SequentialAuditLedger();
        var firstStore = new FakeRunStore(context.Run);
        var firstExecutor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog();
        var firstRunner = Runner(firstStore, firstExecutor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(firstStore, new FakeControlOperationStore(), firstRunner, new AvailableModel(), firstRunner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence, ledger);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(firstRunner, firstEvidence, firstEvidence);
        CustomLoopControlResult? pause = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
            {
                pause = await lifecycle.PauseAsync(new CustomLoopPauseRequest(firstStore.Current.Id, firstStore.Current.LifecycleVersion, "pause-canonical-final-boundary", AuditSchema.Actors.Web));
            }
        };

        var paused = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.PauseRequested, pause!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, paused.Status);
        Assert.Empty(firstExecutor.Requests);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, firstEvidence.Requests[^1].Disposition);
        var firstRejection = Assert.Single(paused.Run!.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection });
        Assert.Equal(1, firstRejection.Attempt);

        var resumable = ResumeReady(paused.Run, "resume-canonical-paused-attempt");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor(Result("resumed outcome"));
        var resumedAudit = new RecordingAuditLog();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence, ledger);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(resumedStore, resumedExecutor, audit: resumedAudit), resumedEvidence, resumedEvidence);

        var resumed = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-canonical-paused-attempt",
            AuditSchema.Actors.Cli));

        Assert.True(resumed.Status == CustomLoopOrderedRunStatus.Completed, resumed.Detail);
        Assert.Single(resumedExecutor.Requests);
        Assert.Equal(2, resumedExecutor.Requests[0].Attempt);
        Assert.Equal([1, 2], resumed.Run!.Events
            .Where(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted && item.StepId == "infer-01")
            .Select(item => item.Attempt));
        var resumedStartAudit = Assert.Single(resumedAudit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Started);
        Assert.Equal(2, Assert.IsType<int>(resumedStartAudit.Metadata["attempt"]));
        Assert.Single(resumed.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01" && item.Attempt == 2);
        Assert.DoesNotContain(resumed.Run.Events, item => item.ToolEvidence is not null);
    }

    [Fact]
    public async Task Canonical_resume_reconciles_retained_ordered_outcome_without_provider_redispatch()
    {
        var admitted = Run(SequentialDefinition());
        var context = await SequentialContextAsync(admitted);
        CustomLoopRunRecord? retainedOutcome = null;
        var crashingStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedOutcome is null
                    && candidate.Checkpoint.NextStepIndex == 0
                    && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01"))
                {
                    retainedOutcome = candidate;
                    throw new IOException("Simulated process loss after ordered outcome retention.");
                }

                return Task.CompletedTask;
            }
        };
        var firstExecutor = new QueueExecutor(Result("retained outcome"));
        var firstEvidence = new SequentialEvidenceHarness(crashingStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(crashingStore, firstExecutor), firstEvidence, firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        var retained = Assert.IsType<CustomLoopRunRecord>(retainedOutcome);
        var resumeOperationId = "resume-retained-outcome";
        var resumable = await RecoverForExplicitResumeAsync(retained, resumeOperationId);
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedAudit = new RecordingAuditLog();
        var evidence = new SequentialEvidenceHarness(resumedStore, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(resumedStore, resumedExecutor, audit: resumedAudit),
            evidence,
            evidence);

        var result = await adapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            resumeOperationId,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Equal(["infer-01", "exit"], evidence.Requests.Select(item => item.Dispatch.Node.NodeId));
        Assert.Equal(1, result.Run!.Checkpoint.NextStepIndex);
        Assert.Equal("retained outcome", result.Run.FinalOutput);
        Assert.Single(firstExecutor.Requests);
        var recoveredAudit = Assert.Single(evidence.AuditRequests, item => item.AuditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && item.AuditEvent.Outcome == AuditSchema.Outcomes.Succeeded).AuditEvent;
        var recoveredEvidence = result.Run.Events.Single(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01").SequentialNodeEvidence!;
        Assert.Equal(recoveredEvidence.NodeId, recoveredAudit.Metadata["canonicalNodeId"]);
        Assert.Equal(recoveredEvidence.EvidenceHash, recoveredAudit.Metadata["sequentialEvidenceHash"]);
    }

    [Fact]
    public async Task Restart_recovery_quarantines_a_genuinely_open_canonical_attempt_without_provider_redispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        CustomLoopRunRecord? retainedStart = null;
        var crashingStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedStart is null
                    && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted
                        && item.StepId == "infer-01"
                        && item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted }))
                {
                    retainedStart = candidate;
                    throw new IOException("Simulated process loss after dispatch-start retention.");
                }

                return Task.CompletedTask;
            },
        };
        var executor = new QueueExecutor(Result("must not run"));
        var evidence = new SequentialEvidenceHarness(crashingStore, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(crashingStore, executor), evidence, evidence);

        _ = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        var interrupted = Assert.IsType<CustomLoopRunRecord>(retainedStart);
        Assert.Empty(executor.Requests);
        var recoveryStore = new FakeRunStore(interrupted);
        var recoveryAudit = new RecordingAuditLog();
        var recovered = Assert.Single(await new CustomLoopRecoveryService(recoveryStore, recoveryAudit, new FixedTimeProvider(interrupted.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.NeedsReview, recovered.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, recoveryStore.Current.Status);
        Assert.Equal("recovery_open_attempt", recoveryStore.Current.FailureCode);
        Assert.All(recoveryAudit.Events, item => Assert.Equal(true, item.Metadata["openAttemptAfterCheckpoint"]));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Restart_recovery_quarantines_a_canonical_terminal_with_substituted_iteration_coordinates()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        CustomLoopRunRecord? retainedOutcome = null;
        var crashingStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedOutcome is null
                    && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted
                        && item.StepId == "infer-01"
                        && item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome }))
                {
                    retainedOutcome = candidate;
                    throw new IOException("Simulated process loss after canonical outcome retention.");
                }

                return Task.CompletedTask;
            },
        };
        var executor = new QueueExecutor(Result("retained outcome"));
        var evidence = new SequentialEvidenceHarness(crashingStore, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(crashingStore, executor), evidence, evidence);

        _ = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        var interrupted = Assert.IsType<CustomLoopRunRecord>(retainedOutcome);
        var events = interrupted.Events.ToArray();
        var terminalIndex = Array.FindIndex(events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome }
            && string.Equals(item.StepId, "infer-01", StringComparison.Ordinal));
        var terminal = events[terminalIndex];
        var substituted = terminal with { Iteration = terminal.Iteration!.Value + 1 };
        var terminalEvidence = terminal.SequentialNodeEvidence!;
        substituted = substituted with
        {
            SequentialNodeEvidence = CustomLoopSequentialNodeEvidenceHash.Apply(terminalEvidence with
            {
                OutcomeArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(substituted),
                EvidenceHash = string.Empty,
            }),
        };
        events[terminalIndex] = substituted;
        var malformed = interrupted with { Events = events };
        var validation = CustomLoopRunValidator.Validate(malformed);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var recoveryStore = new FakeRunStore(malformed);
        var recoveryAudit = new RecordingAuditLog();

        var recovered = Assert.Single(await new CustomLoopRecoveryService(recoveryStore, recoveryAudit, new FixedTimeProvider(malformed.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.NeedsReview, recovered.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, recoveryStore.Current.Status);
        Assert.Equal("recovery_open_attempt", recoveryStore.Current.FailureCode);
        Assert.All(recoveryAudit.Events, item => Assert.Equal(true, item.Metadata["openAttemptAfterCheckpoint"]));
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Canonical_resume_reconciles_tool_enabled_outcome_from_persisted_authority_without_provider_or_tool_redispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition(allowWorkspaceTools: true)));
        CustomLoopRunRecord? retainedOutcome = null;
        var crashingStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedOutcome is null
                    && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01"))
                {
                    retainedOutcome = candidate;
                    throw new IOException("Simulated process loss after tool-enabled outcome retention.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result("tool-backed retained outcome", toolCalls: 1));
        firstExecutor.BeforeExecute = request => AppendToolTraceAsync(crashingStore, request, 1, includeOutcomes: true, ToolApprovalDecision.Approved);
        var firstEvidence = new SequentialEvidenceHarness(crashingStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(crashingStore, firstExecutor), firstEvidence, firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        var retained = Assert.IsType<CustomLoopRunRecord>(retainedOutcome);
        var retainedToolEvents = retained.Events.Count(item => item.ToolEvidence is not null);
        var resumable = ResumeReady(retained, "resume-tool-enabled-outcome");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(resumedStore, resumedExecutor), resumedEvidence, resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-tool-enabled-outcome",
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Single(firstExecutor.Requests);
        Assert.Equal(retainedToolEvents, result.Run!.Events.Count(item => item.ToolEvidence is not null));
        Assert.Equal(1, result.Run.Checkpoint.ToolRequestsUsed);
        Assert.Single(resumedEvidence.AuditRequests, item => item.AuditEvent.Action == AuditSchema.Actions.LoopNodeAttempt);
    }

    [Fact]
    public async Task Canonical_resume_replays_a_durable_append_once_audit_without_duplicate_record_or_provider_dispatch()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()));
        var ledger = new SequentialAuditLedger();
        var firstStore = new FakeRunStore(context.Run);
        var firstExecutor = new QueueExecutor(Result("append-once outcome"));
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence, ledger)
        {
            AfterAuditRecord = () => throw new IOException("Simulated process loss after the durable audit commit."),
        };
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(firstStore, firstExecutor), firstEvidence, firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        var retained = firstStore.Writes.Last(item => item.Status == CustomLoopRunStatus.Running
            && item.Checkpoint.NextStepIndex == 0
            && item.Events.Any(runEvent => runEvent.Kind == CustomLoopRunEventKind.NodeAttemptCompleted));
        var terminalEvidence = retained.Events.Single(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted).SequentialNodeEvidence!;
        Assert.Single(ledger.Records);
        Assert.Equal(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(terminalEvidence.EvidenceHash),
            Assert.Single(ledger.Records).Key);

        var resumable = await RecoverForExplicitResumeAsync(retained, "resume-append-once-audit");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence, ledger);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(resumedStore, resumedExecutor), resumedEvidence, resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-append-once-audit",
            AuditSchema.Actors.Cli));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Single(firstExecutor.Requests);
        Assert.Equal(2, ledger.Records.Count);
        Assert.Single(ledger.Records, item => string.Equals(
            item.Key,
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(terminalEvidence.EvidenceHash),
            StringComparison.Ordinal));
        var replay = Assert.Single(resumedEvidence.AuditRequests, item => item.AuditEvent.Action == AuditSchema.Actions.LoopNodeAttempt);
        Assert.Equal(terminalEvidence.EvidenceHash, replay.EvidenceHash);
        Assert.Equal(retained.Events.Single(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted).TimestampUtc, replay.AuditEvent.TimestampUtc);
        Assert.Equal(context.Run.AdmissionActor, replay.AuditEvent.Actor);
    }

    [Fact]
    public async Task Canonical_resume_reaudits_retained_exit_then_retries_one_stable_publication_without_provider_redispatch()
    {
        var conversation = new CustomLoopConversationReference("conversation-one", "version-one", _now);
        var context = await SequentialContextAsync(Run(SequentialDefinition(includeConversation: true), conversation));
        CustomLoopRunRecord? retainedExit = null;
        var crashed = false;
        var crashingStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (!crashed
                    && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted
                        && item.SequentialNodeEvidence is { Disposition: CustomLoopSequentialNodeDisposition.Completed }))
                {
                    crashed = true;
                    retainedExit = candidate;
                    throw new IOException("Simulated process loss after the durable Exit outcome and before its audit.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result("retained exit output"));
        var firstEvidence = new SequentialEvidenceHarness(crashingStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(crashingStore, firstExecutor),
            firstEvidence,
            firstEvidence);

        var firstResult = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(retainedExit is not null, firstResult.Status + "/" + firstResult.Run?.FailureCode + ": " + firstResult.Detail + "\n" + string.Join(Environment.NewLine, crashingStore.ValidationFailures));
        var resumable = ResumeReady(retainedExit, "resume-retained-exit");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedPublisher = new RecordingPublisher();
        var resumedAudit = new RecordingAuditLog();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(resumedStore, resumedExecutor, resumedPublisher, resumedAudit),
            resumedEvidence,
            resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-retained-exit",
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        Assert.Empty(resumedExecutor.Requests);
        var publication = Assert.Single(resumedPublisher.Requests);
        Assert.Equal("retained exit output", publication.CanonicalOutput);
        Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted);
        Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        var exitAudit = Assert.Single(resumedEvidence.AuditRequests, item => item.AuditEvent.Action == AuditSchema.Actions.LoopExitDecision).AuditEvent;
        var exitEvidence = result.Run.Events.Single(item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted).SequentialNodeEvidence!;
        Assert.Equal(exitEvidence.NodeId, exitAudit.Metadata["canonicalNodeId"]);
        Assert.Equal(exitEvidence.EvidenceHash, exitAudit.Metadata["sequentialEvidenceHash"]);
        Assert.Single(firstExecutor.Requests);
    }

    [Fact]
    public async Task Canonical_exit_prepublication_capability_denial_replays_without_provider_or_publication_redispatch()
    {
        var conversation = new CustomLoopConversationReference("conversation-one", "version-one", _now);
        var context = await SequentialContextAsync(Run(SequentialDefinition(includeConversation: true), conversation));
        var ledger = new SequentialAuditLedger();
        var firstStore = new FakeRunStore(context.Run);
        var firstExecutor = new QueueExecutor(Result("publication-denied output"));
        var firstPublisher = new RecordingPublisher();
        var firstEvidence = new SequentialEvidenceHarness(firstStore, context.Evidence, ledger);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                firstStore,
                firstExecutor,
                firstPublisher,
                capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(4)),
            firstEvidence,
            firstEvidence);

        var denied = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, denied.Status);
        Assert.Equal("capability_revalidation_check_failed_before_publication", denied.Run!.FailureCode);
        Assert.Single(firstExecutor.Requests);
        Assert.Empty(firstPublisher.Requests);
        var preTerminal = firstStore.Writes.Last(item => item.Status == CustomLoopRunStatus.Running
            && item.Events.Any(runEvent => runEvent.Kind == CustomLoopRunEventKind.ExitDecisionCompleted)
            && item.Events.Any(runEvent => runEvent.Kind == CustomLoopRunEventKind.ConversationPublicationStarted));

        var resumable = ResumeReady(preTerminal, "resume-publication-capability-denial");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedPublisher = new RecordingPublisher();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence, ledger);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                resumedStore,
                resumedExecutor,
                resumedPublisher,
                capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(1)),
            resumedEvidence,
            resumedEvidence);

        var replay = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-publication-capability-denial",
            AuditSchema.Actors.Cli));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, replay.Status);
        Assert.Equal("capability_revalidation_check_failed_before_publication", replay.Run!.FailureCode);
        Assert.Empty(resumedExecutor.Requests);
        Assert.Empty(resumedPublisher.Requests);
        Assert.Single(firstExecutor.Requests);
        Assert.Single(replay.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted);
        Assert.Single(resumedEvidence.AuditRequests, item => item.AuditEvent.Action == AuditSchema.Actions.LoopExitDecision);
    }

    [Fact]
    public async Task Canonical_resume_reuses_publication_intent_and_durable_outcome_without_duplicate_append_or_provider_dispatch()
    {
        var conversation = new CustomLoopConversationReference("conversation-one", "version-one", _now);
        var context = await SequentialContextAsync(Run(SequentialDefinition(includeConversation: true), conversation));
        CustomLoopRunRecord? retainedIntent = null;
        var crashed = false;
        var intentStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (!crashed && candidate.Events[^1].Kind == CustomLoopRunEventKind.ConversationPublicationStarted)
                {
                    crashed = true;
                    retainedIntent = candidate;
                    throw new IOException("Simulated process loss after durable publication intent.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result("stable publication output"));
        var firstPublisher = new RecordingPublisher();
        var firstEvidence = new SequentialEvidenceHarness(intentStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(intentStore, firstExecutor, firstPublisher),
            firstEvidence,
            firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Empty(firstPublisher.Requests);
        var intentResumable = ResumeReady(Assert.IsType<CustomLoopRunRecord>(retainedIntent), "resume-publication-intent");
        CustomLoopRunRecord? retainedOutcome = null;
        var outcomeCrashed = false;
        var outcomeStore = new FakeRunStore(intentResumable)
        {
            AfterUpdate = candidate =>
            {
                if (!outcomeCrashed && candidate.Events[^1].Kind == CustomLoopRunEventKind.ConversationPublished)
                {
                    outcomeCrashed = true;
                    retainedOutcome = candidate;
                    throw new IOException("Simulated process loss after durable publication outcome.");
                }

                return Task.CompletedTask;
            },
        };
        var resumedPublisher = new RecordingPublisher();
        var outcomeEvidence = new SequentialEvidenceHarness(outcomeStore, context.Evidence);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(outcomeStore, new QueueExecutor(), resumedPublisher),
            outcomeEvidence,
            outcomeEvidence);

        var intentResult = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            intentResumable.LifecycleVersion,
            "resume-publication-intent",
            AuditSchema.Actors.Web));

        var published = Assert.Single(resumedPublisher.Requests);
        Assert.Equal(intentResumable.Events.Single(item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted).ConversationPublicationId, published.OperationId);
        Assert.NotEqual(CustomLoopOrderedRunStatus.Completed, intentResult.Status);

        var outcomeResumable = ResumeReady(Assert.IsType<CustomLoopRunRecord>(retainedOutcome), "resume-publication-outcome");
        var completedStore = new FakeRunStore(outcomeResumable);
        var noRedispatchPublisher = new RecordingPublisher();
        var completedEvidence = new SequentialEvidenceHarness(completedStore, context.Evidence);
        var completedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(completedStore, new QueueExecutor(), noRedispatchPublisher),
            completedEvidence,
            completedEvidence);

        var outcomeResult = await completedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            outcomeResumable.LifecycleVersion,
            "resume-publication-outcome",
            AuditSchema.Actors.Web));

        Assert.True(outcomeResult.Status == CustomLoopOrderedRunStatus.Completed, outcomeResult.Detail);
        Assert.Empty(noRedispatchPublisher.Requests);
        Assert.Single(outcomeResult.Run!.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted);
        Assert.Single(outcomeResult.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
    }

    [Fact]
    public async Task Run_executes_inference_steps_in_persisted_order_and_completes_without_an_Exit_call_when_disabled()
    {
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0,
            tools: [CustomLoopToolAssignment.Read]);
        var store = new FakeRunStore(Run(definition));
        var audit = new RecordingAuditLog();
        var executor = new QueueExecutor(
            Result("first retained output", toolCalls: 2),
            Result("final output", toolCalls: 1));
        executor.BeforeExecute = request =>
        {
            Assert.Equal(CustomLoopRunEventKind.NodeAttemptStarted, store.Current.Events[^1].Kind);
            Assert.Contains(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Started);
            return Task.CompletedTask;
        };
        executor.AfterExecute = request => AppendToolTraceAsync(store, request, request.StepId == "step-first" ? 2 : 1, includeOutcomes: true);
        var runner = Runner(store, executor, audit: audit);

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, string.Join(Environment.NewLine, store.ValidationFailures));
        Assert.True(result.ProviderWasInvoked);
        Assert.Equal(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Equal("final output", result.Run.FinalOutput);
        Assert.Equal(["step-first", "step-second"], executor.Requests.Select(item => item.StepId));
        Assert.All(executor.Requests, request => Assert.False(request.IsExit));
        Assert.All(executor.Requests, request => Assert.Equal([CustomLoopToolAssignment.Read], request.AdmittedToolAssignments));
        Assert.Equal([0, 2], executor.Requests.Select(item => item.ToolRequestsUsedInRun));
        Assert.Equal(3, result.Run.Checkpoint.ToolRequestsUsed);
        Assert.Contains(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("first retained output", StringComparison.Ordinal));
        Assert.DoesNotContain(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("provider-thread", StringComparison.Ordinal));
        var deterministicExit = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
        Assert.Equal(CustomLoopExitDecision.Complete, deterministicExit.ExitDecision);
        Assert.Contains("disabled", deterministicExit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionStarted);
        Assert.Contains(audit.Events, item => item.Action == AuditSchema.Actions.LoopRunLifecycle && item.Metadata.ContainsKey("terminalStatus"));
    }

    [Fact]
    public async Task Exact_capability_drift_after_run_start_fails_before_provider_dispatch_and_preserves_admission_evidence()
    {
        var admitted = Run(Definition());
        var admissionJson = JsonSerializer.Serialize(admitted.CapabilityAdmission);
        var store = new FakeRunStore(admitted);
        var executor = new QueueExecutor(Result("must not run"));
        var capabilities = new TestCapabilityAdmissionService();
        capabilities.RevalidationResults.Enqueue(new CapabilityRevalidationResult(true, admitted.CapabilityAdmission.Pins, "Pins are current at run start."));
        capabilities.RevalidationResults.Enqueue(new CapabilityRevalidationResult(false, [], "The exact provider capability drifted."));

        var result = await Runner(store, executor, capabilityAdmissionService: capabilities).RunAsync(new CustomLoopOrderedRunRequest(admitted.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Equal("invalid_inference_request", result.Run!.FailureCode);
        Assert.Equal(admissionJson, JsonSerializer.Serialize(result.Run.CapabilityAdmission));
    }

    [Fact]
    public async Task Evidence_is_retained_even_when_output_is_not_visible_to_later_nodes_and_the_last_output_still_becomes_the_iteration_result()
    {
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: false, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("evidence only"), Result("iteration result"));
        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, string.Join(Environment.NewLine, store.ValidationFailures));
        Assert.DoesNotContain(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("evidence only", StringComparison.Ordinal));
        var evidence = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved && item.StepId == "step-first");
        Assert.Equal("evidence only", evidence.CanonicalOutput);
        Assert.False(evidence.RetainedForLoopReasoning);
        Assert.Equal("iteration result", result.Run.Checkpoint.CurrentIterationResult!.Content);
        Assert.Equal("iteration result", result.Run.FinalOutput);
    }

    [Fact]
    public async Task Canonical_output_preserves_exact_text_and_is_truncated_once_then_reused_for_context_and_evidence()
    {
        var longOutput = "e\u0301" + new string('x', CustomLoopLimits.MaxCanonicalModelOutputCharacters + 20);
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result(longOutput), Result("final"));

        var runResult = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        var canonical = runResult.Run!.Events.First(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved && item.StepId == "step-first");
        Assert.Equal(CustomLoopLimits.MaxCanonicalModelOutputCharacters, canonical.CanonicalOutput!.Length);
        Assert.Equal(longOutput.Length, canonical.OriginalOutputCharacterCount);
        Assert.True(canonical.CanonicalOutputTruncated);
        Assert.StartsWith("e\u0301", canonical.CanonicalOutput, StringComparison.Ordinal);
        Assert.Contains(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains(canonical.CanonicalOutput, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Canonical_output_truncation_never_splits_a_surrogate_pair()
    {
        var output = new string('x', CustomLoopLimits.MaxCanonicalModelOutputCharacters - 1) + "\U0001F600" + "tail";
        var store = new FakeRunStore(Run(Definition()));

        var result = await Runner(store, new QueueExecutor(Result(output))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        var observed = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved);
        Assert.Equal(CustomLoopLimits.MaxCanonicalModelOutputCharacters - 1, observed.CanonicalOutput!.Length);
        Assert.False(char.IsSurrogate(observed.CanonicalOutput[^1]));
        Assert.True(observed.CanonicalOutputTruncated);
    }

    [Fact]
    public async Task Exact_Repeat_restarts_step_zero_and_carries_the_previous_iteration_result_only_when_Exit_retains_it()
    {
        var exitPolicy = Policy(Output(retain: true, publish: false));
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: exitPolicy,
            tools: [CustomLoopToolAssignment.List]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(
            Result("iteration one"),
            Result("  Repeat\r\n"),
            Result("iteration two"),
            Result("Complete"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(["step-only", "exit", "step-only", "exit"], executor.Requests.Select(item => item.StepId));
        Assert.True(executor.Requests[1].IsExit);
        Assert.False(executor.Requests[1].AllowTools);
        Assert.Empty(executor.Requests[1].AdmittedToolAssignments);
        Assert.Contains(executor.Requests[2].InferenceRequest.Messages, item => item.Content.Contains("iteration one", StringComparison.Ordinal));
        Assert.Equal(1, result.Run!.Checkpoint.AcceptedRepeatCount);
        Assert.Equal(2, result.Run.Checkpoint.Iteration);
        Assert.Equal("iteration two", result.Run.FinalOutput);
    }

    [Fact]
    public async Task Repeat_does_not_carry_the_previous_iteration_result_when_Exit_discards_it()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), Result("Repeat"), Result("iteration two"), Result("Complete"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.DoesNotContain(executor.Requests[2].InferenceRequest.Messages, item => item.Content.Contains("iteration one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeat_ceiling_completes_after_the_final_allowed_iteration_without_another_Exit_model_call()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 1,
            exitPolicy: Policy(Output(retain: true, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), Result("Repeat"), Result("iteration two"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(["step-only", "exit", "step-only"], executor.Requests.Select(item => item.StepId));
        Assert.Equal("iteration two", result.Run!.FinalOutput);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted && item.Detail.Contains("repeat ceiling", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Complete.")]
    [InlineData("Complete Repeat")]
    [InlineData("{\"decision\":\"Complete\"}")]
    [InlineData("~~~Complete~~~")]
    [InlineData("complete")]
    [InlineData("repeat")]
    [InlineData("rEpEaT")]
    [InlineData("Repeat\u00A0")]
    public async Task Invalid_Exit_output_never_repeats_and_becomes_NeedsReview(string decision)
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: Policy(Output(retain: true, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), Result(decision));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal(2, executor.Requests.Count);
        var exit = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
        Assert.Equal(CustomLoopExitDecision.Invalid, exit.ExitDecision);
        Assert.Equal(decision, exit.CanonicalOutput);
    }

    [Fact]
    public async Task Exit_decision_is_validated_against_the_complete_raw_response_when_evidence_is_truncated()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: Policy(Output(retain: true, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var malformed = "Repeat" + new string(' ', CustomLoopLimits.MaxCanonicalModelOutputCharacters - "Repeat".Length) + "unexpected";
        var executor = new QueueExecutor(Result("iteration one"), Result(malformed));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(2, executor.Requests.Count);
        var exit = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
        Assert.True(exit.CanonicalOutputTruncated);
        Assert.Equal(CustomLoopExitDecision.Invalid, exit.ExitDecision);
    }

    [Fact]
    public async Task Malformed_provider_response_id_is_omitted_without_losing_the_observed_outcome()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(new CustomLoopInferenceAttemptResult("completed output", "provider", "model", "malformed\uD800id", 0));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        var observed = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved);
        Assert.Equal("completed output", observed.CanonicalOutput);
        Assert.Null(observed.ProviderResponseId);
    }

    [Fact]
    public async Task Failed_Exit_attempt_has_no_retry_and_becomes_NeedsReview()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: Policy(Output(retain: true, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), new InvalidOperationException("provider down"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed && item.StepId == "exit");
    }

    [Fact]
    public async Task Failed_inference_stops_later_steps_without_retry()
    {
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(new InvalidOperationException("provider down"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.True(result.ProviderWasInvoked);
        Assert.Single(executor.Requests);
        Assert.DoesNotContain(result.Run!.Events, item => item.StepId == "step-second");
    }

    [Fact]
    public async Task Failure_before_the_provider_request_starts_is_not_reported_as_dispatched()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(new InvalidOperationException("transport construction failed")) { MarkProviderRequestStarted = false };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.False(result.ProviderWasInvoked);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Transport_failure_before_provider_dispatch_is_definitive_and_does_not_require_review()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(new IOException("transport construction failed")) { MarkProviderRequestStarted = false };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, result.Run!.Status);
        Assert.Equal("inference_attempt_failed", result.Run.FailureCode);
        Assert.Contains("before dispatch", result.Run.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.ProviderWasInvoked);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("io")]
    [InlineData("wrapped-io")]
    [InlineData("aggregate-timeout")]
    public async Task Transport_failure_after_dispatch_is_uncertain_and_becomes_NeedsReview_without_retry(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "timeout" => new TimeoutException("provider timeout"),
            "io" => new IOException("transport closed"),
            "wrapped-io" => new InvalidOperationException("provider wrapper", new IOException("transport closed")),
            _ => new AggregateException(new InvalidOperationException("definite"), new TimeoutException("provider timeout"))
        };
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var executor = new QueueExecutor(failure);

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("inference_attempt_uncertain", result.Run!.FailureCode);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Outcome_evidence_and_audit_precede_idempotent_publication_which_precedes_the_checkpoint()
    {
        var outputPolicy = Output(retain: false, publish: true);
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", outputPolicy)],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var run = Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now));
        var store = new FakeRunStore(run);
        var audit = new RecordingAuditLog();
        var publisher = new RecordingPublisher();
        publisher.BeforePublish = request =>
        {
            Assert.Equal(CustomLoopRunEventKind.ConversationPublicationStarted, store.Current.Events[^1].Kind);
            Assert.Contains(store.Current.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
            Assert.Contains(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Succeeded);
            return Task.CompletedTask;
        };
        var executor = new QueueExecutor(Result("published output"));

        var result = await Runner(store, executor, publisher, audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        var publicationRequest = Assert.Single(publisher.Requests);
        Assert.Equal("published output", publicationRequest.CanonicalOutput);
        Assert.StartsWith("publish-", publicationRequest.OperationId, StringComparison.Ordinal);
        var observedSequence = result.Run!.Events.Single(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved).Sequence;
        var publishedSequence = result.Run.Events.Single(item => item.Kind == CustomLoopRunEventKind.ConversationPublished).Sequence;
        var checkpointSequence = result.Run.Events.First(item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted).Sequence;
        Assert.True(observedSequence < publishedSequence);
        Assert.True(publishedSequence < checkpointSequence);
    }

    [Fact]
    public async Task Capability_revocation_after_provider_outcome_stops_before_conversation_publication()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition, new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var publisher = new RecordingPublisher();
        var capabilities = new TestCapabilityAdmissionService();
        capabilities.RevalidationResults.Enqueue(new CapabilityRevalidationResult(true, store.Current.CapabilityAdmission.Pins, "Capabilities are current at run start."));
        capabilities.RevalidationResults.Enqueue(new CapabilityRevalidationResult(true, store.Current.CapabilityAdmission.Pins, "Capabilities are current before provider dispatch."));
        capabilities.RevalidationResults.Enqueue(new CapabilityRevalidationResult(false, [], "The admitted capability was disabled."));

        var result = await Runner(store, new QueueExecutor(Result("observed output")), publisher, capabilityAdmissionService: capabilities).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("capability_revalidation_failed_before_publication", result.Run.FailureCode);
        Assert.Empty(publisher.Requests);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
    }

    [Fact]
    public async Task Capability_revalidation_exception_before_conversation_publication_is_definitively_failed()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition, new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var publisher = new RecordingPublisher();

        var runner = Runner(
            store,
            new QueueExecutor(Result("observed output")),
            publisher,
            capabilityAdmissionService: new ThrowingOnRevalidationCapabilityAdmissionService(3));

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, result.Run!.Status);
        Assert.Equal("capability_revalidation_check_failed_before_publication", result.Run.FailureCode);
        Assert.Empty(publisher.Requests);
        Assert.Contains("IOException", result.Run.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sequential_node_and_Exit_publications_advance_from_the_immutable_admission_version_using_durable_prior_outputs()
    {
        var publish = Output(retain: true, publish: true);
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", publish),
                Step("step-second", "Second", "Second instruction", publish)
            ],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: true)));
        var conversation = new CustomLoopConversationReference("conversation-one", "immutable-admission-version", _now);
        var store = new FakeRunStore(Run(definition, conversation: conversation));
        var publisher = new RecordingPublisher();

        var result = await Runner(store, new QueueExecutor(Result("first output"), Result("second output")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(3, publisher.Requests.Count);
        Assert.All(publisher.Requests, request => Assert.Equal(conversation.CapturedVersion, request.ExpectedConversationVersion));
        Assert.Empty(publisher.Requests[0].PriorPublications!);
        Assert.Collection(
            publisher.Requests[1].PriorPublications!,
            prior => Assert.Equal("first output", prior.CanonicalOutput));
        Assert.Collection(
            publisher.Requests[2].PriorPublications!,
            prior => Assert.Equal("first output", prior.CanonicalOutput),
            prior => Assert.Equal("second output", prior.CanonicalOutput));
        Assert.Equal(
            ["first output", "second output", "second output"],
            result.Run!.Events.Where(item => item is { Kind: CustomLoopRunEventKind.ConversationPublished, PublishedToInvokingConversation: true }).Select(item => item.CanonicalOutput));
    }

    [Fact]
    public async Task Inference_step_named_exit_and_synthetic_Exit_use_distinct_publication_operation_ids()
    {
        var publish = Output(retain: true, publish: true);
        var definition = Definition(
            steps: [Step("exit", "User-authored exit", "Do the work", publish)],
            maxAdditionalIterations: 1,
            exitPolicy: Policy(publish));
        var conversation = new CustomLoopConversationReference("conversation-one", "immutable-admission-version", _now);
        var store = new FakeRunStore(Run(definition, conversation: conversation));
        var publisher = new RecordingPublisher();

        var result = await Runner(store, new QueueExecutor(Result("inference output"), Result("Complete")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(2, publisher.Requests.Count);
        Assert.NotEqual(publisher.Requests[0].OperationId, publisher.Requests[1].OperationId);
        var prior = Assert.Single(publisher.Requests[1].PriorPublications!);
        Assert.Equal(publisher.Requests[0].OperationId, prior.OperationId);
    }

    [Fact]
    public async Task Selected_publication_without_a_bound_destination_is_a_recorded_omission_and_never_calls_the_publisher()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var publisher = new RecordingPublisher();

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Empty(publisher.Requests);
        var omitted = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        Assert.False(omitted.PublishedToInvokingConversation);
        Assert.Contains("omitted", omitted.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CustomLoopConversationPublicationOutcome.DefinitelyFailed, CustomLoopOrderedRunStatus.Failed)]
    [InlineData(CustomLoopConversationPublicationOutcome.Uncertain, CustomLoopOrderedRunStatus.NeedsReview)]
    [InlineData((CustomLoopConversationPublicationOutcome)0, CustomLoopOrderedRunStatus.NeedsReview)]
    public async Task Publication_failure_is_durable_and_never_reported_as_success(CustomLoopConversationPublicationOutcome outcome, CustomLoopOrderedRunStatus expected)
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var run = Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now));
        var store = new FakeRunStore(run);
        var publisher = new RecordingPublisher { NextResult = new CustomLoopConversationPublicationResult(outcome, null, "safe publication detail") };

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(expected, result.Status);
        Assert.NotEqual(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Fact]
    public async Task Cancellation_while_waiting_before_publication_append_is_not_marked_uncertain()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var run = Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now));
        var store = new FakeRunStore(run);
        CustomLoopOrderedRunner? runner = null;
        var publisher = new RecordingPublisher
        {
            BeforePublish = request =>
            {
                runner!.CancelActiveAttempt(request.RunId);
                return Task.CompletedTask;
            }
        };
        runner = Runner(store, new QueueExecutor(Result("evidence")), publisher);

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("publication_cancelled_before_dispatch", result.Run!.FailureCode);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
    }

    [Fact]
    public async Task Routed_cancellation_reaches_conversation_publication_before_append()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var run = Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now));
        var store = new FakeRunStore(run);
        var broker = new RecordingAttemptCancellationBroker();
        CustomLoopOrderedRunner? runner = null;
        Task<CustomLoopAttemptCancellationResult>? signal = null;
        var publisher = new RecordingPublisher
        {
            BeforePublish = request =>
            {
                signal = runner!.RequestActiveAttemptCancellationAsync(request.RunId, "cancel-publication");
                return Task.CompletedTask;
            }
        };
        runner = new CustomLoopOrderedRunner(
            store,
            new CustomLoopContextResolver(),
            new QueueExecutor(Result("evidence")),
            publisher,
            new RecordingAuditLog(),
            new TestAuthorityProvider(),
            new FixedTimeProvider(_now),
            broker,
            new TestCapabilityAdmissionService());

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));
        var signalResult = await signal!;

        Assert.Equal(2, broker.RegistrationCount);
        Assert.Equal(CustomLoopAttemptCancellationStatus.SignalDelivered, signalResult.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("publication_cancelled_before_dispatch", result.Run!.FailureCode);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
    }

    [Fact]
    public async Task Missing_started_audit_blocks_provider_dispatch()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var executor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog { FailPredicate = item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Started };

        var result = await Runner(store, executor, audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
    }

    [Fact]
    public async Task Missing_outcome_audit_stops_before_publication_and_checkpoint_and_requires_review()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do", Output(false, true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog { FailPredicate = item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Succeeded };

        var result = await Runner(store, new QueueExecutor(Result("observed")), publisher, audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Empty(publisher.Requests);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Fact]
    public async Task Unsupported_publication_outcome_conflict_escalates_because_the_external_append_may_exist()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)))
        {
            ConflictOnPublicationWrite = true
        };
        var publisher = new RecordingPublisher { NextResult = new CustomLoopConversationPublicationResult((CustomLoopConversationPublicationOutcome)999, null, "Unsupported outcome.") };

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("post_outcome_persistence_conflict", result.Run.FailureCode);
        Assert.Single(publisher.Requests);
    }

    [Fact]
    public async Task Null_publication_result_is_recorded_as_uncertain_and_requires_review()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var run = Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now));
        var store = new FakeRunStore(run);
        var publisher = new RecordingPublisher { ReturnNull = true };

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("conversation_publication_uncertain", result.Run!.FailureCode);
        var publication = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        Assert.False(publication.PublishedToInvokingConversation);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Theory]
    [InlineData(CustomLoopConversationPublicationOutcome.Published)]
    [InlineData(CustomLoopConversationPublicationOutcome.AlreadyPublished)]
    public async Task Mismatched_publication_operation_id_is_not_accepted_as_success(CustomLoopConversationPublicationOutcome outcome)
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var publisher = new RecordingPublisher { NextResult = new CustomLoopConversationPublicationResult(outcome, "publish-unrelated", "Mismatched operation.") };

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("conversation_publication_uncertain", result.Run!.FailureCode);
        var request = Assert.Single(publisher.Requests);
        var publication = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        Assert.Equal(request.OperationId, publication.ConversationPublicationId);
        Assert.False(publication.PublishedToInvokingConversation);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Theory]
    [InlineData(CustomLoopConversationPublicationOutcome.Published)]
    [InlineData(CustomLoopConversationPublicationOutcome.AlreadyPublished)]
    public async Task Missing_publication_operation_id_is_not_accepted_as_success(CustomLoopConversationPublicationOutcome outcome)
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var publisher = new RecordingPublisher { NextResult = new CustomLoopConversationPublicationResult(outcome, null, "Missing operation ID.") };

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("conversation_publication_uncertain", result.Run!.FailureCode);
        var request = Assert.Single(publisher.Requests);
        var publication = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        Assert.Equal(request.OperationId, publication.ConversationPublicationId);
        Assert.False(publication.PublishedToInvokingConversation);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Theory]
    [InlineData("different-provider", "model", 0)]
    [InlineData("provider", "different-model", 0)]
    [InlineData("provider", "model", 7)]
    public async Task Rejected_provider_results_are_audited_as_needs_review_and_never_as_succeeded(string provider, string model, int toolCalls)
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var audit = new RecordingAuditLog();
        var providerResult = new CustomLoopInferenceAttemptResult("untrusted outcome", provider, model, "response-invalid", toolCalls);

        var result = await Runner(store, new QueueExecutor(providerResult), audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("provider_result_mismatch", result.Run!.FailureCode);
        var outcomeAudit = Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome != AuditSchema.Outcomes.Started);
        Assert.Equal(AuditSchema.Outcomes.NeedsReview, outcomeAudit.Outcome);
        Assert.DoesNotContain(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Succeeded);
    }

    [Fact]
    public async Task Caller_cancellation_after_provider_return_cannot_cancel_outcome_or_checkpoint_integrity_writes()
    {
        using var cancellation = new CancellationTokenSource();
        var definition = Definition(exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("observed"));
        executor.AfterExecute = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.True(result.ProviderWasInvoked);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Fact]
    public async Task Invalid_persisted_state_is_rejected_before_dispatch()
    {
        var invalid = Run(Definition()) with { AdmissionRequestHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };
        var store = new FakeRunStore(invalid, validateSeed: false);
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(invalid.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Admission_without_the_durable_audit_marker_is_rejected_before_dispatch()
    {
        var marked = Run(Definition());
        var incomplete = marked with { LifecycleVersion = 1, Events = [marked.Events[0]] };
        var store = new FakeRunStore(incomplete);
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(incomplete.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Contains("admission", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(executor.Requests);
        Assert.Equal(CustomLoopRunStatus.Admitted, store.Current.Status);
    }

    [Fact]
    public async Task Duplicate_admission_evidence_is_rejected_before_dispatch()
    {
        var admitted = Run(Definition());
        var malformed = admitted with
        {
            LifecycleVersion = 3,
            Events =
            [
                .. admitted.Events,
                new CustomLoopRunEvent(3, "event-admitted-duplicate", admitted.UpdatedAtUtc, CustomLoopRunEventKind.Admitted, null, null, null, "Duplicate admission.", [], null, null, null, null, null, null, null, null, null, null)
            ]
        };
        var store = new FakeRunStore(malformed, validateSeed: false);
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(malformed.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Equal(malformed, store.Current);
    }

    [Fact]
    public async Task Public_execution_rejects_a_Running_run_even_at_a_safe_boundary()
    {
        var admitted = Run(Definition());
        var lifecycle = new CustomLoopRunEvent(3, "event-running", _now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null);
        var running = admitted with
        {
            LifecycleVersion = 3,
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = new CustomLoopExecutionClock(0, _now),
            Events = [.. admitted.Events, lifecycle]
        };
        var store = new FakeRunStore(running);
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(running.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Contains("explicit recovery", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Resume_and_load_boundaries_reject_missing_mismatched_and_failed_reads_without_dispatch()
    {
        var admitted = Run(Definition());
        var lifecycle = new CustomLoopRunEvent(3, "event-running", _now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null);
        var running = admitted with
        {
            LifecycleVersion = 3,
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = new CustomLoopExecutionClock(0, _now),
            Events = [.. admitted.Events, lifecycle]
        };
        var executor = new QueueExecutor(Result("must not run"));
        var missingStore = new FakeRunStore(running) { ReturnMissing = true };
        var missingRunner = Runner(missingStore, executor);
        var missing = await missingRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, running.LifecycleVersion, lifecycle.EventId, AuditSchema.Actors.Web));

        var mismatchRunner = Runner(new FakeRunStore(running), executor);
        var mismatch = await mismatchRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, running.LifecycleVersion, "different-operation", AuditSchema.Actors.Web));

        var invalidRunning = running with { AdmissionRequestHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };
        var invalid = await Runner(new FakeRunStore(invalidRunning, validateSeed: false), executor).ResumeAsync(new CustomLoopResumeExecutionRequest(invalidRunning.Id, invalidRunning.LifecycleVersion, lifecycle.EventId, AuditSchema.Actors.Web));

        var failedStore = new FakeRunStore(running) { GetException = new IOException("Unavailable.") };
        var failedRunner = Runner(failedStore, executor);
        var failed = await failedRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, running.LifecycleVersion, lifecycle.EventId, AuditSchema.Actors.Web));
        var failedPublicRun = await failedRunner.RunAsync(new CustomLoopOrderedRunRequest(running.Id, AuditSchema.Actors.Web));
        failedRunner.CancelActiveAttempt("INVALID");
        var remoteCancellation = Assert.Throws<InvalidOperationException>(() => failedRunner.CancelActiveAttempt(running.Id));

        Assert.Equal(CustomLoopOrderedRunStatus.NotFound, missing.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, mismatch.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, invalid.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, failed.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, failedPublicRun.Status);
        Assert.Contains("not owned by this runtime", remoteCancellation.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mismatchRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, 0, lifecycle.EventId, AuditSchema.Actors.Web)));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_before_dispatch_cancels_the_admitted_run_without_provider_work()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.False(result.ProviderWasInvoked);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Routed_provider_cancellation_is_confirmed_only_after_the_attempt_observes_it()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new BlockingCancellationExecutor();
        var broker = new RecordingAttemptCancellationBroker();
        var runner = new CustomLoopOrderedRunner(
            store,
            new CustomLoopContextResolver(),
            executor,
            new RecordingPublisher(),
            new RecordingAuditLog(),
            new TestAuthorityProvider(),
            new FixedTimeProvider(_now),
            broker,
            new TestCapabilityAdmissionService());
        var execution = runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var signal = await runner.RequestActiveAttemptCancellationAsync(store.Current.Id, "cancel-routed-attempt");
        var result = await execution;

        Assert.Equal(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, signal.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("inference_attempt_uncertain", result.Run!.FailureCode);
    }

    [Fact]
    public async Task Caller_cancellation_that_wins_the_race_is_not_reported_as_routed_provider_interruption()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new RacingCancellationExecutor();
        var broker = new RecordingAttemptCancellationBroker();
        var runner = new CustomLoopOrderedRunner(
            store,
            new CustomLoopContextResolver(),
            executor,
            new RecordingPublisher(),
            new RecordingAuditLog(),
            new TestAuthorityProvider(),
            new FixedTimeProvider(_now),
            broker,
            new TestCapabilityAdmissionService());
        using var callerCancellation = new CancellationTokenSource();
        var execution = runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), callerCancellation.Token);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();

        var signal = runner.RequestActiveAttemptCancellationAsync(store.Current.Id, "cancel-after-caller");
        executor.Release.TrySetResult();
        var signalResult = await signal;
        var result = await execution;

        Assert.Equal(CustomLoopAttemptCancellationStatus.SignalDelivered, signalResult.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
    }

    [Fact]
    public async Task Public_execution_registers_local_ownership_before_persisting_running_state()
    {
        CustomLoopOrderedRunner? runner = null;
        var store = new FakeRunStore(Run(Definition()))
        {
            BeforeUpdate = (candidate, _) =>
            {
                if (candidate.Status == CustomLoopRunStatus.Running)
                {
                    runner!.CancelActiveAttempt(candidate.Id);
                }
            }
        };
        runner = Runner(store, new QueueExecutor(Result("completed")));

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Unsupported_discovery_index_schema_propagates_before_provider_dispatch()
    {
        var store = new FakeRunStore(Run(Definition()))
        {
            BeforeUpdate = (candidate, _) =>
            {
                if (candidate.Status == CustomLoopRunStatus.Running)
                {
                    throw new UnsupportedCustomLoopRunDiscoveryIndexSchemaException(2);
                }
            }
        };
        var executor = new QueueExecutor(Result("must not run"));

        var exception = await Assert.ThrowsAsync<UnsupportedCustomLoopRunDiscoveryIndexSchemaException>(() => Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web)));

        Assert.Contains("Delete `.custom-loop-run-index.json`", exception.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Unsupported_discovery_index_schema_after_provider_dispatch_escalates_to_needs_review()
    {
        var schemaFailureInjected = false;
        var store = new FakeRunStore(Run(Definition()))
        {
            BeforeUpdate = (candidate, _) =>
            {
                if (!schemaFailureInjected && candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved))
                {
                    schemaFailureInjected = true;
                    throw new UnsupportedCustomLoopRunDiscoveryIndexSchemaException(2);
                }
            }
        };
        var executor = new QueueExecutor(Result("provider outcome"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run?.Status);
        Assert.Equal("post_outcome_persistence_conflict", result.Run?.FailureCode);
        Assert.Contains("Delete `.custom-loop-run-index.json`", result.Detail, StringComparison.Ordinal);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Persistent_unsupported_schema_after_provider_dispatch_propagates_for_cleanup_and_recovery()
    {
        var store = new FakeRunStore(Run(Definition()))
        {
            BeforeUpdate = (candidate, _) =>
            {
                if (candidate.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved)
                    || candidate.Status == CustomLoopRunStatus.NeedsReview)
                {
                    throw new UnsupportedCustomLoopRunDiscoveryIndexSchemaException(2);
                }
            }
        };
        var executor = new QueueExecutor(Result("provider outcome"));

        var exception = await Assert.ThrowsAsync<UnsupportedCustomLoopRunDiscoveryIndexSchemaException>(() => Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web)));

        Assert.Contains("Delete `.custom-loop-run-index.json`", exception.Message, StringComparison.Ordinal);
        Assert.Contains("external outcome may exist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NeedsReview escalation could not be persisted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(CustomLoopRunStatus.Running, store.Current.Status);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_admitted_run_load_parks_the_run_before_returning()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition()))
        {
            BeforeGet = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.False(result.ProviderWasInvoked);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_run_start_audit_cancels_instead_of_reporting_an_audit_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog
        {
            BeforeAppend = (auditEvent, token) =>
            {
                if (auditEvent.Action == AuditSchema.Actions.LoopRunLifecycle && auditEvent.Outcome == AuditSchema.Outcomes.Started)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }
        };

        var result = await Runner(store, executor, audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.False(result.ProviderWasInvoked);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.DoesNotContain("run_start_audit_failed", result.Detail, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_attempt_start_audit_cancels_without_provider_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog
        {
            BeforeAppend = (auditEvent, token) =>
            {
                if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }
        };

        var result = await Runner(store, executor, audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.False(result.ProviderWasInvoked);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.DoesNotContain("attempt_start_audit_failed", result.Detail, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_Exit_start_audit_cancels_without_Exit_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var definition = Definition(maxAdditionalIterations: 1, exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration outcome"), Result("must not run"));
        var audit = new RecordingAuditLog
        {
            BeforeAppend = (auditEvent, token) =>
            {
                if (auditEvent.Action == AuditSchema.Actions.LoopExitDecision && auditEvent.Outcome == AuditSchema.Outcomes.Started)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }
        };

        var result = await Runner(store, executor, audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.True(result.ProviderWasInvoked);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.DoesNotContain("exit_start_audit_failed", result.Detail, StringComparison.Ordinal);
        Assert.Single(executor.Requests);
        Assert.False(executor.Requests[0].IsExit);
    }

    [Fact]
    public async Task Caller_cancellation_during_attempt_start_persistence_reloads_and_durably_cancels_the_running_run()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition()))
        {
            BeforeUpdate = (candidate, token) =>
            {
                if (candidate.Events[^1].Kind == CustomLoopRunEventKind.NodeAttemptStarted)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }
        };
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
        Assert.Null(result.Run.FailureCode);
    }

    [Fact]
    public async Task Caller_cancellation_during_attempt_start_persistence_requires_review_when_the_durable_trace_cannot_be_reloaded()
    {
        using var cancellation = new CancellationTokenSource();
        FakeRunStore? store = null;
        store = new FakeRunStore(Run(Definition()))
        {
            BeforeUpdate = (candidate, token) =>
            {
                if (candidate.Events[^1].Kind == CustomLoopRunEventKind.NodeAttemptStarted)
                {
                    store!.GetException = new IOException("Reload unavailable.");
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("must not run"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("could not be loaded", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_authority_snapshot_is_rejected_before_trace_or_dispatch()
    {
        var definition = Definition(tools: [CustomLoopToolAssignment.Read]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("must not run"));
        var authority = new FixedAuthorityProvider(Authority("role-workspace", [CustomLoopToolAssignment.Read], [CustomLoopToolAssignment.Read]) with { IsValid = false, Detail = "Authority unavailable." });

        var result = await Runner(store, executor, authorityProvider: authority).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("invalid_inference_request", result.Run!.FailureCode);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Authority_snapshot_for_another_role_is_rejected_before_trace_or_dispatch()
    {
        var definition = Definition(tools: [CustomLoopToolAssignment.Read]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("must not run"));
        var authority = new FixedAuthorityProvider(Authority("role-other", [CustomLoopToolAssignment.Read], [CustomLoopToolAssignment.Read]));

        var result = await Runner(store, executor, authorityProvider: authority).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("invalid_inference_request", result.Run!.FailureCode);
        Assert.Empty(executor.Requests);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
    }

    [Fact]
    public async Task Authority_snapshot_wider_than_the_admitted_tool_maximum_is_rejected_before_dispatch()
    {
        var definition = Definition(tools: [CustomLoopToolAssignment.Read]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("must not run"));
        var authority = new FixedAuthorityProvider(Authority("role-workspace", [CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search], [CustomLoopToolAssignment.Search]));

        var result = await Runner(store, executor, authorityProvider: authority).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("invalid_inference_request", result.Run!.FailureCode);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Completed_durable_tool_requests_are_counted_in_the_checkpoint()
    {
        var store = new FakeRunStore(Run(Definition(tools: [CustomLoopToolAssignment.Read])));
        var executor = new QueueExecutor(Result("completed", toolCalls: 2))
        {
            AfterExecute = request => AppendToolTraceAsync(store, request, requestCount: 2, includeOutcomes: true)
        };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(2, result.Run!.Checkpoint.ToolRequestsUsed);
    }

    [Fact]
    public async Task Exhausted_recorded_run_budget_makes_later_inference_attempts_tool_less()
    {
        var definition = Definition(tools: [CustomLoopToolAssignment.Read]);
        var run = Run(definition) with { Checkpoint = CustomLoopRunCheckpoint.Start() with { ToolRequestsUsed = CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun } };
        var store = new FakeRunStore(run);
        var executor = new QueueExecutor(Result("completed without tools"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        var request = Assert.Single(executor.Requests);
        Assert.False(request.AllowTools);
        Assert.Contains(request.InferenceRequest.Messages, message => message.Content.Contains("Tools: none", StringComparison.Ordinal));
        Assert.Equal(CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun, result.Run!.Checkpoint.ToolRequestsUsed);
    }

    [Fact]
    public async Task Underreported_tool_usage_is_rejected_against_the_durable_completed_trace()
    {
        var store = new FakeRunStore(Run(Definition(tools: [CustomLoopToolAssignment.Read])));
        var executor = new QueueExecutor(Result("untrusted", toolCalls: 1))
        {
            AfterExecute = request => AppendToolTraceAsync(store, request, requestCount: 2, includeOutcomes: true)
        };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("durable completed trace records 2", result.Run!.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(0, result.Run.Checkpoint.ToolRequestsUsed);
    }

    [Fact]
    public async Task Incomplete_durable_tool_phases_are_not_counted_as_completed_usage()
    {
        var store = new FakeRunStore(Run(Definition(tools: [CustomLoopToolAssignment.Read])));
        var executor = new QueueExecutor(Result("untrusted", toolCalls: 1))
        {
            AfterExecute = request => AppendToolTraceAsync(store, request, requestCount: 1, includeOutcomes: false)
        };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("reservation, governance decision, observed outcome, and exact returned-to-model marker", result.Run!.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_unreserved_repeated_request_integrity_is_recognized_but_never_counted_as_completed_usage()
    {
        var store = new FakeRunStore(Run(Definition(tools: [CustomLoopToolAssignment.Read])));
        var executor = new QueueExecutor(Result("untrusted", toolCalls: 1))
        {
            AfterExecute = async request =>
            {
                await AppendToolTraceAsync(store, request, requestCount: 1, includeOutcomes: true);
                await AppendStandaloneIntegrityAsync(store, request, requestOrdinal: 2, correlationId: $"tool-{request.Iteration}-{request.StepId}-1");
            }
        };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("exact non-actuating integrity failure", result.Run!.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(0, result.Run.Checkpoint.ToolRequestsUsed);
    }

    [Fact]
    public async Task Deadline_expiring_during_pre_dispatch_audit_prevents_the_provider_request()
    {
        var time = new MutableTimeProvider(_now);
        var audit = new RecordingAuditLog
        {
            BeforeAppend = (auditEvent, _) =>
            {
                if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
                {
                    time.Advance(TimeSpan.FromMilliseconds(CustomLoopLimits.MaxRunExecutionMilliseconds));
                }
            }
        };
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor, audit: audit, timeProvider: time).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("run_deadline_exceeded", result.Run!.FailureCode);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_inference_assembly_cancels_without_provider_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var authority = new CancellingAuthorityProvider(cancellation, cancelOnCall: 1);

        var result = await Runner(store, executor, authorityProvider: authority).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_at_the_final_dispatch_boundary_cancels_without_marking_the_attempt_uncertain()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var time = new FinalDispatchBoundaryCancellingTimeProvider(_now, store, cancellation);

        var result = await Runner(store, executor, timeProvider: time).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.Empty(executor.Requests);
    }

    [Theory]
    [InlineData(true, "run_deadline_exceeded")]
    [InlineData(false, "provider_cancelled_before_dispatch")]
    public async Task Provider_deadline_expiry_before_invocation_returns_a_structured_pre_dispatch_failure(bool reportDeadlineReached, string expectedFailureCode)
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var time = new FinalDispatchDeadlineTimeProvider(_now, store, reportDeadlineReached);

        var result = await Runner(store, executor, timeProvider: time).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, result.Run!.Status);
        Assert.Equal(expectedFailureCode, result.Run.FailureCode);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Durable_cancel_at_the_final_dispatch_boundary_wins_without_provider_invocation()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var time = new FinalDispatchActionTimeProvider(_now, store);
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit, timeProvider: time);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? cancel = null;
        time.AtFinalBoundary = () => cancel = lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-at-final-boundary", AuditSchema.Actors.Web)).GetAwaiter().GetResult();

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_Exit_assembly_cancels_without_Exit_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var definition = Definition(maxAdditionalIterations: 1, exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration outcome"), Result("must not run"));
        var authority = new CancellingAuthorityProvider(cancellation, cancelOnCall: 2);

        var result = await Runner(store, executor, authorityProvider: authority).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.Single(executor.Requests);
        Assert.False(executor.Requests[0].IsExit);
    }

    [Fact]
    public async Task Pause_after_attempt_start_audit_can_resume_without_consuming_an_undispatched_attempt()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var executor = new QueueExecutor(Result("resumed outcome"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? pause = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
            {
                pause = await lifecycle.PauseAsync(new CustomLoopPauseRequest(store.Current.Id, store.Current.LifecycleVersion, "pause-before-dispatch", AuditSchema.Actors.Web));
            }
        };

        var paused = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.PauseRequested, pause!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, paused.Status);
        Assert.Empty(executor.Requests);

        audit.AfterAppend = null;
        var resumed = await lifecycle.ResumeAsync(new CustomLoopResumeRequest(store.Current.Id, store.Current.LifecycleVersion, "resume-undispatched-attempt", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.Completed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, resumed.Run!.Status);
        Assert.Single(executor.Requests);
        Assert.Equal(2, resumed.Run.Events.Count(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted));
        Assert.Single(resumed.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
    }

    [Fact]
    public async Task Durable_cancel_between_attempt_audit_and_registration_prevents_provider_dispatch()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? cancel = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
            {
                cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-before-registration", AuditSchema.Actors.Web));
            }
        };

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Durable_cancel_after_outcome_audit_prevents_conversation_publication()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var executor = new QueueExecutor(Result("observed outcome"));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, publisher, audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? cancel = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Succeeded)
            {
                cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-before-publication", AuditSchema.Actors.Web));
            }
        };

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(publisher.Requests);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted);
    }

    [Fact]
    public async Task Durable_pause_after_committed_Exit_completion_resumes_without_redispatching_Exit()
    {
        var definition = Definition(maxAdditionalIterations: 1, exitPolicy: Policy(Output(false, true)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var executor = new QueueExecutor(Result("iteration outcome"), Result("Complete"));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, publisher, audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? pause = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopExitDecision && auditEvent.Outcome == AuditSchema.Outcomes.Succeeded)
            {
                pause = await lifecycle.PauseAsync(new CustomLoopPauseRequest(store.Current.Id, store.Current.LifecycleVersion, "pause-before-exit-publication", AuditSchema.Actors.Web));
            }
        };

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.PauseRequested, pause!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, result.Run!.Status);
        Assert.Single(publisher.Requests);

        audit.AfterAppend = null;
        var resumed = await lifecycle.ResumeAsync(new CustomLoopResumeRequest(store.Current.Id, store.Current.LifecycleVersion, "resume-committed-exit", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.Completed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, resumed.Run!.Status);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Single(publisher.Requests);
    }

    [Fact]
    public async Task Durable_cancel_after_publication_intent_prevents_the_external_append()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            exitPolicy: Policy(Output(false, false)));
        CustomLoopLifecycleService? lifecycle = null;
        CustomLoopControlResult? cancel = null;
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)))
        {
            AfterUpdate = async updated =>
            {
                if (cancel is null && updated.Events[^1].Kind == CustomLoopRunEventKind.ConversationPublicationStarted)
                {
                    cancel = await lifecycle!.CancelAsync(new CustomLoopCancelRequest(updated.Id, updated.LifecycleVersion, "cancel-after-publication-intent", AuditSchema.Actors.Web));
                }
            }
        };
        var executor = new QueueExecutor(Result("observed outcome"));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, publisher, audit);
        lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(publisher.Requests);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted);
    }

    [Fact]
    public async Task Durable_cancel_after_deterministic_Exit_audit_prevents_publication_and_cancels_at_the_checkpoint()
    {
        var definition = Definition(exitPolicy: Policy(Output(false, true)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", _now)));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        var runner = Runner(store, new QueueExecutor(Result("iteration outcome")), publisher, audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? cancel = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopExitDecision && auditEvent.Outcome == AuditSchema.Outcomes.Succeeded)
            {
                cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-before-deterministic-exit-publication", AuditSchema.Actors.Web));
            }
        };

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_persists_a_needs_review_terminal_trace()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true
        };
        var executor = new QueueExecutor(Result("provider outcome may exist"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("post_outcome_persistence_conflict", result.Run.FailureCode);
        Assert.Contains("external outcome may exist", result.Run.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
        Assert.Single(executor.Requests);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Internal_outcome_trace_cancellation_is_converted_to_a_durable_needs_review_result()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            BeforeUpdate = (candidate, _) =>
            {
                if (candidate.Events[^1].Kind == CustomLoopRunEventKind.NodeAttemptCompleted)
                {
                    throw new OperationCanceledException("Integrity write timed out.");
                }
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("post_outcome_persistence_conflict", result.Run.FailureCode);
        Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
    }

    [Fact]
    public async Task Thrown_post_outcome_trace_failure_is_durably_quarantined_for_review()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            BeforeUpdate = (candidate, _) =>
            {
                if (candidate.Events[^1].Kind == CustomLoopRunEventKind.NodeAttemptCompleted)
                {
                    throw new IOException("Outcome store unavailable.");
                }
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("post_outcome_persistence_conflict", result.Run.FailureCode);
        Assert.Contains(nameof(IOException), result.Run.FailureDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_preserves_a_concurrent_needs_review_trace()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true,
            ConcurrentNeedsReviewOnOutcomeConflict = true
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("concurrent_review", result.Run.FailureCode);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_reports_when_the_latest_trace_disappears()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true,
            ReturnMissingAfterOutcomeConflict = true
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("latest run trace could not be found", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_reports_uncertain_escalation_persistence()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true,
            GetExceptionAfterOutcomeConflict = new IOException("Unavailable.")
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("escalation persistence is uncertain", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminal_trace_is_durable_before_audit_and_audit_failure_preserves_Completed_output_with_a_visible_integrity_warning()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var audit = new RecordingAuditLog
        {
            FailPredicate = item =>
            {
                if (item.Action != AuditSchema.Actions.LoopRunLifecycle || !item.Metadata.ContainsKey("terminalStatus"))
                {
                    return false;
                }

                Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
                Assert.Equal("final", store.Current.FinalOutput);
                Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, store.Current.Events[^1].Kind);
                return true;
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("final")), audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.Equal("final", result.Run.FinalOutput);
        Assert.Equal(CustomLoopRunEventKind.IntegrityWarning, result.Run.Events[^1].Kind);
        Assert.Contains("terminal audit append failed", result.Run.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminal_warning_persistence_uncertainty_is_visible_without_rewriting_the_truthful_terminal_outcome()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            AppendTerminalWarningException = new IOException("warning store unavailable")
        };
        var audit = new RecordingAuditLog
        {
            FailPredicate = item => item.Action == AuditSchema.Actions.LoopRunLifecycle && item.Metadata.ContainsKey("terminalStatus")
        };

        var result = await Runner(store, new QueueExecutor(Result("final")), audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Equal("final", result.Run.FinalOutput);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.IntegrityWarning);
        Assert.Contains("persistence outcome is uncertain", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_after_deterministic_Exit_outcome_cannot_cancel_its_post_outcome_audit()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            AfterUpdate = run =>
            {
                if (run.Events[^1].Kind == CustomLoopRunEventKind.ExitDecisionCompleted)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("final"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal("final", result.Run!.FinalOutput);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
    }

    [Fact]
    public async Task Caller_cancellation_during_deterministic_Exit_persistence_cancels_before_the_outcome_is_committed()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            BeforeUpdate = (candidate, token) =>
            {
                if (candidate.Events[^1].Kind == CustomLoopRunEventKind.ExitDecisionCompleted)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("final"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
    }

    [Fact]
    public async Task Pause_during_an_open_attempt_finishes_that_attempt_commits_a_checkpoint_and_dispatches_nothing_later()
    {
        var definition = Definition(steps:
        [
            Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
            Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
        ]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("first outcome"), Result("must not dispatch"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? pause = null;
        executor.BeforeExecute = async _ => pause = await lifecycle.PauseAsync(new CustomLoopPauseRequest(store.Current.Id, store.Current.LifecycleVersion, "pause-open-attempt", AuditSchema.Actors.Web));

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.PauseRequested, pause!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, result.Run!.Status);
        Assert.Single(executor.Requests);
        Assert.Null(result.Run.ExecutionClock.ActiveSinceUtc);
        var checkpoint = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
        var paused = result.Run.Events.Last(item => item.Kind == CustomLoopRunEventKind.LifecycleChanged);
        Assert.True(checkpoint.Sequence < paused.Sequence);
        Assert.Equal(checkpoint.Sequence, result.Run.Checkpoint.LastCommittedSequence);
    }

    [Fact]
    public async Task Cancel_during_an_open_attempt_cancels_transport_and_records_NeedsReview_without_later_dispatch()
    {
        var definition = Definition(steps:
        [
            Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
            Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
        ]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("must be cancelled"), Result("must not dispatch"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        CustomLoopControlResult? cancel = null;
        executor.BeforeExecute = async _ => cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-open-attempt", AuditSchema.Actors.Web));

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("inference_attempt_uncertain", result.Run.FailureCode);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Explicit_resume_uses_the_paused_checkpoint_while_same_operation_replays_and_changed_content_conflicts()
    {
        var definition = Definition(steps:
        [
            Step("step-first", "First", "First instruction", Output(retain: true, publish: true)),
            Step("step-second", "Second", "Second instruction", Output(retain: false, publish: true))
        ], exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "immutable-admission-version", _now)));
        var executor = new QueueExecutor(Result("first outcome"), Result("second outcome"));
        var audit = new RecordingAuditLog();
        var firstPublisher = new RecordingPublisher();
        var runner = Runner(store, executor, firstPublisher, audit);
        var operations = new FakeControlOperationStore();
        var lifecycle = new CustomLoopLifecycleService(store, operations, runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        var pauseRequest = new CustomLoopPauseRequest(store.Current.Id, 4, "pause-for-resume", AuditSchema.Actors.Web);
        executor.BeforeExecute = async _ =>
        {
            if (executor.Requests.Count == 1)
            {
                Assert.Equal(pauseRequest.ExpectedLifecycleVersion, store.Current.LifecycleVersion);
                await lifecycle.PauseAsync(pauseRequest);
            }
        };

        var paused = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));
        var replay = await lifecycle.PauseAsync(pauseRequest);
        var conflict = await lifecycle.PauseAsync(pauseRequest with { Actor = AuditSchema.Actors.Cli });
        executor.BeforeExecute = null;
        var resumedPublisher = new RecordingPublisher();
        var resumedRunner = Runner(store, executor, resumedPublisher, audit);
        var resumedLifecycle = new CustomLoopLifecycleService(store, operations, resumedRunner, new AvailableModel(), resumedRunner, audit, new TestExecutionGate(), new FixedTimeProvider(_now));
        var resumed = await resumedLifecycle.ResumeAsync(new CustomLoopResumeRequest(store.Current.Id, store.Current.LifecycleVersion, "resume-paused-run", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Paused, paused.Status);
        Assert.Equal(CustomLoopControlStatus.PauseRequested, replay.Status);
        Assert.Equal(CustomLoopControlStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopControlStatus.Completed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, resumed.Run!.Status);
        Assert.Equal(["step-first", "step-second"], executor.Requests.Select(item => item.StepId));
        Assert.Contains(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("first outcome", StringComparison.Ordinal));
        Assert.Single(firstPublisher.Requests);
        var resumedPublication = Assert.Single(resumedPublisher.Requests);
        var priorPublication = Assert.Single(resumedPublication.PriorPublications!);
        Assert.Equal("first outcome", priorPublication.CanonicalOutput);
    }

    [Fact]
    public async Task Missing_and_non_runnable_runs_fail_without_dispatch()
    {
        var seed = Run(Definition());
        var missingStore = new FakeRunStore(seed) { ReturnMissing = true };
        var executor = new QueueExecutor(Result("must not run"));
        var missing = await Runner(missingStore, executor).RunAsync(new CustomLoopOrderedRunRequest(seed.Id, AuditSchema.Actors.Web));

        var completedStore = new FakeRunStore(seed with
        {
            Status = CustomLoopRunStatus.Completed,
            CompletedAtUtc = _now,
            FinalOutput = "done",
            ExecutionClock = CustomLoopExecutionClock.NotStarted()
        }, validateSeed: false);
        var invalidState = await Runner(completedStore, executor).RunAsync(new CustomLoopOrderedRunRequest(seed.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NotFound, missing.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, invalidState.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Trace_capacity_is_proved_before_each_dispatch_and_stops_before_mandatory_evidence_would_exceed_the_run_bound()
    {
        var steps = Enumerable.Range(1, CustomLoopLimits.MaxInferenceSteps)
            .Select(index => Step($"step-{index}", $"Step {index}", "Do the work", Output(retain: false, publish: false)))
            .ToArray();
        var definition = Definition(steps, CustomLoopLimits.MaxAdditionalIterations, Policy(Output(retain: false, publish: false)));
        var sourceContent = new string('漢', CustomLoopLimits.MaxInstructionCharacters);
        var worstCaseOutput = new string('\uffff', CustomLoopLimits.MaxCanonicalModelOutputCharacters);
        var seed = Run(definition);
        var context = CustomLoopContextSnapshotHash.Apply(seed.ContextSnapshot with
        {
            SourceManifest = seed.ContextSnapshot.SourceManifest
                .Select(source => source with
                {
                    Content = sourceContent,
                    ContentHash = CustomLoopTraceContentHash.Compute(sourceContent),
                    OriginalCharacterCount = sourceContent.Length,
                    UsedCharacterCount = sourceContent.Length,
                    Truncated = false,
                    TruncationReason = null,
                    OmissionReason = null
                })
                .ToArray()
        });
        var run = CustomLoopAdmissionRequestHash.Apply(seed with { ContextSnapshot = context });
        var outcomes = new List<object>();
        for (var iteration = 0; iteration <= CustomLoopLimits.MaxAdditionalIterations; iteration++)
        {
            outcomes.AddRange(Enumerable.Range(0, CustomLoopLimits.MaxInferenceSteps).Select(_ => (object)Result(worstCaseOutput)));
            if (iteration < CustomLoopLimits.MaxAdditionalIterations)
            {
                outcomes.Add(Result("Repeat"));
            }
        }

        var store = new FakeRunStore(run) { ApplyRawTraceCapacityLimit = true };
        var executor = new QueueExecutor(outcomes.ToArray());

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal("run_trace_capacity_exhausted", result.Run!.FailureCode);
        Assert.True(executor.Requests.Count < CustomLoopLimits.MaxModelAttemptsPerRun);
        Assert.DoesNotContain(store.ValidationFailures, error => error.Code == "too_many_trace_events");
    }

    private static CustomLoopOrderedRunner Runner(FakeRunStore store, QueueExecutor executor, RecordingPublisher? publisher = null, RecordingAuditLog? audit = null, ICustomLoopToolAuthorityProvider? authorityProvider = null, TimeProvider? timeProvider = null, ICapabilityAdmissionService? capabilityAdmissionService = null)
    {
        return new CustomLoopOrderedRunner(store, new CustomLoopContextResolver(), executor, publisher ?? new RecordingPublisher(), audit ?? new RecordingAuditLog(), authorityProvider ?? new TestAuthorityProvider(), timeProvider ?? new FixedTimeProvider(_now), capabilityAdmissionService: capabilityAdmissionService ?? new TestCapabilityAdmissionService());
    }

    private static CustomLoopDefinition SequentialDefinition(
        int inferenceCount = 1,
        bool includeConversation = false,
        bool allowWorkspaceTools = false)
    {
        var seed = CustomLoopDefinition.CreateSeed("sequential-loop", "bounded-helper", "infer-01", "create-sequential-loop", _now);
        var definition = seed with
        {
            TriggerPolicy = new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, string.Empty, includeConversation),
            InferenceSteps = Enumerable.Range(1, inferenceCount)
                .Select(index => new CustomLoopInferenceStep(
                    $"infer-{index:D2}",
                    $"Inference {index}",
                    $"Execute bounded inference step {index}.",
                    CustomLoopNodeContextPolicy.Inherit()))
                .ToArray(),
            ToolAssignments = allowWorkspaceTools
                ? [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search]
                : [],
            ExitPolicy = new CustomLoopExitPolicy(0, CustomLoopDefinition.DefaultExitDecisionInstruction, CustomLoopNodeContextPolicy.Inherit())
        };
        return CustomLoopDefinitionContentHash.Apply(definition with
        {
            ContentHash = string.Empty,
            CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest(definition.Id, definition.ToolAssignments)
        });
    }

    private static async Task<SequentialTestContext> SequentialContextAsync(CustomLoopRunRecord run)
    {
        var seedHarness = GovernedLoopAdmissionTestHarness.Create();
        var seedOutcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seedHarness.CreateService().AdmitAsync(seedHarness.Request)).Outcome);
        var seedReceipt = Assert.IsType<GovernedLoopAdmissionReceipt>(seedOutcome.Receipt);
        var inferenceIds = run.AdmittedDefinition.InferenceSteps.Select(step => step.Id).ToArray();
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(
            inferenceIds.Length,
            inferenceIds,
            owningRole: seedReceipt.Intent.Role,
            allowWorkspaceTools: run.AdmittedDefinition.ToolAssignments.Length > 0);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            artifact.RevisionArtifact.Revision,
            "publish-sequential",
            Hash("publication-lifecycle"));
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            run.TriggerPrompt,
            run.ModelSnapshot,
            run.InvokingConversation,
            run.ContextSnapshot.CapturedAtUtc,
            run.ContextSnapshot.SourceManifest,
            string.Empty));
        var request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            1,
            run.AdmissionOperationId,
            invocation.ContentHash,
            string.Empty,
            publication,
            seedReceipt.Intent.AuthorityGrant,
            seedReceipt.Intent.ActorId,
            run.Surface));
        var intent = new GovernedLoopAdmissionIntent(
            1,
            seedReceipt.Intent.WorkspaceId,
            request.OperationId,
            request.RequestHash,
            publication,
            request.AuthorityGrant,
            artifact.Graph.OwningRole,
            request.ActorId,
            request.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var execution = GovernedLoopExecutionBinding.Create(1, run.Id, publication.Revision, 1);
        var capabilityAdmission = SequentialCapabilityAdmission(artifact) with
        {
            WorkspaceScopeId = intent.WorkspaceId,
        };
        var admissionEvidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            1,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            seedReceipt.Evidence.EffectiveAuthority,
            capabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, seedReceipt.Evidence.EffectiveAuthority, capabilityAdmission),
            GovernedLoopSequentialApplicationTestFixture.Now,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            1,
            intent,
            admissionEvidence,
            GovernedLoopSequentialApplicationTestFixture.Now,
            string.Empty));
        var binding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            1,
            intent.WorkspaceId,
            execution,
            request.OperationId,
            receipt.ContentHash,
            request.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));
        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(binding, request, receipt, invocation, artifact);
        var anchor = Assert.IsType<GovernedLoopSequentialRunAnchor>(anchorResult.Anchor);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        var projectedDefinition = Assert.IsType<CustomLoopDefinition>(
            GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, plan, artifact).Definition);
        var admitted = WithSequentialEvidence(
            run.Events[0],
            binding,
            artifact.Graph.EntryNodeId,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var boundRun = CustomLoopAdmissionRequestHash.Apply(run with
        {
            AdmittedDefinition = projectedDefinition,
            CapabilityAdmission = capabilityAdmission,
            SequentialInvocationSnapshot = invocation,
            SequentialAdapterBinding = binding,
            Events = [admitted, .. run.Events.Skip(1)],
            AdmissionRequestHash = string.Empty,
        });
        return new SequentialTestContext(
            artifact,
            anchor,
            plan,
            new GovernedLoopSequentialRunEvidence(binding, invocation),
            boundRun);
    }

    private static CapabilityAdmissionSnapshot SequentialCapabilityAdmission(GovernedLoopGraphRevisionArtifact artifact)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var compatibleVersions, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _));
        var dependencies = artifact.Graph.AuthorityCeiling.CapabilityIds.Select(capabilityId =>
        {
            Assert.True(CapabilityId.TryParse(capabilityId, out var dependency, out _));
            return new CapabilityDependency(dependency!, compatibleVersions!);
        }).ToArray();
        var requirements = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            dependencies,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        return TestCapabilityAdmissionFactory.Create(requirements, _now);
    }

    private static CustomLoopRunEvent WithSequentialEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        string nodeId,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition)
    {
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            kind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            nodeId,
            1,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopDefinition Definition(
        CustomLoopInferenceStep[]? steps = null,
        int maxAdditionalIterations = 0,
        CustomLoopContextPolicy? exitPolicy = null,
        CustomLoopToolAssignment[]? tools = null)
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-ordered", "role-workspace", "step-only", "create-loop", _now);
        var definition = seed with
        {
            InferenceSteps = steps ?? [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            ToolAssignments = tools ?? [],
            ExitPolicy = new CustomLoopExitPolicy(maxAdditionalIterations, CustomLoopDefinition.DefaultExitDecisionInstruction, exitPolicy is null ? CustomLoopNodeContextPolicy.Inherit() : CustomLoopNodeContextPolicy.Override(exitPolicy))
        };
        return CustomLoopDefinitionContentHash.Apply(definition with
        {
            ContentHash = string.Empty,
            CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest(definition.Id, definition.ToolAssignments)
        });
    }

    private static CustomLoopInferenceStep Step(string id, string name, string instruction, CustomLoopContextOutputPolicy output)
    {
        return new CustomLoopInferenceStep(id, name, instruction, CustomLoopNodeContextPolicy.Override(Policy(output)));
    }

    private static CustomLoopContextPolicy Policy(CustomLoopContextOutputPolicy output)
    {
        return new CustomLoopContextPolicy(new CustomLoopContextInputPolicy(true, true, false, true, true), output);
    }

    private static CustomLoopContextOutputPolicy Output(bool retain, bool publish)
    {
        return new CustomLoopContextOutputPolicy(retain, publish);
    }

    private static CustomLoopRunRecord ResumeReady(CustomLoopRunRecord run, string operationId)
    {
        var resumed = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 1,
            operationId,
            run.UpdatedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "Recovery retained the immutable invocation and resumed canonical evidence reconciliation.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            FailureCode = null,
            FailureDetail = null,
            ExecutionClock = run.ExecutionClock.ActiveSinceUtc is null
                ? run.ExecutionClock with { ActiveSinceUtc = run.UpdatedAtUtc }
                : run.ExecutionClock,
            Events = [.. run.Events, resumed],
        };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static async Task<CustomLoopRunRecord> RecoverForExplicitResumeAsync(CustomLoopRunRecord run, string operationId)
    {
        var recoveryStore = new FakeRunStore(run);
        var recoveryAudit = new RecordingAuditLog();
        var recovery = new CustomLoopRecoveryService(recoveryStore, recoveryAudit, new FixedTimeProvider(run.UpdatedAtUtc.AddSeconds(1)));

        var result = Assert.Single(await recovery.RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Paused, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, recoveryStore.Current.Status);
        Assert.Null(recoveryStore.Current.FailureCode);
        Assert.Equal(2, recoveryAudit.Events.Count);
        Assert.All(recoveryAudit.Events, item => Assert.Equal(false, item.Metadata["openAttemptAfterCheckpoint"]));
        return ResumeReady(recoveryStore.Current, operationId);
    }

    private static CustomLoopRunRecord Run(CustomLoopDefinition definition, CustomLoopConversationReference? conversation = null)
    {
        var admission = new CustomLoopRunEvent(1, "event-admitted", _now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var auditCompleted = new CustomLoopRunEvent(2, "event-admission-audit-complete", _now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null);
        var context = CustomLoopContextSnapshot.CreateEmpty(_now);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-ordered",
            definition.Id,
            2,
            CustomLoopRunStatus.Admitted,
            _now,
            _now,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            "invoke-operation",
            AuditSchema.Actors.Web,
            string.Empty,
            definition,
            "Initial user prompt",
            conversation,
            context,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admission, auditCompleted],
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _now)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static async Task AppendToolTraceAsync(
        FakeRunStore store,
        CustomLoopInferenceAttemptRequest request,
        int requestCount,
        bool includeOutcomes,
        ToolApprovalDecision approvalDecision = ToolApprovalDecision.NotRequired)
    {
        var authority = Assert.IsType<CustomLoopToolAuthoritySnapshot>(request.AuthoritySnapshot);
        var events = new List<CustomLoopRunEvent>();
        for (var ordinal = 1; ordinal <= requestCount; ordinal++)
        {
            var correlation = $"tool-{request.Iteration}-{request.StepId}-{ordinal}";
            var reservation = new CustomLoopToolTraceEvidence(
                CustomLoopToolEvidencePhase.RequestReserved,
                ordinal,
                correlation,
                null,
                ToolCommand.Read,
                "shared/file.txt",
                null,
                null,
                null,
                authority,
                null,
                null,
                null,
                null,
                null,
                false,
                CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
            var governance = new ToolGovernanceEvidence(
                ToolAuthorityDecision.Allowed,
                "Allowed by the test authority.",
                PermissionDecision.Allow,
                "shared/file.txt",
                "Allowed by test policy.",
                null,
                approvalDecision,
                approvalDecision == ToolApprovalDecision.Approved ? "user-approver" : null,
                approvalDecision == ToolApprovalDecision.Approved ? "Approved by test policy." : null);
            var governed = reservation with { Phase = CustomLoopToolEvidencePhase.GovernanceDecided, BrokerRequestId = $"broker-{ordinal}", Governance = governance };
            var canonicalResult = $"tool result {ordinal}";
            var outcome = governed with
            {
                Phase = CustomLoopToolEvidencePhase.OutcomeObserved,
                Outcome = ToolExecutionOutcome.Succeeded,
                CanonicalResultReturnedToModel = canonicalResult,
                CanonicalResultHash = CustomLoopTraceContentHash.Compute(canonicalResult),
                CanonicalResultCharacterCount = canonicalResult.Length,
                ReturnedToModel = false
            };
            var returned = outcome with { ReturnedToModel = true };
            events.Add(ToolEvent(store.Current.Events.Length + events.Count + 1, CustomLoopRunEventKind.ToolRequestReserved, request, reservation));
            events.Add(ToolEvent(store.Current.Events.Length + events.Count + 1, CustomLoopRunEventKind.ToolGovernanceDecided, request, governed));
            if (includeOutcomes)
            {
                events.Add(ToolEvent(store.Current.Events.Length + events.Count + 1, CustomLoopRunEventKind.ToolOutcomeObserved, request, outcome));
                events.Add(ToolEvent(store.Current.Events.Length + events.Count + 1, CustomLoopRunEventKind.ToolOutcomeObserved, request, returned));
            }
        }

        var candidate = store.Current with
        {
            LifecycleVersion = store.Current.LifecycleVersion + 1,
            Events = [.. store.Current.Events, .. events]
        };
        var stored = await store.UpdateAsync(candidate, store.Current.LifecycleVersion);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, stored.Status);
    }

    private static async Task AppendStandaloneIntegrityAsync(FakeRunStore store, CustomLoopInferenceAttemptRequest request, int requestOrdinal, string correlationId)
    {
        var authority = Assert.IsType<CustomLoopToolAuthoritySnapshot>(request.AuthoritySnapshot);
        var integrity = new CustomLoopToolTraceEvidence(
            CustomLoopToolEvidencePhase.IntegrityFailed,
            requestOrdinal,
            correlationId,
            null,
            ToolCommand.Read,
            "shared/repeated.txt",
            null,
            null,
            "workspace/shared/repeated.txt",
            authority,
            null,
            null,
            null,
            null,
            null,
            false,
            CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes);
        var traceEvent = ToolEvent(store.Current.Events.Length + 1, CustomLoopRunEventKind.ToolIntegrityFailed, request, integrity);
        var candidate = store.Current with
        {
            LifecycleVersion = store.Current.LifecycleVersion + 1,
            Events = [.. store.Current.Events, traceEvent]
        };
        var stored = await store.UpdateAsync(candidate, store.Current.LifecycleVersion);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, stored.Status);
    }

    private static CustomLoopRunEvent ToolEvent(long sequence, CustomLoopRunEventKind kind, CustomLoopInferenceAttemptRequest request, CustomLoopToolTraceEvidence evidence)
    {
        var returnMarker = evidence.ReturnedToModel ? "-returned" : string.Empty;
        return new CustomLoopRunEvent(sequence, $"event-{evidence.RequestCorrelationId}-{evidence.Phase.ToString().ToLowerInvariant()}{returnMarker}", _now, kind, request.Iteration, request.StepId, request.Attempt, $"Durable {evidence.Phase} test evidence.", [], null, null, null, null, null, null, null, null, null, null, evidence.Authority, evidence);
    }

    private static CustomLoopInferenceAttemptResult Result(string output, int toolCalls = 0)
    {
        return new CustomLoopInferenceAttemptResult(output, "provider", "model", $"response-{Guid.NewGuid():N}", toolCalls);
    }

    private static string Hash(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static CustomLoopToolAuthoritySnapshot Authority(string roleId, CustomLoopToolAssignment[] admitted, CustomLoopToolAssignment[] effective)
    {
        var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
        var roleHash = CustomLoopTraceContentHash.Compute(roleId + "\n" + string.Join('\n', admitted.OrderBy(value => value)));
        var catalogHash = CustomLoopTraceContentHash.Compute(string.Join('\n', catalog));
        return new CustomLoopToolAuthoritySnapshot(roleId, admitted, admitted, catalog, effective, roleHash, catalogHash, _now, true, "Test authority snapshot.");
    }

    private sealed record SequentialTestContext(
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopSequentialRunAnchor Anchor,
        GovernedLoopSequentialPlan Plan,
        GovernedLoopSequentialRunEvidence Evidence,
        CustomLoopRunRecord Run);

    private sealed class SequentialEvidenceHarness(
        FakeRunStore store,
        GovernedLoopSequentialRunEvidence retainedEvidence,
        SequentialAuditLedger? auditLedger = null) :
        IGovernedLoopSequentialRunEvidenceSource,
        IGovernedLoopSequentialOrderedNodeEvidenceRecorder,
        IGovernedLoopSequentialAuditRecorder
    {
        private readonly Dictionary<string, GovernedLoopSequentialNodeEvidenceReceipt> _receipts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string EventId, long Sequence)> _identityEvents = new(StringComparer.Ordinal);
        private readonly SequentialAuditLedger _auditLedger = auditLedger ?? new SequentialAuditLedger();

        public List<GovernedLoopSequentialOrderedNodeEvidenceRequest> Requests { get; } = [];

        public List<int> NextStepIndicesAtRetention { get; } = [];

        public List<(string OperationId, string EvidenceHash, AuditEvent AuditEvent)> AuditRequests { get; } = [];

        public GovernedLoopSequentialAuditRecordStatus? ForcedAuditStatus { get; set; }

        public Func<Task>? AfterAuditRecord { get; set; }

        public Task<GovernedLoopSequentialRunEvidence?> ResolveAsync(string runId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<GovernedLoopSequentialRunEvidence?>(string.Equals(
                retainedEvidence.AdapterBinding.ExecutionBinding.RunId,
                runId,
                StringComparison.Ordinal)
                ? retainedEvidence
                : null);
        }

        public Task<GovernedLoopSequentialNodeHandlerResult> RetainAsync(
            GovernedLoopSequentialOrderedNodeEvidenceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            NextStepIndicesAtRetention.Add(store.Current.Checkpoint.NextStepIndex);
            var dispatch = request.Dispatch;
            var binding = dispatch.Anchor.AdapterBinding;
            var run = store.Current;
            var orderedEvent = run.Events.SingleOrDefault(item => item.Sequence == request.OrderedEventSequence
                && string.Equals(item.EventId, request.OrderedEventId, StringComparison.Ordinal));
            var durable = orderedEvent?.SequentialNodeEvidence;
            var expectedDisposition = durable?.Disposition switch
            {
                CustomLoopSequentialNodeDisposition.Completed => GovernedLoopSequentialNodeHandlerResultStatus.Completed,
                CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopSequentialNodeHandlerResultStatus.Rejected,
                CustomLoopSequentialNodeDisposition.NeedsReview => GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview,
                _ => GovernedLoopSequentialNodeHandlerResultStatus.Unknown,
            };
            var eventMatchesNode = dispatch.Node.Descriptor.Kind switch
            {
                EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Trigger => orderedEvent?.Kind == CustomLoopRunEventKind.Admitted,
                EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Inference => orderedEvent?.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeAttemptFailed or CustomLoopRunEventKind.NodeOutcomeObserved,
                EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Exit => orderedEvent?.Kind is CustomLoopRunEventKind.ExitDecisionCompleted or CustomLoopRunEventKind.NodeAttemptFailed or CustomLoopRunEventKind.NodeOutcomeObserved,
                _ => false,
            };
            if (request.SchemaVersion != GovernedLoopSequentialOrderedNodeEvidenceRequest.CurrentSchemaVersion
                || request.OrderedLifecycleVersion != run.LifecycleVersion
                || !string.Equals(binding.ExecutionBinding.RunId, run.Id, StringComparison.Ordinal)
                || !eventMatchesNode
                || durable is null
                || request.Disposition != expectedDisposition
                || !string.Equals(durable.NodeId, dispatch.Node.NodeId, StringComparison.Ordinal)
                || durable.Attempt != dispatch.Attempt
                || !CustomLoopSequentialNodeEvidenceHash.Matches(durable)
                || !CustomLoopSequentialOutcomeArtifactHash.Matches(orderedEvent))
            {
                return Task.FromResult(new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, string.Empty));
            }

            var identity = $"{binding.ExecutionBinding.RunId}/{binding.ExecutionBinding.ExecutionGeneration}/{dispatch.Node.NodeId}/{dispatch.Attempt}";
            if (_identityEvents.TryGetValue(identity, out var prior)
                && (!string.Equals(prior.EventId, orderedEvent!.EventId, StringComparison.Ordinal) || prior.Sequence != orderedEvent.Sequence))
            {
                return Task.FromResult(new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, string.Empty));
            }

            _identityEvents[identity] = (orderedEvent!.EventId, orderedEvent.Sequence);
            var receipt = new GovernedLoopSequentialNodeEvidenceReceipt(
                1,
                durable.Kind switch
                {
                    CustomLoopSequentialNodeEvidenceKind.CompletedOutcome => GovernedLoopSequentialNodeEvidenceKind.CompletedOutcome,
                    CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection => GovernedLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                    CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention => GovernedLoopSequentialNodeEvidenceKind.AmbiguityAttention,
                    _ => GovernedLoopSequentialNodeEvidenceKind.Unknown,
                },
                durable.WorkspaceId,
                durable.RunId,
                durable.Revision,
                durable.ExecutionGeneration,
                durable.NodeId,
                durable.Attempt,
                request.Disposition,
                durable.OutcomeArtifactHash,
                durable.EvidenceHash);
            if (!GovernedLoopSequentialNodeEvidenceHash.Matches(receipt))
            {
                return Task.FromResult(new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, string.Empty));
            }

            _receipts[receipt.EvidenceHash] = receipt;
            return Task.FromResult(new GovernedLoopSequentialNodeHandlerResult(request.Disposition, receipt.EvidenceHash));
        }

        Task<GovernedLoopSequentialNodeEvidenceReceipt?> IGovernedLoopSequentialNodeEvidenceSource.ResolveAsync(
            string evidenceHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _receipts.TryGetValue(evidenceHash, out var receipt);
            return Task.FromResult(receipt);
        }

        public async Task<GovernedLoopSequentialAuditRecordResult> RecordOnceAsync(
            string operationId,
            string evidenceHash,
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuditRequests.Add((operationId, evidenceHash, auditEvent));
            if (ForcedAuditStatus is { } forced)
            {
                return new GovernedLoopSequentialAuditRecordResult(forced, "Forced test disposition.");
            }

            var serialized = JsonSerializer.Serialize(auditEvent);
            if (_auditLedger.Records.TryGetValue(operationId, out var existing))
            {
                var status = string.Equals(existing.EvidenceHash, evidenceHash, StringComparison.Ordinal)
                    && string.Equals(existing.SerializedAudit, serialized, StringComparison.Ordinal)
                        ? GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded
                        : GovernedLoopSequentialAuditRecordStatus.Conflict;
                return new GovernedLoopSequentialAuditRecordResult(status, "Existing test audit operation reconciled.");
            }

            _auditLedger.Records[operationId] = (evidenceHash, serialized);
            if (AfterAuditRecord is not null)
            {
                await AfterAuditRecord();
            }

            return new GovernedLoopSequentialAuditRecordResult(GovernedLoopSequentialAuditRecordStatus.Recorded, "Test audit operation recorded.");
        }
    }

    private sealed class SequentialAuditLedger
    {
        public Dictionary<string, (string EvidenceHash, string SerializedAudit)> Records { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class FakeRunStore : ICustomLoopRunStore
    {
        public FakeRunStore(CustomLoopRunRecord current, bool validateSeed = true)
        {
            if (validateSeed)
            {
                Assert.True(CustomLoopRunValidator.Validate(current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(current).Errors));
            }

            Current = current;
        }

        public CustomLoopRunRecord Current { get; private set; }

        public bool ReturnMissing { get; init; }

        public Exception? GetException { get; set; }

        public Action<CancellationToken>? BeforeGet { get; init; }

        public Exception? AppendTerminalWarningException { get; init; }

        public Func<CustomLoopRunRecord, Task>? AfterUpdate { get; init; }

        public Action<CustomLoopRunRecord, CancellationToken>? BeforeUpdate { get; init; }

        public bool ConflictOnOutcomeWrite { get; init; }

        public bool ConflictOnPublicationWrite { get; init; }

        public bool ConcurrentNeedsReviewOnOutcomeConflict { get; init; }

        public bool ReturnMissingAfterOutcomeConflict { get; init; }

        public Exception? GetExceptionAfterOutcomeConflict { get; init; }

        public bool ApplyRawTraceCapacityLimit { get; init; }

        public List<CustomLoopRunRecord> Writes { get; } = [];

        public List<CustomLoopValidationError> ValidationFailures { get; } = [];

        private bool OutcomeConflictInjected { get; set; }

        private bool PublicationConflictInjected { get; set; }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        {
            BeforeGet?.Invoke(cancellationToken);
            if (OutcomeConflictInjected && GetExceptionAfterOutcomeConflict is not null)
            {
                throw GetExceptionAfterOutcomeConflict;
            }

            if (GetException is not null)
            {
                throw GetException;
            }

            return Task.FromResult<CustomLoopRunRecord?>(ReturnMissing || (OutcomeConflictInjected && ReturnMissingAfterOutcomeConflict) ? null : Current);
        }

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CustomLoopRunRecord?>(null);
        }

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CustomLoopRunRecord?>(Current.IsTerminal ? null : Current);
        }

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);
        }

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CustomLoopRunRecord> runs = Current.IsTerminal ? [] : [Current];
            return Task.FromResult(runs);
        }

        public Task<bool> HasSufficientTraceCapacityForDispatchAsync(CustomLoopRunRecord candidate, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Current.LifecycleVersion, expectedLifecycleVersion);
            if (!ApplyRawTraceCapacityLimit)
            {
                return Task.FromResult(true);
            }

            var candidateBytes = JsonSerializer.SerializeToUtf8Bytes(candidate, _rawTraceSizingJsonOptions).LongLength;
            var requiredReserve = CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes + CustomLoopLimits.MaxTraceControlReserveUtf8Bytes + CustomLoopLimits.MaxPermanentTerminalIntegrityReserveUtf8Bytes;
            return Task.FromResult(candidateBytes + requiredReserve <= CustomLoopLimits.MaxRunTraceUtf8Bytes);
        }

        public Task<CustomLoopRunStoreResult> AppendTerminalIntegrityWarningAsync(string runId, int expectedLifecycleVersion, CustomLoopRunEvent warning, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            if (AppendTerminalWarningException is not null)
            {
                throw AppendTerminalWarningException;
            }

            if (Current.LifecycleVersion == expectedLifecycleVersion + 1 && Current.Events[^1] == warning)
            {
                return Task.FromResult(CustomLoopRunStoreResult.Updated(Current));
            }

            if (Current.LifecycleVersion != expectedLifecycleVersion)
            {
                return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion));
            }

            var validation = CustomLoopRunValidator.ValidateTerminalIntegrityWarningAppend(Current, warning);
            if (!validation.IsValid)
            {
                ValidationFailures.AddRange(validation.Errors);
                throw new FormatException("Terminal warning failed validation.");
            }

            Current = Current with { LifecycleVersion = Current.LifecycleVersion + 1, UpdatedAtUtc = warning.TimestampUtc, Events = [.. Current.Events, warning] };
            Writes.Add(Current);
            return Task.FromResult(CustomLoopRunStoreResult.Updated(Current));
        }

        public async Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            BeforeUpdate?.Invoke(run, cancellationToken);
            if (ConflictOnOutcomeWrite && !OutcomeConflictInjected && run.Events.Skip(Current.Events.Length).Any(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved))
            {
                OutcomeConflictInjected = true;
                var concurrentDetail = ConcurrentNeedsReviewOnOutcomeConflict
                    ? "A concurrent controller required review after provider dispatch."
                    : "A concurrent controller requested pause after provider dispatch.";
                var concurrentEvent = new CustomLoopRunEvent(Current.Events.Length + 1, "event-concurrent-control", _now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, concurrentDetail, [], null, null, null, null, null, null, null, null, null, null);
                var concurrent = ConcurrentNeedsReviewOnOutcomeConflict
                    ? Current with
                    {
                        LifecycleVersion = Current.LifecycleVersion + 1,
                        Status = CustomLoopRunStatus.NeedsReview,
                        UpdatedAtUtc = _now,
                        CompletedAtUtc = _now,
                        ExecutionClock = new CustomLoopExecutionClock(Current.ExecutionClock.AccumulatedRunningMilliseconds, null),
                        Events = [.. Current.Events, concurrentEvent],
                        FailureCode = "concurrent_review",
                        FailureDetail = concurrentDetail
                    }
                    : Current with
                    {
                        LifecycleVersion = Current.LifecycleVersion + 1,
                        Status = CustomLoopRunStatus.PauseRequested,
                        UpdatedAtUtc = _now,
                        Events = [.. Current.Events, concurrentEvent]
                    };
                var concurrentValidation = CustomLoopRunValidator.ValidateUpdate(Current, concurrent);
                Assert.True(concurrentValidation.IsValid, string.Join(Environment.NewLine, concurrentValidation.Errors));
                Current = concurrent;
                Writes.Add(concurrent);
                return CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion);
            }

            if (ConflictOnPublicationWrite && !PublicationConflictInjected && run.Events.Skip(Current.Events.Length).Any(item => item.Kind == CustomLoopRunEventKind.ConversationPublished))
            {
                PublicationConflictInjected = true;
                return CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion);
            }

            if (Current.LifecycleVersion != expectedLifecycleVersion)
            {
                return CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion);
            }

            var validation = CustomLoopRunValidator.ValidateUpdate(Current, run);
            if (!validation.IsValid)
            {
                ValidationFailures.AddRange(validation.Errors);
                throw new FormatException("Candidate run failed validation.");
            }
            Current = run;
            Writes.Add(run);
            if (AfterUpdate is not null)
            {
                await AfterUpdate(run);
            }

            return CustomLoopRunStoreResult.Updated(run);
        }
    }

    private sealed class QueueExecutor : ICustomLoopInferenceAttemptExecutor
    {
        private readonly Queue<object> _outcomes;

        public QueueExecutor(params object[] outcomes)
        {
            _outcomes = new Queue<object>(outcomes);
        }

        public List<CustomLoopInferenceAttemptRequest> Requests { get; } = [];

        public Func<CustomLoopInferenceAttemptRequest, Task>? BeforeExecute { get; set; }

        public Func<CustomLoopInferenceAttemptRequest, Task>? AfterExecute { get; set; }

        public Func<CustomLoopInferenceAttemptRequest, Task>? BeforeProviderRequestStarted { get; set; }

        public bool MarkProviderRequestStarted { get; set; } = true;

        public int ProviderRequestStartedCount { get; private set; }

        public async Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeProviderRequestStarted is not null)
            {
                await BeforeProviderRequestStarted(request);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (MarkProviderRequestStarted)
            {
                providerRequestStarted?.Invoke();
                ProviderRequestStartedCount++;
            }

            if (BeforeExecute is not null)
            {
                await BeforeExecute(request);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _outcomes.Dequeue();
            if (outcome is Exception exception)
            {
                throw exception;
            }

            if (AfterExecute is not null)
            {
                await AfterExecute(request);
            }

            return (CustomLoopInferenceAttemptResult)outcome;
        }
    }

    private sealed class ThrowingToolAuthorityProvider : ICustomLoopToolAuthorityProvider
    {
        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(
            string roleId,
            IReadOnlyList<CustomLoopToolAssignment> admittedMaximum,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Canonical sequential execution must not resolve mutable legacy tool authority.");
    }

    private sealed class BlockingCancellationExecutor : ICustomLoopInferenceAttemptExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
        {
            providerRequestStarted?.Invoke();
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation test provider unexpectedly completed.");
        }
    }

    private sealed class RacingCancellationExecutor : ICustomLoopInferenceAttemptExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
        {
            providerRequestStarted?.Invoke();
            Started.TrySetResult();
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancellation race test provider unexpectedly completed.");
        }
    }

    private sealed class RecordingAttemptCancellationBroker : ICustomLoopAttemptCancellationBroker
    {
        private CancellationTokenSource? _cancellation;
        private CancellationToken _competingCancellationToken;
        private TaskCompletionSource<CustomLoopAttemptCancellationResult>? _completion;
        private bool _routedSignalWon;

        public int RegistrationCount { get; private set; }

        public ICustomLoopAttemptCancellationRegistration RegisterActiveAttempt(string runId, CancellationTokenSource cancellation, CancellationToken competingCancellationToken = default)
        {
            RegistrationCount++;
            _cancellation = cancellation;
            _competingCancellationToken = competingCancellationToken;
            _completion = new TaskCompletionSource<CustomLoopAttemptCancellationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Registration(this);
        }

        public async Task<CustomLoopAttemptCancellationResult> RequestCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default)
        {
            _routedSignalWon = !_cancellation!.IsCancellationRequested;
            if (_routedSignalWon)
            {
                _cancellation.Cancel();
            }

            return await _completion!.Task.WaitAsync(cancellationToken);
        }

        private sealed class Registration(RecordingAttemptCancellationBroker owner) : ICustomLoopAttemptCancellationRegistration
        {
            public bool TryConfirmProviderInterruption(CancellationToken observedCancellationToken)
            {
                if (!owner._routedSignalWon || owner._competingCancellationToken.IsCancellationRequested || observedCancellationToken != owner._cancellation!.Token)
                {
                    return false;
                }

                owner._completion!.TrySetResult(new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, "Confirmed."));
                return true;
            }

            public void Dispose()
            {
                owner._completion!.TrySetResult(new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.SignalDelivered, "Completed without confirmation."));
            }
        }
    }

    private sealed class FakeControlOperationStore : ICustomLoopControlOperationStore
    {
        private readonly Dictionary<string, CustomLoopControlOperation> _operations = new(StringComparer.Ordinal);

        public Task<CustomLoopControlOperationStoreResult> BeginAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
        {
            if (_operations.TryGetValue(operation.OperationId, out var existing))
            {
                var status = string.Equals(existing.RequestHash, operation.RequestHash, StringComparison.Ordinal) ? CustomLoopControlOperationStoreStatus.Replayed : CustomLoopControlOperationStoreStatus.Conflict;
                return Task.FromResult(new CustomLoopControlOperationStoreResult(status, existing));
            }

            _operations.Add(operation.OperationId, operation);
            return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Created, operation));
        }

        public Task<CustomLoopControlOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _operations.TryGetValue(operationId, out var operation);
            return Task.FromResult(operation);
        }

        public Task<CustomLoopControlOperationStoreResult> CompleteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
        {
            if (!_operations.TryGetValue(operation.OperationId, out var existing))
            {
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.NotFound, null));
            }

            if (!string.Equals(existing.RequestHash, operation.RequestHash, StringComparison.Ordinal))
            {
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing));
            }

            if (existing.State == CustomLoopControlOperationState.Complete)
            {
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, existing));
            }

            _operations[operation.OperationId] = operation;
            return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Completed, operation));
        }
    }

    private sealed class AvailableModel : ICustomLoopModelAvailability
    {
        public Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class CancellingAuthorityProvider(CancellationTokenSource callerCancellation, int cancelOnCall) : ICustomLoopToolAuthorityProvider
    {
        private int _callCount;

        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == cancelOnCall)
            {
                callerCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var admitted = admittedMaximum.ToArray();
            var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
            var roleHash = CustomLoopTraceContentHash.Compute(roleId + "\n" + string.Join('\n', admitted.OrderBy(value => value)));
            var catalogHash = CustomLoopTraceContentHash.Compute(string.Join('\n', catalog));
            return Task.FromResult(new CustomLoopToolAuthoritySnapshot(roleId, admitted, admitted, catalog, admitted, roleHash, catalogHash, _now, true, "Test authority snapshot."));
        }
    }

    private sealed class FinalDispatchBoundaryCancellingTimeProvider(DateTimeOffset now, FakeRunStore store, CancellationTokenSource cancellation) : TimeProvider
    {
        private int _callsAfterAttemptStart;

        public override DateTimeOffset GetUtcNow()
        {
            if (store.Current.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted) && ++_callsAfterAttemptStart == 2)
            {
                cancellation.Cancel();
            }

            return now;
        }
    }

    private sealed class FinalDispatchDeadlineTimeProvider(DateTimeOffset now, FakeRunStore store, bool reportDeadlineReached) : TimeProvider
    {
        private int _callsAfterAttemptStart;

        public override DateTimeOffset GetUtcNow()
        {
            if (!store.Current.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted))
            {
                return now;
            }

            _callsAfterAttemptStart++;
            if (_callsAfterAttemptStart == 2)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }

            return reportDeadlineReached && _callsAfterAttemptStart >= 3
                ? now.AddMilliseconds(CustomLoopLimits.MaxRunExecutionMilliseconds)
                : now.AddMilliseconds(CustomLoopLimits.MaxRunExecutionMilliseconds - 1);
        }
    }

    private sealed class CanonicalExitBoundaryDeadlineTimeProvider(DateTimeOffset now, FakeRunStore store) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
            => store.Current.Checkpoint.NextStepIndex == store.Current.AdmittedDefinition.InferenceSteps.Length
                ? now.AddMilliseconds(CustomLoopLimits.MaxRunExecutionMilliseconds)
                : now;
    }

    private sealed class FinalDispatchActionTimeProvider(DateTimeOffset now, FakeRunStore store) : TimeProvider
    {
        private int _callsAfterAttemptStart;

        public Action? AtFinalBoundary { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (store.Current.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted) && ++_callsAfterAttemptStart == 2)
            {
                var action = AtFinalBoundary;
                AtFinalBoundary = null;
                action?.Invoke();
            }

            return now;
        }
    }

    private sealed class FixedAuthorityProvider(CustomLoopToolAuthoritySnapshot snapshot) : ICustomLoopToolAuthorityProvider
    {
        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class TestAuthorityProvider : ICustomLoopToolAuthorityProvider
    {
        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
        {
            var admitted = admittedMaximum.ToArray();
            return Task.FromResult(Authority(roleId, admitted, admitted));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingPublisher : ICustomLoopConversationPublisher
    {
        public List<CustomLoopConversationPublicationRequest> Requests { get; } = [];

        public Func<CustomLoopConversationPublicationRequest, Task>? BeforePublish { get; set; }

        public CustomLoopConversationPublicationResult? NextResult { get; set; }

        public bool ReturnNull { get; set; }

        public async Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            Requests.Add(request);
            if (BeforePublish is not null)
            {
                await BeforePublish(request);
            }

            cancellationToken.ThrowIfCancellationRequested();
            request.AppendStarted?.Invoke();

            return ReturnNull ? null! : NextResult ?? new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.Published, request.OperationId, "Published.");
        }
    }

    private sealed class ThrowingOnRevalidationCapabilityAdmissionService(int throwOnRevalidation) : ICapabilityAdmissionService
    {
        private int _revalidationCount;

        public Task<CapabilityAdmissionResult> AdmitAsync(CapabilityDependencyManifest requirements, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CapabilityRevalidationResult> RevalidateAsync(CapabilityAdmissionSnapshot snapshot, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_revalidationCount == throwOnRevalidation)
            {
                throw new IOException("Catalog revalidation failed.");
            }

            return Task.FromResult(new CapabilityRevalidationResult(true, snapshot.Pins, "Capabilities are current."));
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Func<AuditEvent, bool>? FailPredicate { get; init; }

        public Action<AuditEvent, CancellationToken>? BeforeAppend { get; init; }

        public Func<AuditEvent, Task>? AfterAppend { get; set; }

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (FailPredicate?.Invoke(auditEvent) == true)
            {
                throw new IOException("Audit unavailable.");
            }

            BeforeAppend?.Invoke(auditEvent, cancellationToken);
            Assert.False(cancellationToken.IsCancellationRequested);
            Events.Add(auditEvent);
            if (AfterAppend is not null)
            {
                await AfterAppend(auditEvent);
            }
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
        }
    }
}
