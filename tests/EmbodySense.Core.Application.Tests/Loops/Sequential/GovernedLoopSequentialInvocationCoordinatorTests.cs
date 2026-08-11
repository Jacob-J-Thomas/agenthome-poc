using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialInvocationCoordinatorTests
{
    private static readonly DateTimeOffset _coordinatedAtUtc = GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(3);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', 64);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Exact_pending_operation_is_bound_admitted_materialized_completed_and_dispatched_in_order(
        bool includeConversation,
        bool allowWorkspaceTools)
    {
        var context = await ContextAsync(includeConversation, allowWorkspaceTools);
        var operations = new RecordingOperationStore(context.Operation);
        var admission = new RecordingAdmissionService(context.AdmissionResult)
        {
            BeforeCall = () => Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, operations.Operation?.BindingState),
        };
        var runStore = new GovernedLoopSequentialRunMaterializerTests.RecordingRunStore();
        var materializer = new GovernedLoopSequentialRunMaterializer(
            runStore,
            new GovernedLoopSequentialRunMaterializerTests.RecordingAuditRecorder(),
            new GovernedLoopSequentialRunMaterializerTests.RecordingEventIdentityGenerator(),
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(_coordinatedAtUtc));
        var runtime = new RecordingOrderedRuntime
        {
            BeforeRun = () => Assert.Equal(CustomLoopInvocationOperationState.Complete, operations.Operation?.State),
        };
        var coordinator = new GovernedLoopSequentialInvocationCoordinator(
            _workspaceId,
            operations,
            Writer(operations),
            admission,
            materializer,
            runtime,
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(_coordinatedAtUtc));

        var result = await coordinator.InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Executed, result.Status);
        Assert.True(result.ProviderWasInvoked);
        Assert.Equal(1, operations.BindCallCount);
        Assert.Equal(1, operations.CompleteCallCount);
        Assert.Equal(1, admission.CallCount);
        Assert.Equal(1, runtime.RunCallCount);
        Assert.Equal(context.Invocation.ContentHash, operations.Operation?.SequentialInvocationSnapshot?.ContentHash);
        Assert.Equal(context.Invocation.InvokingConversation?.ConversationId, operations.Operation?.InvokingConversationId);
        Assert.Equal(context.Receipt.Evidence.Binding.RunId, operations.Operation?.RunId);
        Assert.Equal(CustomLoopInvocationOutcome.Admitted, operations.Operation?.Outcome);
        var runtimeRequest = Assert.IsType<GovernedLoopSequentialOrderedRunRequest>(runtime.LastRunRequest);
        Assert.Equal(context.Receipt.Evidence.Binding.RunId, runtimeRequest.Anchor.AdapterBinding.ExecutionBinding.RunId);
        Assert.Equal(context.Receipt.ContentHash, runtimeRequest.Anchor.AdapterBinding.AdmissionReceiptHash);
        Assert.Equal(context.Artifact.ArtifactHash, runtimeRequest.Anchor.AdapterBinding.GraphArtifactHash);
        Assert.Equal(context.Artifact.LayoutHash, runtimeRequest.Anchor.AdapterBinding.GraphLayoutHash);
        Assert.Equal(context.Invocation.ContentHash, runtimeRequest.Anchor.AdapterBinding.InvocationPayloadHash);
    }

    [Fact]
    public async Task Missing_or_substituted_pre_admission_operation_prevents_admission_and_runtime_work()
    {
        var context = await ContextAsync();
        var missingOperations = new RecordingOperationStore(null);
        var missingAdmission = new RecordingAdmissionService(context.AdmissionResult);
        var missingRuntime = new RecordingOrderedRuntime();
        var missing = await Coordinator(missingOperations, missingAdmission, new RecordingMaterializer(), missingRuntime).InvokeAsync(context.Request);

        var substituted = context.Operation with { Surface = "cli" };
        substituted = substituted with
        {
            RequestHash = CustomLoopInvocationRequestHash.Compute(
                substituted.OperationId,
                substituted.LoopId,
                substituted.ExpectedDefinitionVersion,
                substituted.ExpectedDefinitionHash,
                substituted.Actor,
                substituted.Surface,
                substituted.CurrentRoleId,
                context.Invocation.TriggerPrompt,
                substituted.Provider,
                substituted.Model),
        };
        var conflictOperations = new RecordingOperationStore(substituted);
        var conflictAdmission = new RecordingAdmissionService(context.AdmissionResult);
        var conflictRuntime = new RecordingOrderedRuntime();
        var conflict = await Coordinator(conflictOperations, conflictAdmission, new RecordingMaterializer(), conflictRuntime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.NotFound, missing.Status);
        Assert.Equal(GovernedLoopSequentialInvocationStatus.Conflict, conflict.Status);
        Assert.Equal(0, missingAdmission.CallCount);
        Assert.Equal(0, conflictAdmission.CallCount);
        Assert.Equal(0, missingRuntime.RunCallCount);
        Assert.Equal(0, conflictRuntime.RunCallCount);
    }

    [Fact]
    public async Task Changed_snapshot_cannot_replace_an_already_bound_pre_admission_snapshot()
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation);
        var firstAdmission = new RecordingAdmissionService(new GovernedLoopAdmissionResult(
            GovernedLoopAdmissionStatus.Unavailable,
            context.AdmissionRequest.OperationId,
            context.AdmissionRequest.RequestHash,
            null));
        var first = await Coordinator(operations, firstAdmission, new RecordingMaterializer(), new RecordingOrderedRuntime()).InvokeAsync(context.Request);
        var changedInvocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            context.Invocation.SchemaVersion,
            context.Invocation.TriggerPrompt,
            context.Invocation.ModelSnapshot,
            context.Invocation.InvokingConversation! with { CapturedVersion = "version-2" },
            context.Invocation.ContextCapturedAtUtc,
            context.Invocation.ContextManifest,
            string.Empty));
        var changedAdmissionRequest = GovernedLoopAdmissionRequestHash.Apply(context.AdmissionRequest with
        {
            InvocationPayloadHash = changedInvocation.ContentHash,
            RequestHash = string.Empty,
        });
        var changed = context.Request with
        {
            AdmissionRequest = changedAdmissionRequest,
            InvocationSnapshot = changedInvocation,
        };
        var secondAdmission = new RecordingAdmissionService(context.AdmissionResult);
        var runtime = new RecordingOrderedRuntime();

        var conflict = await Coordinator(operations, secondAdmission, new RecordingMaterializer(), runtime).InvokeAsync(changed);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Unavailable, first.Status);
        Assert.Equal(GovernedLoopSequentialInvocationStatus.Conflict, conflict.Status);
        Assert.Equal(1, operations.BindCallCount);
        Assert.Equal(0, secondAdmission.CallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Same_operation_cannot_bind_a_changed_layout_after_admission_was_unavailable()
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation);
        var unavailableAdmission = new RecordingAdmissionService(new GovernedLoopAdmissionResult(
            GovernedLoopAdmissionStatus.Unavailable,
            context.AdmissionRequest.OperationId,
            context.AdmissionRequest.RequestHash,
            null));
        var first = await Coordinator(operations, unavailableAdmission, new RecordingMaterializer(), new RecordingOrderedRuntime()).InvokeAsync(context.Request);
        var changedArtifact = WithChangedLayout(context.Artifact);
        var changedPlan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(changedArtifact).Plan);
        var changedRequest = context.Request with
        {
            Artifact = changedArtifact,
            Plan = changedPlan,
        };
        var secondAdmission = new RecordingAdmissionService(context.AdmissionResult);
        var runtime = new RecordingOrderedRuntime();

        var conflict = await Coordinator(operations, secondAdmission, new RecordingMaterializer(), runtime).InvokeAsync(changedRequest);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Unavailable, first.Status);
        Assert.NotEqual(context.Artifact.LayoutHash, changedArtifact.LayoutHash);
        Assert.NotEqual(context.Artifact.ArtifactHash, changedArtifact.ArtifactHash);
        Assert.Equal(GovernedLoopSequentialInvocationStatus.Conflict, conflict.Status);
        Assert.Equal(0, secondAdmission.CallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Substituted_successful_bind_result_conflicts_before_admission()
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation)
        {
            BindMutation = operation => operation with { CreatedAtUtc = operation.CreatedAtUtc.AddSeconds(1) },
        };
        var admission = new RecordingAdmissionService(context.AdmissionResult);
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(operations, admission, new RecordingMaterializer(), runtime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Conflict, result.Status);
        Assert.Equal(0, admission.CallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Theory]
    [InlineData(GovernedLoopAdmissionStatus.Invalid, GovernedLoopSequentialInvocationStatus.Invalid)]
    [InlineData(GovernedLoopAdmissionStatus.Conflict, GovernedLoopSequentialInvocationStatus.Conflict)]
    [InlineData(GovernedLoopAdmissionStatus.Unavailable, GovernedLoopSequentialInvocationStatus.Unavailable)]
    [InlineData(GovernedLoopAdmissionStatus.Ambiguous, GovernedLoopSequentialInvocationStatus.Unavailable)]
    [InlineData(GovernedLoopAdmissionStatus.LimitExceeded, GovernedLoopSequentialInvocationStatus.LimitExceeded)]
    public async Task Nonterminal_admission_results_never_materialize_or_dispatch(
        GovernedLoopAdmissionStatus admissionStatus,
        GovernedLoopSequentialInvocationStatus expectedStatus)
    {
        var context = await ContextAsync();
        var admission = new RecordingAdmissionService(new GovernedLoopAdmissionResult(
            admissionStatus,
            context.AdmissionRequest.OperationId,
            context.AdmissionRequest.RequestHash,
            null));
        var materializer = new RecordingMaterializer();
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(new RecordingOperationStore(context.Operation), admission, materializer, runtime).InvokeAsync(context.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, materializer.CallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Definitive_rejection_completes_strict_receipt_without_creating_or_dispatching_a_run()
    {
        var context = await ContextAsync();
        var roleReference = new GovernedLoopAdmissionEvidenceReference(
            GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision,
            GovernedLoopAdmissionContractHash.ComputeContextualRoleReferenceHash(context.Receipt.Intent.Role));
        var rejection = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionRejection(
            GovernedLoopAdmissionRejection.CurrentSchemaVersion,
            context.Receipt.Intent,
            GovernedLoopAdmissionFailureCode.RoleInactive,
            null,
            null,
            [roleReference],
            context.Receipt.RecordedAtUtc,
            string.Empty));
        var outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            context.Receipt.Intent,
            GovernedLoopAdmissionDisposition.Rejected,
            null,
            rejection,
            context.Receipt.RecordedAtUtc,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        var operations = new RecordingOperationStore(context.Operation);
        var materializer = new RecordingMaterializer();
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(
            operations,
            new RecordingAdmissionService(new GovernedLoopAdmissionResult(
                GovernedLoopAdmissionStatus.Rejected,
                context.AdmissionRequest.OperationId,
                context.AdmissionRequest.RequestHash,
                outcome)),
            materializer,
            runtime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Rejected, result.Status);
        Assert.Equal(CustomLoopInvocationOperationState.Complete, operations.Operation?.State);
        Assert.Equal(CustomLoopInvocationOutcome.Rejected, operations.Operation?.Outcome);
        Assert.Null(operations.Operation?.RunId);
        Assert.Equal(0, materializer.CallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Foreign_workspace_rejection_is_rejected_before_receipt_completion()
    {
        var context = await ContextAsync();
        var foreignIntent = context.Receipt.Intent with
        {
            WorkspaceId = "workspace-sha256:" + new string('b', 64),
        };
        var roleReference = new GovernedLoopAdmissionEvidenceReference(
            GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision,
            GovernedLoopAdmissionContractHash.ComputeContextualRoleReferenceHash(foreignIntent.Role));
        var rejection = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionRejection(
            GovernedLoopAdmissionRejection.CurrentSchemaVersion,
            foreignIntent,
            GovernedLoopAdmissionFailureCode.RoleInactive,
            null,
            null,
            [roleReference],
            context.Receipt.RecordedAtUtc,
            string.Empty));
        var outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            foreignIntent,
            GovernedLoopAdmissionDisposition.Rejected,
            null,
            rejection,
            context.Receipt.RecordedAtUtc,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        var operations = new RecordingOperationStore(context.Operation);
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(
            operations,
            new RecordingAdmissionService(new GovernedLoopAdmissionResult(
                GovernedLoopAdmissionStatus.Rejected,
                context.AdmissionRequest.OperationId,
                context.AdmissionRequest.RequestHash,
                outcome)),
            new RecordingMaterializer(),
            runtime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Invalid, result.Status);
        Assert.Equal(0, operations.CompleteCallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Substituted_capability_root_set_is_rejected_before_materialization_or_runtime()
    {
        var context = await ContextAsync();
        var substitutedCapability = CapabilityAdmission(
            context.Artifact.ArtifactHash,
            context.Receipt.Intent.WorkspaceId,
            [GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId]);
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            context.Receipt.Evidence.SchemaVersion,
            context.Receipt.Evidence.IntentHash,
            context.Receipt.Evidence.Binding,
            context.Receipt.Evidence.EffectiveAuthority,
            substitutedCapability,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(
                context.Receipt.Intent,
                context.Receipt.Evidence.EffectiveAuthority,
                substitutedCapability),
            context.Receipt.Evidence.EvaluatedAtUtc,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(context.Receipt with
        {
            Evidence = evidence,
            ContentHash = string.Empty,
        });
        var outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            receipt.Intent,
            GovernedLoopAdmissionDisposition.Admitted,
            receipt,
            null,
            receipt.RecordedAtUtc,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        var materializer = new RecordingMaterializer();
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(
            new RecordingOperationStore(context.Operation),
            new RecordingAdmissionService(new GovernedLoopAdmissionResult(
                GovernedLoopAdmissionStatus.Admitted,
                context.AdmissionRequest.OperationId,
                context.AdmissionRequest.RequestHash,
                outcome)),
            materializer,
            runtime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Invalid, result.Status);
        Assert.Equal(0, materializer.CallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Theory]
    [InlineData(CustomLoopRunStatus.Running, GovernedLoopSequentialInvocationStatus.RecoveryRequired)]
    [InlineData(CustomLoopRunStatus.Paused, GovernedLoopSequentialInvocationStatus.RecoveryRequired)]
    [InlineData(CustomLoopRunStatus.NeedsReview, GovernedLoopSequentialInvocationStatus.Terminal)]
    [InlineData(CustomLoopRunStatus.Completed, GovernedLoopSequentialInvocationStatus.Terminal)]
    public async Task Existing_non_admitted_lifecycle_states_never_call_first_run(
        CustomLoopRunStatus runStatus,
        GovernedLoopSequentialInvocationStatus expectedStatus)
    {
        var context = await ContextAsync();
        var ready = await MaterializedAsync(context);
        var run = Assert.IsType<CustomLoopRunRecord>(ready.Run) with
        {
            Status = runStatus,
            CompletedAtUtc = runStatus is CustomLoopRunStatus.NeedsReview or CustomLoopRunStatus.Completed
                ? _coordinatedAtUtc
                : null,
        };
        var materializer = new RecordingMaterializer
        {
            Result = ready with
            {
                Status = GovernedLoopSequentialMaterializationStatus.Replayed,
                Run = run,
            },
        };
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(
            new RecordingOperationStore(context.Operation),
            new RecordingAdmissionService(context.AdmissionResult),
            materializer,
            runtime).InvokeAsync(context.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Strict_receipt_completion_conflict_forbids_runtime_dispatch()
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation)
        {
            CompleteStatus = CustomLoopInvocationOperationStoreStatus.Conflict,
        };
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(
            operations,
            new RecordingAdmissionService(context.AdmissionResult),
            new RecordingMaterializer { Result = await MaterializedAsync(context) },
            runtime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Conflict, result.Status);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Substituted_snapshot_with_copied_hash_in_successful_completion_conflicts_and_forbids_dispatch()
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation)
        {
            CompleteMutation = operation => operation with
            {
                SequentialInvocationSnapshot = operation.SequentialInvocationSnapshot! with
                {
                    TriggerPrompt = "Mutated content carrying the copied old hash.",
                },
            },
        };
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(
            operations,
            new RecordingAdmissionService(context.AdmissionResult),
            new RecordingMaterializer { Result = await MaterializedAsync(context) },
            runtime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Conflict, result.Status);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Completion_capacity_retries_once_after_governed_retention_then_dispatches()
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation);
        operations.CompleteStatuses.Enqueue(CustomLoopInvocationOperationStoreStatus.LimitExceeded);
        operations.CompleteStatuses.Enqueue(CustomLoopInvocationOperationStoreStatus.Completed);
        var runtime = new RecordingOrderedRuntime();

        var result = await Coordinator(
            operations,
            new RecordingAdmissionService(context.AdmissionResult),
            new RecordingMaterializer { Result = await MaterializedAsync(context) },
            runtime).InvokeAsync(context.Request);

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Executed, result.Status);
        Assert.Equal(2, operations.CompleteCallCount);
        Assert.Equal(1, operations.RetentionCommitCallCount);
        Assert.Equal(1, runtime.RunCallCount);
    }

    [Theory]
    [InlineData(true, false, GovernedLoopSequentialInvocationStatus.AuditUnavailable)]
    [InlineData(false, true, GovernedLoopSequentialInvocationStatus.Invalid)]
    public async Task Completion_retention_audit_or_integrity_failure_remains_closed(
        bool failAudit,
        bool failReservation,
        GovernedLoopSequentialInvocationStatus expected)
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation)
        {
            RetentionReserveException = failReservation ? new FormatException("corrupt retention state") : null,
        };
        operations.CompleteStatuses.Enqueue(CustomLoopInvocationOperationStoreStatus.RetentionRequired);
        var runtime = new RecordingOrderedRuntime();
        var writer = Writer(operations, new RecordingRetentionAuditLog { FailAppend = failAudit });
        var coordinator = new GovernedLoopSequentialInvocationCoordinator(
            _workspaceId,
            operations,
            writer,
            new RecordingAdmissionService(context.AdmissionResult),
            new RecordingMaterializer { Result = await MaterializedAsync(context) },
            runtime,
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(_coordinatedAtUtc));

        var result = await coordinator.InvokeAsync(context.Request);

        Assert.Equal(expected, result.Status);
        Assert.Equal(1, operations.CompleteCallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    [Fact]
    public async Task Invalid_request_or_publication_artifact_substitution_does_no_port_work()
    {
        var context = await ContextAsync();
        var operations = new RecordingOperationStore(context.Operation);
        var admission = new RecordingAdmissionService(context.AdmissionResult);
        var materializer = new RecordingMaterializer();
        var runtime = new RecordingOrderedRuntime();
        var coordinator = Coordinator(operations, admission, materializer, runtime);
        var otherArtifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2);

        var invalid = await coordinator.InvokeAsync(null);
        var substituted = await coordinator.InvokeAsync(context.Request with { Artifact = otherArtifact });

        Assert.Equal(GovernedLoopSequentialInvocationStatus.Invalid, invalid.Status);
        Assert.Equal(GovernedLoopSequentialInvocationStatus.Invalid, substituted.Status);
        Assert.Equal(0, operations.GetCallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.Equal(0, materializer.CallCount);
        Assert.Equal(0, runtime.RunCallCount);
    }

    private static GovernedLoopSequentialInvocationCoordinator Coordinator(
        RecordingOperationStore operations,
        RecordingAdmissionService admission,
        IGovernedLoopSequentialRunMaterializer materializer,
        RecordingOrderedRuntime runtime)
        => new(
            _workspaceId,
            operations,
            Writer(operations),
            admission,
            materializer,
            runtime,
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(_coordinatedAtUtc));

    private static CustomLoopInvocationReceiptWriter Writer(
        RecordingOperationStore operations,
        RecordingRetentionAuditLog? audit = null)
        => new(
            operations,
            new CustomLoopInvocationReceiptRetentionService(
                operations,
                audit ?? new RecordingRetentionAuditLog(),
                new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(_coordinatedAtUtc)));

    private static GovernedLoopGraphRevisionArtifact WithChangedLayout(GovernedLoopGraphRevisionArtifact artifact)
    {
        var graph = artifact.Graph;
        var changedGraph = GovernedLoopGraphDefinition.Create(
            graph.SchemaVersion,
            graph.GraphId,
            graph.RevisionId,
            graph.Purpose,
            graph.OwningRole,
            graph.EntryNodeId,
            graph.TerminalNodeIds,
            graph.AuthorityCeiling,
            graph.ValueSchemas,
            graph.Nodes,
            graph.ControlEdges,
            graph.Bindings,
            graph.OutputContract,
            new GovernedLoopDisplayMetadata(
                graph.DisplayMetadata.DisplayName,
                "Changed display-only metadata must still change full artifact identity.",
                graph.DisplayMetadata.Nodes));
        return GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            artifact.RevisionArtifact,
            changedGraph);
    }

    private static async Task<TestContext> ContextAsync(
        bool includeConversation = true,
        bool allowWorkspaceTools = false)
    {
        var materialization = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(
            includeConversation,
            allowWorkspaceTools: allowWorkspaceTools);
        var projection = GovernedLoopSequentialLegacyDefinitionProjector.ProjectPrepared(
            materialization.AdmissionRequest.OperationId,
            materialization.Invocation,
            materialization.Plan,
            materialization.Artifact);
        var definition = Assert.IsType<CustomLoopDefinition>(projection.Definition);
        var operation = new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            materialization.AdmissionRequest.OperationId,
            string.Empty,
            materialization.Artifact.Graph.GraphId,
            definition.DefinitionVersion,
            definition.ContentHash,
            materialization.AdmissionRequest.ActorId.Value,
            materialization.AdmissionRequest.Surface,
            materialization.Artifact.Graph.OwningRole.Identity.RoleId,
            CustomLoopInvocationRequestHash.ComputePromptHash(materialization.Invocation.TriggerPrompt),
            materialization.Invocation.ModelSnapshot.Provider,
            materialization.Invocation.ModelSnapshot.Model,
            CustomLoopInvocationBindingState.Unbound,
            null,
            null,
            GovernedLoopSequentialApplicationTestFixture.Now,
            GovernedLoopSequentialApplicationTestFixture.Now,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            [],
            "The canonical invocation is pending before immutable context binding.")
        {
            SequentialAdmissionRequestHash = materialization.AdmissionRequest.RequestHash,
            SequentialArtifactHash = materialization.Artifact.ArtifactHash,
        };
        operation = CustomLoopInvocationRequestHash.ApplySequential(operation);
        var outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            materialization.Receipt.Intent,
            GovernedLoopAdmissionDisposition.Admitted,
            materialization.Receipt,
            null,
            materialization.Receipt.RecordedAtUtc,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        var admissionResult = new GovernedLoopAdmissionResult(
            GovernedLoopAdmissionStatus.Admitted,
            materialization.AdmissionRequest.OperationId,
            materialization.AdmissionRequest.RequestHash,
            outcome);
        return new TestContext(
            materialization.Artifact,
            materialization.Plan,
            materialization.Invocation,
            materialization.AdmissionRequest,
            materialization.Receipt,
            operation,
            admissionResult,
            new GovernedLoopSequentialInvocationRequest(
                GovernedLoopSequentialInvocationRequest.CurrentSchemaVersion,
                materialization.AdmissionRequest,
                materialization.Artifact,
                materialization.Plan,
                materialization.Invocation),
            materialization.Request);
    }

    private static async Task<GovernedLoopSequentialMaterializationResult> MaterializedAsync(TestContext context)
    {
        var materializer = new GovernedLoopSequentialRunMaterializer(
            new GovernedLoopSequentialRunMaterializerTests.RecordingRunStore(),
            new GovernedLoopSequentialRunMaterializerTests.RecordingAuditRecorder(),
            new GovernedLoopSequentialRunMaterializerTests.RecordingEventIdentityGenerator(),
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(_coordinatedAtUtc));
        var result = await materializer.MaterializeAsync(context.MaterializationRequest);
        Assert.True(result.IsReady);
        return result;
    }

    private static CapabilityAdmissionSnapshot CapabilityAdmission(
        string artifactHash,
        string workspaceId,
        IReadOnlyList<string> capabilityIds)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-" + artifactHash[..32], out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var any, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + artifactHash, out var checksum, out _));
        var required = capabilityIds.Order(StringComparer.Ordinal).Select(value =>
        {
            Assert.True(CapabilityId.TryParse(value, out var id, out _));
            return new CapabilityDependency(id!, any!);
        }).ToArray();
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            required,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        return TestCapabilityAdmissionFactory.Create(manifest, GovernedLoopSequentialApplicationTestFixture.Now) with
        {
            WorkspaceScopeId = workspaceId,
        };
    }

    private sealed record TestContext(
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopSequentialPlan Plan,
        GovernedLoopSequentialInvocationSnapshot Invocation,
        GovernedLoopAdmissionRequest AdmissionRequest,
        GovernedLoopAdmissionReceipt Receipt,
        CustomLoopInvocationOperation Operation,
        GovernedLoopAdmissionResult AdmissionResult,
        GovernedLoopSequentialInvocationRequest Request,
        GovernedLoopSequentialMaterializationRequest MaterializationRequest);

    private sealed class RecordingAdmissionService(GovernedLoopAdmissionResult result) : IGovernedLoopAdmissionService
    {
        public Action? BeforeCall { get; init; }

        public int CallCount { get; private set; }

        public Task<GovernedLoopAdmissionResult> AdmitAsync(
            GovernedLoopAdmissionRequest? request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeCall?.Invoke();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingMaterializer : IGovernedLoopSequentialRunMaterializer
    {
        public GovernedLoopSequentialMaterializationResult Result { get; init; } = new(
            GovernedLoopSequentialMaterializationStatus.Unavailable,
            null,
            null,
            "not configured");

        public int CallCount { get; private set; }

        public GovernedLoopSequentialMaterializationRequest? LastRequest { get; private set; }

        public Task<GovernedLoopSequentialMaterializationResult> MaterializeAsync(
            GovernedLoopSequentialMaterializationRequest? request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingOrderedRuntime : IGovernedLoopSequentialOrderedRuntime
    {
        public Action? BeforeRun { get; init; }

        public int RunCallCount { get; private set; }

        public GovernedLoopSequentialOrderedRunRequest? LastRunRequest { get; private set; }

        public CustomLoopOrderedRunResult Result { get; init; } = new(
            CustomLoopOrderedRunStatus.Completed,
            null,
            "ordered execution completed",
            ProviderWasInvoked: true);

        public Task<CustomLoopOrderedRunResult> RunAsync(
            GovernedLoopSequentialOrderedRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeRun?.Invoke();
            RunCallCount++;
            LastRunRequest = request;
            return Task.FromResult(Result);
        }

        public Task<CustomLoopOrderedRunResult> ResumeAsync(
            GovernedLoopSequentialOrderedResumeRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingRetentionAuditLog : IAuditLog
    {
        public bool FailAppend { get; init; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FailAppend
                ? Task.FromException(new IOException("retention audit unavailable"))
                : Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class RecordingOperationStore(CustomLoopInvocationOperation? operation) : ICustomLoopInvocationOperationStore
    {
        private CustomLoopInvocationReceiptRetentionOperation? _retentionOperation;

        public CustomLoopInvocationOperation? Operation { get; private set; } = operation;

        public CustomLoopInvocationOperationStoreStatus? CompleteStatus { get; init; }

        public Queue<CustomLoopInvocationOperationStoreStatus> CompleteStatuses { get; } = [];

        public Func<CustomLoopInvocationOperation, CustomLoopInvocationOperation>? BindMutation { get; init; }

        public Func<CustomLoopInvocationOperation, CustomLoopInvocationOperation>? CompleteMutation { get; init; }

        public Exception? RetentionReserveException { get; init; }

        public int GetCallCount { get; private set; }

        public int BindCallCount { get; private set; }

        public int CompleteCallCount { get; private set; }

        public int RetentionCommitCallCount { get; private set; }

        public Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCallCount++;
            return Task.FromResult(Operation is not null && string.Equals(Operation.OperationId, operationId, StringComparison.Ordinal)
                ? Operation
                : null);
        }

        public Task<CustomLoopInvocationOperationStoreResult> BindAsync(
            CustomLoopInvocationOperation candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BindCallCount++;
            if (Operation is null)
            {
                return Task.FromResult(new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.NotFound, null));
            }

            Operation = BindMutation?.Invoke(candidate with { CreatedAtUtc = Operation.CreatedAtUtc })
                ?? candidate with { CreatedAtUtc = Operation.CreatedAtUtc };
            return Task.FromResult(new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Bound, Operation));
        }

        public Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(
            CustomLoopInvocationOperation candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteCallCount++;
            var status = CompleteStatuses.Count > 0 ? CompleteStatuses.Dequeue() : CompleteStatus;
            if (status is { } configured
                && configured is not (CustomLoopInvocationOperationStoreStatus.Completed or CustomLoopInvocationOperationStoreStatus.Replayed))
            {
                return Task.FromResult(new CustomLoopInvocationOperationStoreResult(configured, Operation));
            }

            Operation = CompleteMutation?.Invoke(candidate with { CreatedAtUtc = Operation?.CreatedAtUtc ?? candidate.CreatedAtUtc })
                ?? candidate with { CreatedAtUtc = Operation?.CreatedAtUtc ?? candidate.CreatedAtUtc };
            return Task.FromResult(new CustomLoopInvocationOperationStoreResult(
                status ?? CustomLoopInvocationOperationStoreStatus.Completed,
                Operation));
        }

        public Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation candidate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomLoopInvocationReceiptRetentionReservationResult> ReserveCompletedReceiptRetentionAsync(
            CustomLoopInvocationReceiptRetentionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RetentionReserveException is not null)
            {
                throw RetentionReserveException;
            }

            _retentionOperation ??= new CustomLoopInvocationReceiptRetentionOperation(
                CustomLoopInvocationReceiptRetentionOperation.CurrentSchemaVersion,
                request.OperationId,
                request.Actor,
                request.Surface,
                request.RequestedAtUtc,
                request.ReplayCutoffUtc,
                request.RequestedAtUtc,
                request.RequestedAtUtc,
                [new CustomLoopInvocationReceiptRetentionCandidate("expired-receipt", request.ReplayCutoffUtc.AddTicks(-1), new string('f', 64), 100)],
                CustomLoopInvocationReceiptRetentionOperationState.Reserved,
                0,
                0);
            return Task.FromResult(new CustomLoopInvocationReceiptRetentionReservationResult(
                CustomLoopInvocationReceiptRetentionReservationStatus.Reserved,
                _retentionOperation));
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionIntentAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            _retentionOperation = _retentionOperation! with
            {
                State = CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded,
                UpdatedAtUtc = updatedAtUtc,
            };
            return Task.FromResult(_retentionOperation);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> CommitCompletedReceiptRetentionAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            RetentionCommitCallCount++;
            _retentionOperation = _retentionOperation! with
            {
                State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted,
                UpdatedAtUtc = updatedAtUtc,
                DeletedReceiptCount = 1,
                DeletedReceiptUtf8Bytes = 100,
            };
            return Task.FromResult(_retentionOperation);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            _retentionOperation = _retentionOperation! with
            {
                State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted,
                UpdatedAtUtc = updatedAtUtc,
            };
            return Task.FromResult(_retentionOperation);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            _retentionOperation = _retentionOperation! with
            {
                State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded,
                UpdatedAtUtc = updatedAtUtc,
            };
            return Task.FromResult(_retentionOperation);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            _retentionOperation = _retentionOperation! with
            {
                State = CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning,
                UpdatedAtUtc = updatedAtUtc,
            };
            return Task.FromResult(_retentionOperation);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
