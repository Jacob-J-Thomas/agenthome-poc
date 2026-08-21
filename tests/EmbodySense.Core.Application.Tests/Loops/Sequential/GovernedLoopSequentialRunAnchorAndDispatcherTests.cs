using System.Text;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Admission;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialRunAnchorAndDispatcherTests
{
    [Fact]
    public async Task Durable_common_and_application_terminal_evidence_use_one_payload_bound_hash()
    {
        var context = await ContextAsync();
        var node = context.Plan.Nodes[1];
        var receipt = Evidence(context, node, GovernedLoopSequentialNodeHandlerResultStatus.Completed);
        var durable = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            receipt.WorkspaceId,
            receipt.RunId,
            receipt.Revision,
            receipt.ExecutionGeneration,
            receipt.ActivationOrdinal,
            receipt.VisitOrdinal,
            receipt.NodeId,
            receipt.Attempt,
            receipt.CycleId,
            receipt.CycleIteration,
            receipt.ControlOutcome,
            receipt.SelectedControlEdgeIds,
            receipt.SkippedControlEdgeIds,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Completed,
            receipt.OutcomeArtifactHash,
            string.Empty));

        Assert.Equal(receipt.EvidenceHash, durable.EvidenceHash);
        Assert.NotEqual(receipt.EvidenceHash, RehashEvidence(receipt with { OutcomeArtifactHash = Hash('e') }).EvidenceHash);
        Assert.False(GovernedLoopSequentialNodeEvidenceHash.Matches(receipt with { OutcomeArtifactHash = Hash('d') }));
    }

    [Fact]
    public async Task Guard_issues_anchor_only_when_every_exact_coordinate_composes()
    {
        var context = await ContextAsync();

        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.Ready, context.AnchorResult.Status);
        var anchor = Assert.IsType<GovernedLoopSequentialRunAnchor>(context.AnchorResult.Anchor);
        Assert.Same(context.AdapterBinding, anchor.AdapterBinding);
        Assert.Same(context.Invocation, anchor.InvocationSnapshot);
        Assert.Equal(context.Receipt.Evidence.Binding, anchor.AdapterBinding.ExecutionBinding);
        Assert.Equal(context.Artifact.ArtifactHash, anchor.AdapterBinding.GraphArtifactHash);
        Assert.Equal(context.Artifact.LayoutHash, anchor.AdapterBinding.GraphLayoutHash);
    }

    [Fact]
    public async Task Guard_rejects_each_adapter_coordinate_substitution_against_the_retained_receipt()
    {
        var context = await ContextAsync();
        var original = context.AdapterBinding;
        var substitutions = new (GovernedLoopSequentialRunAnchorStatus Status, GovernedLoopSequentialAdapterBinding Binding)[]
        {
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, original with { WorkspaceId = WorkspaceId('b') }),
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, original with { AdmissionOperationId = "admit-other" }),
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, original with { AdmissionRequestHash = Hash('b') }),
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, original with { AdmissionReceiptHash = Hash('c') }),
            (GovernedLoopSequentialRunAnchorStatus.InvocationMismatch, Rehash(original with { InvocationPayloadHash = Hash('d') })),
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, original with { GraphArtifactHash = Hash('e') }),
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, original with { GraphLayoutHash = Hash('f') }),
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, Rebind(original, GovernedLoopExecutionBinding.Create(1, "run-other", original.ExecutionBinding.Revision, 1))),
            (GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, Rebind(original, GovernedLoopExecutionBinding.Create(1, original.ExecutionBinding.RunId, original.ExecutionBinding.Revision, 2))),
        };

        foreach (var substitution in substitutions)
        {
            var result = GovernedLoopSequentialRunAnchorGuard.Create(
                substitution.Binding,
                context.Request,
                context.Receipt,
                context.Invocation,
                context.Artifact);

            Assert.Equal(substitution.Status, result.Status);
            Assert.Null(result.Anchor);
        }
    }

    [Fact]
    public async Task Guard_rejects_invalid_proofs_before_comparing_coordinates()
    {
        var context = await ContextAsync();

        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding, GovernedLoopSequentialRunAnchorGuard.Create(context.AdapterBinding with { ContentHash = Hash('f') }, context.Request, context.Receipt, context.Invocation, context.Artifact).Status);
        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.InvalidAdmissionRequest, GovernedLoopSequentialRunAnchorGuard.Create(context.AdapterBinding, context.Request with { RequestHash = Hash('f') }, context.Receipt, context.Invocation, context.Artifact).Status);
        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.InvalidAdmissionReceipt, GovernedLoopSequentialRunAnchorGuard.Create(context.AdapterBinding, context.Request, context.Receipt with { ContentHash = Hash('f') }, context.Invocation, context.Artifact).Status);
        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.InvalidInvocationSnapshot, GovernedLoopSequentialRunAnchorGuard.Create(context.AdapterBinding, context.Request, context.Receipt, context.Invocation with { ContentHash = Hash('f') }, context.Artifact).Status);
        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.InvalidGraphArtifact, GovernedLoopSequentialRunAnchorGuard.Create(context.AdapterBinding, context.Request, context.Receipt, context.Invocation, null).Status);
    }

    [Fact]
    public async Task Guard_rejects_a_self_consistent_invocation_captured_after_admission_evaluation()
    {
        var context = await ContextAsync(GovernedLoopSequentialApplicationTestFixture.Now.AddTicks(1));

        Assert.True(GovernedLoopSequentialContractValidator.Validate(context.Invocation).IsValid);
        Assert.True(GovernedLoopAdmissionValidator.Validate(context.Receipt).IsValid);
        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.AdmissionCausalityMismatch, context.AnchorResult.Status);
        Assert.Null(context.AnchorResult.Anchor);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Guard_rejects_manual_and_schedule_entry_origin_confusion(
        bool scheduleEntry,
        bool scheduleOrigin)
    {
        var context = await ContextAsync(scheduleTrigger: scheduleEntry, scheduleOrigin: scheduleOrigin);

        Assert.True(GovernedLoopSequentialContractValidator.Validate(context.Invocation).IsValid);
        Assert.True(GovernedLoopAdmissionValidator.Validate(context.Receipt).IsValid);
        Assert.Equal(GovernedLoopSequentialRunAnchorStatus.InvocationMismatch, context.AnchorResult.Status);
        Assert.Null(context.AnchorResult.Anchor);
    }

    [Fact]
    public void Registry_rejects_null_duplicate_oversized_and_unsupported_handler_sets()
    {
        var trigger = new TestHandler(GovernedLoopSequentialNodeDescriptors.ManualTrigger);
        var inference = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var exit = new TestHandler(GovernedLoopSequentialNodeDescriptors.SuccessExit);
        var unsupported = new TestHandler(new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 2));

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSequentialNodeHandlerRegistry(null!));
        Assert.Throws<ArgumentException>(() => new GovernedLoopSequentialNodeHandlerRegistry([trigger, trigger]));
        Assert.Throws<ArgumentException>(() => new GovernedLoopSequentialNodeHandlerRegistry([unsupported]));
        Assert.Throws<ArgumentException>(() => new GovernedLoopSequentialNodeHandlerRegistry([trigger, inference, exit, trigger]));
    }

    [Fact]
    public void Dispatcher_requires_both_exact_registry_and_retained_evidence_ports()
    {
        var registry = new GovernedLoopSequentialNodeHandlerRegistry([]);
        var source = new TestEvidenceSource(null);

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSequentialNodeDispatcher(null!, source));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSequentialNodeDispatcher(registry, null!));
    }

    [Fact]
    public void Registry_resolves_only_exact_case_sensitive_descriptor_keys()
    {
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var registry = new GovernedLoopSequentialNodeHandlerRegistry([handler]);

        Assert.True(registry.TryResolve(GovernedLoopSequentialNodeDescriptors.ProviderInference, out var resolved));
        Assert.Same(handler, resolved);
        Assert.False(registry.TryResolve(null, out _));
        Assert.False(registry.TryResolve(new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "Provider-Inference", 1), out _));
        Assert.False(registry.TryResolve(new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 2), out _));
        Assert.False(registry.TryResolve(GovernedLoopSequentialNodeDescriptors.SuccessExit, out _));
    }

    [Theory]
    [InlineData(GovernedLoopSequentialNodeHandlerResultStatus.Completed, GovernedLoopSequentialNodeDispatchStatus.Completed)]
    [InlineData(GovernedLoopSequentialNodeHandlerResultStatus.Rejected, GovernedLoopSequentialNodeDispatchStatus.Rejected)]
    [InlineData(GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview, GovernedLoopSequentialNodeDispatchStatus.NeedsReview)]
    public async Task Dispatcher_invokes_exact_live_descriptor_once_and_preserves_evidence(
        GovernedLoopSequentialNodeHandlerResultStatus handlerStatus,
        GovernedLoopSequentialNodeDispatchStatus expectedStatus)
    {
        var context = await ContextAsync();
        var evidence = Evidence(context, context.Plan.Nodes[1], handlerStatus);
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference)
        {
            Result = new GovernedLoopSequentialNodeHandlerResult(handlerStatus, evidence.EvidenceHash),
        };
        var evidenceSource = new TestEvidenceSource(evidence);
        var dispatcher = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([handler]), evidenceSource);
        var request = DispatchRequest(context, context.Plan.Nodes[1]);

        var result = await dispatcher.DispatchAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(evidence.EvidenceHash, result.EvidenceHash);
        Assert.Equal(1, handler.CallCount);
        Assert.Same(request, handler.LastRequest);
        Assert.Equal(1, evidenceSource.CallCount);
        Assert.Equal(evidence.EvidenceHash, evidenceSource.LastEvidenceHash);
        Assert.Equal(CancellationToken.None, evidenceSource.LastCancellationToken);
    }

    [Fact]
    public async Task Dispatcher_rejects_absent_malformed_or_causally_substituted_retained_evidence()
    {
        var context = await ContextAsync();
        var node = context.Plan.Nodes[1];
        var valid = Evidence(context, node, GovernedLoopSequentialNodeHandlerResultStatus.Completed);
        var otherRevision = GovernedLoopRevisionReference.Create(
            valid.Revision.SchemaVersion,
            valid.Revision.GraphId,
            "revision-other",
            valid.Revision.ExecutableHash);
        var substitutions = new GovernedLoopSequentialNodeEvidenceReceipt?[]
        {
            null,
            valid with { SchemaVersion = 2 },
            RehashEvidence(valid with { Kind = GovernedLoopSequentialNodeEvidenceKind.DefinitiveRejection }),
            RehashEvidence(valid with { WorkspaceId = WorkspaceId('b') }),
            RehashEvidence(valid with { RunId = "run-other" }),
            RehashEvidence(valid with { Revision = otherRevision }),
            RehashEvidence(valid with { ExecutionGeneration = 2 }),
            RehashEvidence(valid with { NodeId = "node-other" }),
            RehashEvidence(valid with { Attempt = 2 }),
            RehashEvidence(valid with { Disposition = GovernedLoopSequentialNodeHandlerResultStatus.Rejected }),
            valid with { EvidenceHash = Hash('f') },
        };
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference)
        {
            Result = new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Completed, valid.EvidenceHash),
        };

        foreach (var substitution in substitutions)
        {
            var source = new TestEvidenceSource(substitution);
            var dispatcher = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([handler]), source);

            var result = await dispatcher.DispatchAsync(DispatchRequest(context, node));

            Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidHandlerResult, result.Status);
            Assert.Null(result.EvidenceHash);
            Assert.Equal(1, source.CallCount);
        }

        Assert.Equal(substitutions.Length, handler.CallCount);
    }

    [Fact]
    public async Task Dispatcher_rejects_plan_node_and_handler_substitution_before_invocation()
    {
        var context = await ContextAsync();
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var registry = new GovernedLoopSequentialNodeHandlerRegistry([handler]);
        var dispatcher = new GovernedLoopSequentialNodeDispatcher(registry, new TestEvidenceSource(null));
        var secondPlan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(context.Artifact).Plan);
        var substitutedNode = DispatchRequest(context, secondPlan.Nodes[1]);

        var invalidNode = await dispatcher.DispatchAsync(substitutedNode);
        handler.Descriptor = GovernedLoopSequentialNodeDescriptors.SuccessExit;
        var changedHandler = await dispatcher.DispatchAsync(DispatchRequest(context, context.Plan.Nodes[1]));

        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, invalidNode.Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.UnsupportedDescriptor, changedHandler.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Dispatcher_rejects_missing_handler_invalid_request_and_invalid_handler_result()
    {
        var context = await ContextAsync();
        var node = context.Plan.Nodes[1];
        var source = new TestEvidenceSource(null);
        var missing = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([]), source);
        var invalidHandler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference)
        {
            Result = new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, "bad"),
        };
        var invalidResultDispatcher = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([invalidHandler]), source);

        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.UnsupportedDescriptor, (await missing.DispatchAsync(DispatchRequest(context, node))).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, (await invalidResultDispatcher.DispatchAsync(DispatchRequest(context, node) with { Attempt = 0 })).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, (await invalidResultDispatcher.DispatchAsync(DispatchRequest(context, node) with { SchemaVersion = 2 })).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, (await invalidResultDispatcher.DispatchAsync(null)).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidHandlerResult, (await invalidResultDispatcher.DispatchAsync(DispatchRequest(context, node))).Status);
        Assert.Equal(1, invalidHandler.CallCount);
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task Dispatcher_honors_cancellation_before_irreversible_handler_dispatch()
    {
        var context = await ContextAsync();
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var dispatcher = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([handler]), new TestEvidenceSource(null));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(DispatchRequest(context, context.Plan.Nodes[1]), cancellation.Token));
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<TestContext> ContextAsync(
        DateTimeOffset? invocationCapturedAtUtc = null,
        bool scheduleTrigger = false,
        bool scheduleOrigin = false)
    {
        var seedHarness = GovernedLoopAdmissionTestHarness.Create();
        var seedOutcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seedHarness.CreateService().AdmitAsync(seedHarness.Request)).Outcome);
        var seedReceipt = Assert.IsType<GovernedLoopAdmissionReceipt>(seedOutcome.Receipt);
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(
            owningRole: seedReceipt.Intent.Role,
            scheduleTrigger: scheduleTrigger);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-sequential", Hash('7'));
        var contextCapturedAtUtc = invocationCapturedAtUtc ?? GovernedLoopSequentialApplicationTestFixture.Now;
        var invocationContext = CustomLoopContextSnapshot.CreateEmpty(contextCapturedAtUtc);
        const string Prompt = "Execute the exact admitted request.";
        var triggerOrigin = scheduleOrigin
            ? ScheduleOrigin(publication, seedReceipt, artifact, Prompt, contextCapturedAtUtc)
            : null;
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            Prompt,
            new CustomLoopModelSnapshot("provider", "model"),
            scheduleOrigin
                ? null
                : new CustomLoopConversationReference("conversation-1", "version-1", GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(-1)),
            contextCapturedAtUtc,
            invocationContext.SourceManifest,
            string.Empty)
        {
            TriggerOrigin = triggerOrigin,
        });
        var request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            1,
            "admit-sequential",
            invocation.ContentHash,
            string.Empty,
            publication,
            seedReceipt.Intent.AuthorityGrant,
            seedReceipt.Intent.ActorId,
            "web"));
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
        var execution = GovernedLoopExecutionBinding.Create(1, "run-sequential", publication.Revision, 1);
        var grantBoundary = new AuthorityGrantBoundary(
            GovernedLoopSequentialApplicationTestFixture.Now.AddHours(-1),
            GovernedLoopSequentialApplicationTestFixture.Now.AddHours(1),
            seedReceipt.Evidence.GrantBoundary.CompletionConstraint);
        var evidence = GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(
            intent,
            execution,
            seedReceipt.Evidence.GrantProfile,
            grantBoundary,
            seedReceipt.Evidence.GrantDependencyEvidenceHash,
            seedReceipt.Evidence.EffectiveAuthority,
            seedReceipt.Evidence.CapabilityAdmission,
            GovernedLoopSequentialApplicationTestFixture.Now);
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            1,
            intent,
            evidence,
            GovernedLoopSequentialApplicationTestFixture.Now,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            1,
            intent.WorkspaceId,
            execution,
            request.OperationId,
            receipt,
            receipt.ContentHash,
            request.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(adapterBinding, request, receipt, invocation, artifact);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        return new TestContext(artifact, request, receipt, invocation, adapterBinding, anchorResult, plan);
    }

    internal static GovernedLoopSequentialTriggerOrigin ScheduleOrigin(
        GovernedLoopRevisionPublicationPin publication,
        GovernedLoopAdmissionReceipt seedReceipt,
        GovernedLoopGraphRevisionArtifact artifact,
        string prompt,
        DateTimeOffset capturedAtUtc)
    {
        Assert.True(ScheduleId.TryParse("sequential-schedule", out var scheduleId));
        var scheduledAtUtc = capturedAtUtc.AddMinutes(-3);
        var timeZone = new ScheduleTimeZoneReference("Etc/UTC", Hash('5'));
        var occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            1,
            DateTime.SpecifyKind(scheduledAtUtc.UtcDateTime, DateTimeKind.Unspecified),
            scheduledAtUtc,
            timeZone);

        Assert.True(CapabilityId.TryParse(GovernedLoopSequentialApplicationTestFixture.ScheduleTriggerCapabilityId, out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + Hash('4'), out var descriptorHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        var adapter = new TriggerAdapterReference(
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!),
            new CapabilityImplementationIdentity(providerId!, "triggers/time"));

        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, seedReceipt.Intent.AuthorityGrant, out var loop, out _));
        var workspaceId = seedReceipt.Intent.WorkspaceId["workspace-sha256:".Length..];
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(
            seedReceipt.Intent.ActorId,
            seedReceipt.Intent.Surface,
            workspaceId,
            artifact.Graph.OwningRole.Identity.RoleId,
            out var actorContext,
            out _));
        var profile = seedReceipt.Evidence.GrantProfile.Reference;
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            AuthorityBoundaryReceipt.CurrentSchemaVersion,
            AuthorityBoundaryDecision.Direct,
            [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)],
            [profile],
            capturedAtUtc.AddMinutes(-1),
            out var boundaryReceipt,
            out _));
        var authority = new TriggerAuthorityEvidence(profile, boundaryReceipt!);
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(Encoding.UTF8.GetBytes(prompt), out var payload, out _));
        var definition = new ScheduleDefinition(
            ScheduleDefinition.CurrentSchemaVersion,
            scheduleId!,
            1,
            loop!,
            adapter,
            seedReceipt.Intent.ActorId,
            seedReceipt.Intent.Surface,
            workspaceId,
            artifact.Graph.OwningRole.Identity.RoleId,
            profile,
            new SchedulePayloadReference("payload/sequential-schedule", payload!.ContentHash),
            SchedulePriority.Normal,
            new ScheduleRecurrenceRule(ScheduleRecurrenceKind.Once, occurrence.ScheduledLocal, null),
            timeZone,
            new ScheduleDaylightSavingPolicy(ScheduleInvalidLocalTimePolicy.ShiftForward, ScheduleAmbiguousLocalTimePolicy.EarlierUtc),
            new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.FireLatestOnce, 0),
            ScheduleOverlapPolicy.DeferOne,
            true);
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        Assert.True(ScheduleIdentityDerivation.TryDerive(scheduleId, 1, definitionHash!, occurrence, out var identity, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, identity!.DeliveryId, out var redelivery, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(
            scheduledAtUtc,
            capturedAtUtc.AddMinutes(-1),
            scheduledAtUtc,
            null,
            null,
            null,
            null,
            out var temporal,
            out _));
        var directive = new ScheduleExecutionDirective(
            ScheduleExecutionDirective.CurrentSchemaVersion,
            definition.ScheduleId,
            definition.Revision,
            definitionHash!,
            occurrence,
            identity,
            definition.Target,
            definition.Overlap,
            Hash('5'));
        Assert.True(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
            TriggerDeliveryEnvelope.CurrentSchemaVersion,
            identity.DeliveryId,
            identity.DeduplicationId,
            adapter,
            loop,
            actorContext,
            authority,
            temporal,
            payload,
            redelivery,
            directive,
            false,
            null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out var envelope,
            out _));
        Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _));
        var evidence = new ScheduleDeliveryProvenanceEvidence(
            ScheduleDeliveryProvenanceEvidence.CurrentSchemaVersion,
            definition,
            definitionHash!,
            occurrence,
            identity,
            new ScheduleDeliveryResultEvidence(
                ScheduleDeliveryResultEvidence.CurrentSchemaVersion,
                ScheduleDeliveryResultKind.Queued,
                "queue-enqueued",
                envelopeHash!,
                capturedAtUtc));
        Assert.True(GovernedLoopSequentialTriggerOriginFactory.TryCreateSchedule(envelope, evidence, out var origin));
        return origin!;
    }

    private static GovernedLoopSequentialNodeDispatchRequest DispatchRequest(TestContext context, GovernedLoopSequentialPlanNode node)
        => new(
            1,
            Assert.IsType<GovernedLoopSequentialRunAnchor>(context.AnchorResult.Anchor),
            context.Plan,
            node,
            RunningActivation(context, context.Plan.Nodes[node.Ordinal]),
            1);

    private static GovernedLoopNodeExecutionEvidence RunningActivation(
        TestContext context,
        GovernedLoopSequentialPlanNode node)
    {
        var initialized = GovernedLoopSequentialFrontierMachine.Initialize(
            context.AdapterBinding,
            context.Plan,
            "trigger-attempt",
            "trigger-outcome",
            Hash('0'),
            GovernedLoopSequentialApplicationTestFixture.Now);
        var ready = Assert.IsType<GovernedLoopFrontierPosture>(initialized.Frontier);
        var selection = GovernedLoopSequentialFrontierMachine.Select(ready, context.AdapterBinding, context.Plan);
        Assert.Same(node, selection.Node);
        var started = GovernedLoopSequentialFrontierMachine.Start(
            ready,
            context.AdapterBinding,
            context.Plan,
            node,
            Assert.IsType<GovernedLoopNodeExecutionEvidence>(selection.Activation),
            1,
            $"attempt-{node.NodeId}",
            GovernedLoopSequentialApplicationTestFixture.Now.AddSeconds(1));
        var running = Assert.IsType<GovernedLoopFrontierPosture>(started.Frontier);
        return Assert.IsType<GovernedLoopNodeExecutionEvidence>(
            GovernedLoopSequentialFrontierMachine.Select(running, context.AdapterBinding, context.Plan).Activation);
    }

    private static GovernedLoopSequentialAdapterBinding Rehash(GovernedLoopSequentialAdapterBinding binding)
        => GovernedLoopSequentialContractHash.Apply(binding with { ContentHash = string.Empty });

    private static GovernedLoopSequentialAdapterBinding Rebind(
        GovernedLoopSequentialAdapterBinding source,
        GovernedLoopExecutionBinding execution)
        => new GovernedLoopSequentialAdapterBinding(
            source.SchemaVersion,
            source.WorkspaceId,
            execution,
            source.AdmissionOperationId,
            source.AdmissionReceipt,
            source.AdmissionReceiptHash,
            source.AdmissionRequestHash,
            source.InvocationPayloadHash,
            source.GraphArtifactHash,
            source.GraphLayoutHash,
            source.CommandActionCapabilityIds,
            source.ContentHash);

    private static GovernedLoopSequentialNodeEvidenceReceipt Evidence(
        TestContext context,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopSequentialNodeHandlerResultStatus disposition)
    {
        var binding = context.AdapterBinding;
        var activation = RunningActivation(context, node);
        var controlOutcome = disposition switch
        {
            GovernedLoopSequentialNodeHandlerResultStatus.Completed => GovernedLoopControlCondition.Success,
            GovernedLoopSequentialNodeHandlerResultStatus.Rejected => GovernedLoopControlCondition.Failure,
            _ => (GovernedLoopControlCondition?)null,
        };
        var selected = controlOutcome is null
            ? []
            : node.OutgoingControlEdgeIds.Where(edgeId => context.Plan.ControlEdges.Single(edge => edge.Id == edgeId).Condition == controlOutcome).ToArray();
        var skipped = controlOutcome is null
            ? []
            : node.OutgoingControlEdgeIds.Except(selected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return GovernedLoopSequentialNodeEvidenceHash.Apply(new GovernedLoopSequentialNodeEvidenceReceipt(
            1,
            EvidenceKind(disposition),
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            node.NodeId,
            1,
            activation.CycleId,
            activation.CycleIteration,
            controlOutcome,
            selected,
            skipped,
            disposition,
            Hash('f'),
            disposition == GovernedLoopSequentialNodeHandlerResultStatus.Completed ? null : "failure-evidence",
            disposition == GovernedLoopSequentialNodeHandlerResultStatus.Completed ? null : Hash('e'),
            string.Empty));
    }

    private static GovernedLoopSequentialNodeEvidenceReceipt RehashEvidence(GovernedLoopSequentialNodeEvidenceReceipt evidence)
        => GovernedLoopSequentialNodeEvidenceHash.Apply(evidence with { EvidenceHash = string.Empty });

    private static GovernedLoopSequentialNodeEvidenceKind EvidenceKind(GovernedLoopSequentialNodeHandlerResultStatus disposition)
        => disposition switch
        {
            GovernedLoopSequentialNodeHandlerResultStatus.Completed => GovernedLoopSequentialNodeEvidenceKind.CompletedOutcome,
            GovernedLoopSequentialNodeHandlerResultStatus.Rejected => GovernedLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview => GovernedLoopSequentialNodeEvidenceKind.AmbiguityAttention,
            _ => GovernedLoopSequentialNodeEvidenceKind.Unknown,
        };

    private static string WorkspaceId(char value) => "workspace-sha256:" + Hash(value);

    private static string Hash(char value) => GovernedLoopSequentialApplicationTestFixture.Hash(value);

    private sealed record TestContext(
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopAdmissionRequest Request,
        GovernedLoopAdmissionReceipt Receipt,
        GovernedLoopSequentialInvocationSnapshot Invocation,
        GovernedLoopSequentialAdapterBinding AdapterBinding,
        GovernedLoopSequentialRunAnchorResult AnchorResult,
        GovernedLoopSequentialPlan Plan);

    private sealed class TestHandler(GovernedLoopNodeDescriptor descriptor) : IGovernedLoopSequentialNodeHandler
    {
        public GovernedLoopNodeDescriptor Descriptor { get; set; } = descriptor;

        public GovernedLoopSequentialNodeHandlerResult Result { get; set; } = new(GovernedLoopSequentialNodeHandlerResultStatus.Completed, Hash('8'));

        public int CallCount { get; private set; }

        public GovernedLoopSequentialNodeDispatchRequest? LastRequest { get; private set; }

        public Task<GovernedLoopSequentialNodeHandlerResult> DispatchAsync(
            GovernedLoopSequentialNodeDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class TestEvidenceSource(GovernedLoopSequentialNodeEvidenceReceipt? evidence) : IGovernedLoopSequentialNodeEvidenceSource
    {
        public int CallCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public string? LastEvidenceHash { get; private set; }

        public Task<GovernedLoopSequentialNodeEvidenceReceipt?> ResolveAsync(
            string evidenceHash,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastEvidenceHash = evidenceHash;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(evidence);
        }
    }
}
