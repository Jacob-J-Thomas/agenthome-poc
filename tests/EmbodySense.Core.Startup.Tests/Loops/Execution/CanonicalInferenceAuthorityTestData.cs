using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal static class CanonicalInferenceAuthorityTestData
{
    internal const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    internal const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    internal const string ModelProfileCapabilityId = "org.embodysense/model-profile/codex";
    internal const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    internal static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    internal static CustomLoopInferenceAttemptRequest Request(
        bool allowTools = false,
        IReadOnlyList<CustomLoopToolAssignment>? assignments = null,
        int attempt = 1,
        string? attemptCorrelationId = null,
        string runId = "run-1",
        string loopId = "loop-1",
        string roleId = "role-1")
    {
        assignments ??= [];
        var artifact = Artifact(allowTools, loopId, roleId);
        var manifest = Manifest(allowTools);
        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(manifest, Now);
        var effectiveAuthority = new AuthorityCeiling(
            capabilityAdmission.Pins.Select(pin => pin.DescriptorIdentity).ToArray(),
            [],
            1,
            allowTools ? CapabilitySideEffectClass.LocalReversible : CapabilitySideEffectClass.ReadOnly,
            AllowsRecurrence: false,
            AllowsExternalPublication: false,
            AllowsIrreversibleAction: false);
        var execution = GovernedLoopExecutionBinding.Create(
            1,
            runId,
            artifact.RevisionArtifact.Revision,
            1);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            artifact.RevisionArtifact.Revision,
            "publish-executor-test",
            Hash('7'));
        var profilePin = new AuthorityGrantProfilePin(
            new AuthorityProfileReference(ProfileId(), ProfileRevision()),
            ProfileHash());
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            capabilityAdmission.WorkspaceScopeId,
            "admit-executor-test",
            Hash('1'),
            publication,
            new AuthorityGrantReference(GrantId(), GrantRevision(), "sha256:" + Hash('2')),
            artifact.Graph.OwningRole,
            ActorId(),
            "test",
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var evidence = EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.RoutingEvidenceForInference(
            intent,
            execution,
            profilePin,
            new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            Hash('3'),
            effectiveAuthority,
            capabilityAdmission,
            Now);
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            evidence,
            Now,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);

        return new CustomLoopInferenceAttemptRequest(
            execution.RunId,
            artifact.Graph.GraphId,
            artifact.Graph.OwningRole.Identity.RoleId,
            1,
            Hash('a'),
            1,
            "step-one",
            attempt,
            attemptCorrelationId ?? $"attempt-{attempt}",
            IsExit: false,
            AllowTools: allowTools,
            new CustomLoopModelSnapshot("openai", "pinned-model"),
            assignments,
            ToolRequestsUsedInRun: 0,
            LlmInferenceRequest.FromUserText("prompt"))
        {
            CapabilityAdmission = capabilityAdmission,
            AdmissionReceipt = receipt,
            ExecutionBinding = execution,
            GraphArtifact = artifact,
            PlanOrdinal = 1,
            ActivationOrdinal = 1,
            VisitOrdinal = 1,
            AttemptOperationId = $"attempt-operation-{attempt}"
        };
    }

    internal static GovernedLoopEffectAuthorityDecision Decision(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityDisposition disposition,
        GovernedLoopEffectAuthorityReason reason,
        bool includeCurrentProof)
    {
        var receipt = request.AdmissionReceipt;
        var proof = new GovernedLoopEffectAuthorityProof(
            GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
            receipt.Intent.AuthorityGrant,
            new AuthorityGrantBinding(receipt.Evidence.GrantProfile, receipt.Intent.Role, receipt.Intent.Publication),
            AuthorityGrantLifecycleStatus.Active,
            GovernedLoopEffectAuthorityGrantPosture.Active,
            receipt.Evidence.GrantBoundary,
            receipt.Evidence.EffectiveAuthority,
            receipt.Evidence.CapabilityAdmission.Pins,
            [],
            receipt.Evidence.GrantDependencyEvidenceHash);
        var decision = new GovernedLoopEffectAuthorityDecision(
            GovernedLoopEffectAuthorityDecision.CurrentSchemaVersion,
            request.ExecutionBinding.RunId,
            request.ExecutionBinding.ExecutionGeneration,
            request.NodeId,
            request.NodeAttempt,
            request.EffectOperationId,
            request.CorrelationId,
            request.BoundaryKind,
            receipt.ContentHash,
            proof,
            includeCurrentProof ? proof : null,
            request.RequiredAuthority,
            disposition == GovernedLoopEffectAuthorityDisposition.Direct
                ? request.RequiredAuthority
                : AuthorityCeilingIntersection.EmptyCeiling(),
            request.RequiredCapabilityPins,
            disposition,
            reason,
            Now,
            string.Empty);
        var canonical = GovernedLoopEffectAuthorityContractHash.Apply(decision);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(canonical).IsValid);
        return canonical;
    }

    internal static string ProviderOperationId(CustomLoopInferenceAttemptRequest request)
        => "provider-" + CustomLoopTraceContentHash.Compute(
            $"provider-transport-v1\n{request.RunId}\n{request.StepId}\n{request.Attempt}\n{request.AttemptCorrelationId}");

    private static GovernedLoopGraphRevisionArtifact Artifact(bool allowTools, string loopId, string roleId)
    {
        var role = new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(roleId, 1),
            Hash('b'));
        var graphCapabilities = allowTools
            ? new[] { ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId }
            : [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId];
        var nodeCapabilities = allowTools
            ? new[] { ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId }
            : [ModelInferenceCapabilityId, ModelProfileCapabilityId];
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [
                    Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                    Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
                ],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "step-one",
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                [
                    Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                    Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context),
                    Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                ],
                GovernedLoopAuthorityCeiling.Create(nodeCapabilities),
                new Dictionary<string, string> { ["instruction"] = "Answer safely." }),
            new GovernedLoopNodeDefinition(
                "exit",
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [
                    Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                    Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                ],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>())
        };
        var graph = GovernedLoopGraphDefinition.Create(
            GovernedLoopGraphDefinition.CurrentSchemaVersion,
            loopId,
            "revision-1",
            "Execute one exact canonical inference authority test.",
            role,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create(graphCapabilities),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-step-one", "trigger", "step-one", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("step-one-to-exit", "step-one", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("request-to-step-one", GovernedLoopBindingKind.Data, "trigger", "request", "step-one", "request"),
                new GovernedLoopBindingDefinition("context-to-step-one", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "step-one", "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "step-one", "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract(
                "Return the exact bounded result.",
                [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Canonical inference test",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
        var revision = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            null,
            null,
            "create-executor-test",
            "user-owner",
            Now);
        return GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            revision,
            graph);
    }

    private static GovernedLoopPortDefinition Port(
        string id,
        GovernedLoopPortDirection direction,
        GovernedLoopBindingKind kind)
        => new(id, direction, kind, "text", true);

    private static CapabilityDependencyManifest Manifest(bool allowTools)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/canonical-inference-test", out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        var ids = allowTools
            ? new[] { ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId }
            : [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId];
        return new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            ids.Select(id =>
            {
                Assert.True(CapabilityId.TryParse(id, out var capabilityId, out _));
                return new CapabilityDependency(capabilityId!, range!);
            }).ToArray(),
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
    }

    private static AuthorityGrantId GrantId()
    {
        Assert.True(AuthorityGrantId.TryParse("grant-executor-test", out var value, out _));
        return value!;
    }

    private static AuthorityGrantRevision GrantRevision()
    {
        Assert.True(AuthorityGrantRevision.TryParse("1", out var value, out _));
        return value!;
    }

    private static AuthorityProfileId ProfileId()
    {
        Assert.True(AuthorityProfileId.TryParse("profile-executor-test", out var value, out _));
        return value!;
    }

    private static AuthorityProfileRevision ProfileRevision()
    {
        Assert.True(AuthorityProfileRevision.TryParse("1", out var value, out _));
        return value!;
    }

    private static AuthorityProfileHash ProfileHash()
    {
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash('4'), out var value, out _));
        return value!;
    }

    private static AuthorityActorId ActorId()
    {
        Assert.True(AuthorityActorId.TryParse("user-owner", out var value, out _));
        return value!;
    }

    private static string Hash(char value) => new(value, 64);
}
