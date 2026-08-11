using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Admission;
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

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialRunAnchorAndDispatcherTests
{
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
    public async Task Guard_rejects_each_adapter_coordinate_substitution_with_a_valid_recomputed_binding_hash()
    {
        var context = await ContextAsync();
        var original = context.AdapterBinding;
        var substitutions = new (GovernedLoopSequentialRunAnchorStatus Status, GovernedLoopSequentialAdapterBinding Binding)[]
        {
            (GovernedLoopSequentialRunAnchorStatus.WorkspaceMismatch, Rehash(original with { WorkspaceId = WorkspaceId('b') })),
            (GovernedLoopSequentialRunAnchorStatus.OperationMismatch, Rehash(original with { AdmissionOperationId = "admit-other" })),
            (GovernedLoopSequentialRunAnchorStatus.RequestMismatch, Rehash(original with { AdmissionRequestHash = Hash('b') })),
            (GovernedLoopSequentialRunAnchorStatus.ReceiptMismatch, Rehash(original with { AdmissionReceiptHash = Hash('c') })),
            (GovernedLoopSequentialRunAnchorStatus.InvocationMismatch, Rehash(original with { InvocationPayloadHash = Hash('d') })),
            (GovernedLoopSequentialRunAnchorStatus.GraphArtifactMismatch, Rehash(original with { GraphArtifactHash = Hash('e') })),
            (GovernedLoopSequentialRunAnchorStatus.GraphLayoutMismatch, Rehash(original with { GraphLayoutHash = Hash('f') })),
            (GovernedLoopSequentialRunAnchorStatus.RunBindingMismatch, Rebind(original, GovernedLoopExecutionBinding.Create(1, "run-other", original.ExecutionBinding.Revision, 1))),
            (GovernedLoopSequentialRunAnchorStatus.RunBindingMismatch, Rebind(original, GovernedLoopExecutionBinding.Create(1, original.ExecutionBinding.RunId, original.ExecutionBinding.Revision, 2))),
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
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference)
        {
            Result = new GovernedLoopSequentialNodeHandlerResult(handlerStatus, Hash('9')),
        };
        var dispatcher = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([handler]));
        var request = DispatchRequest(context, context.Plan.Nodes[1]);

        var result = await dispatcher.DispatchAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(Hash('9'), result.EvidenceHash);
        Assert.Equal(1, handler.CallCount);
        Assert.Same(request, handler.LastRequest);
    }

    [Fact]
    public async Task Dispatcher_rejects_plan_node_and_handler_substitution_before_invocation()
    {
        var context = await ContextAsync();
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var registry = new GovernedLoopSequentialNodeHandlerRegistry([handler]);
        var dispatcher = new GovernedLoopSequentialNodeDispatcher(registry);
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
        var missing = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([]));
        var invalidHandler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference)
        {
            Result = new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, "bad"),
        };
        var invalidResultDispatcher = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([invalidHandler]));

        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.UnsupportedDescriptor, (await missing.DispatchAsync(DispatchRequest(context, node))).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, (await invalidResultDispatcher.DispatchAsync(DispatchRequest(context, node) with { Attempt = 0 })).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, (await invalidResultDispatcher.DispatchAsync(DispatchRequest(context, node) with { SchemaVersion = 2 })).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidRequest, (await invalidResultDispatcher.DispatchAsync(null)).Status);
        Assert.Equal(GovernedLoopSequentialNodeDispatchStatus.InvalidHandlerResult, (await invalidResultDispatcher.DispatchAsync(DispatchRequest(context, node))).Status);
        Assert.Equal(1, invalidHandler.CallCount);
    }

    [Fact]
    public async Task Dispatcher_honors_cancellation_before_irreversible_handler_dispatch()
    {
        var context = await ContextAsync();
        var handler = new TestHandler(GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var dispatcher = new GovernedLoopSequentialNodeDispatcher(new GovernedLoopSequentialNodeHandlerRegistry([handler]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(DispatchRequest(context, context.Plan.Nodes[1]), cancellation.Token));
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<TestContext> ContextAsync()
    {
        var seedHarness = GovernedLoopAdmissionTestHarness.Create();
        var seedOutcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seedHarness.CreateService().AdmitAsync(seedHarness.Request)).Outcome);
        var seedReceipt = Assert.IsType<GovernedLoopAdmissionReceipt>(seedOutcome.Receipt);
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(owningRole: seedReceipt.Intent.Role);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-sequential", Hash('7'));
        var invocationContext = CustomLoopContextSnapshot.CreateEmpty(GovernedLoopSequentialApplicationTestFixture.Now);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            "Execute the exact admitted request.",
            new CustomLoopModelSnapshot("provider", "model"),
            new CustomLoopConversationReference("conversation-1", "version-1", GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(-1)),
            GovernedLoopSequentialApplicationTestFixture.Now,
            invocationContext.SourceManifest,
            string.Empty));
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
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            1,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            seedReceipt.Evidence.EffectiveAuthority,
            seedReceipt.Evidence.CapabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, seedReceipt.Evidence.EffectiveAuthority, seedReceipt.Evidence.CapabilityAdmission),
            GovernedLoopSequentialApplicationTestFixture.Now,
            string.Empty));
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
            receipt.ContentHash,
            request.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));
        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(adapterBinding, request, receipt, invocation, artifact);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        return new TestContext(artifact, request, receipt, invocation, adapterBinding, anchorResult, plan);
    }

    private static GovernedLoopSequentialNodeDispatchRequest DispatchRequest(TestContext context, GovernedLoopSequentialPlanNode node)
        => new(
            1,
            Assert.IsType<GovernedLoopSequentialRunAnchor>(context.AnchorResult.Anchor),
            context.Plan,
            node,
            1);

    private static GovernedLoopSequentialAdapterBinding Rehash(GovernedLoopSequentialAdapterBinding binding)
        => GovernedLoopSequentialContractHash.Apply(binding with { ContentHash = string.Empty });

    private static GovernedLoopSequentialAdapterBinding Rebind(
        GovernedLoopSequentialAdapterBinding source,
        GovernedLoopExecutionBinding execution)
        => GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            source.SchemaVersion,
            source.WorkspaceId,
            execution,
            source.AdmissionOperationId,
            source.AdmissionReceiptHash,
            source.AdmissionRequestHash,
            source.InvocationPayloadHash,
            source.GraphArtifactHash,
            source.GraphLayoutHash,
            string.Empty));

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
}
