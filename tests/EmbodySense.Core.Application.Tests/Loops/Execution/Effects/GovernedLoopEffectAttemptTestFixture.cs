using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

internal sealed class GovernedLoopEffectAttemptTestFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    private GovernedLoopEffectAttemptTestFixture(
        GovernedLoopEffectAttemptRequest request,
        CapabilityDescriptor capability,
        GovernedActuatorOperationDescriptor descriptor)
    {
        Request = request;
        Capability = capability;
        Descriptor = descriptor;
    }

    internal GovernedLoopEffectAttemptRequest Request { get; }
    internal CapabilityDescriptor Capability { get; }
    internal GovernedActuatorOperationDescriptor Descriptor { get; }

    internal static GovernedLoopEffectAttemptTestFixture Create(
        CapabilitySideEffectClass sideEffect = CapabilitySideEffectClass.LocalReversible,
        bool unattended = true,
        bool requiresPrecondition = true,
        bool requiresBefore = true)
    {
        Assert.True(CapabilityId.TryParse("org.example/effects/probe", out var capabilityId, out var capabilityError), capabilityError?.Message);
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        Assert.True(CapabilityPlatform.TryParse("any/any", out var platform, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"required\":[\"target\"],\"properties\":{{\"target\":{{\"type\":\"string\",\"maxLength\":64}}}},\"additionalProperties\":false}}", out var schema, out _));
        var implementation = new CapabilityImplementationIdentity(provider!, "effects/probe");
        var capability = new CapabilityDescriptor(
            1,
            capabilityId!,
            CapabilityKind.Actuator,
            version!,
            implementation,
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://example.test/effects/probe", "revision-1", null),
            new CapabilityCompatibility(range!, [platform!]),
            "Deterministic governed effect probe.",
            schema!,
            schema!,
            new CapabilityResourceLimits(1_000, 4_096, 4_096, 1),
            sideEffect,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(capability, out var identity, out _));
        var descriptor = GovernedActuatorOperationContract.Create(
            1,
            identity!,
            implementation,
            "probe/observe",
            "Deterministic effect probe.",
            GovernedActuatorTargetSemantics.ExactOpaqueFingerprint,
            GovernedActuatorIdempotencyPosture.StableOperationIdentity,
            requiresPrecondition,
            GovernedActuatorApprovalPosture.AuthorityOnly,
            unattended,
            GovernedActuatorCancellationPosture.BeforeBoundaryOnly,
            GovernedActuatorAmbiguityPosture.ReconciliationRequired,
            requiresBefore,
            requiresAfterEvidence: true,
            requiresOutcomeEvidence: true);

        var role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("effect-role", 1), Hash('a'));
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition("trigger", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition("action", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Action, "probe-action", 1), [], GovernedLoopAuthorityCeiling.Create([capabilityId!.Value]), new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition("exit", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1), [new GovernedLoopPortDefinition("published", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
        };
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            "effect-loop",
            "revision-1",
            "Execute one governed test effect.",
            role,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([capabilityId.Value]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-action", "trigger", "action", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("action-exit", "action", "exit", GovernedLoopControlCondition.Success),
            ],
            [],
            new GovernedLoopOutputContract("Return completion.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published", true)]),
            new GovernedLoopDisplayMetadata("Effect loop", "Effect loop.", nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-effect-loop", "user-owner", Now.AddMinutes(-10));
        var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-effect-loop", Hash('7'));
        var profile = AuthorityGrantApplicationTestFixture.Profile();
        var profilePin = new AuthorityGrantProfilePin(
            new AuthorityProfileReference(profile.ProfileId, profile.Revision),
            AuthorityGrantApplicationTestFixture.ProfileHash(profile));
        Assert.True(AuthorityGrantId.TryParse("grant-effect", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        var grantReference = new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + Hash('b'));
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actor, out _));
        var workspaceId = AuthorityGrantApplicationTestFixture.WorkspaceId;
        var intent = new GovernedLoopAdmissionIntent(1, workspaceId, "admit-effect", Hash('c'), publication, grantReference, role, actor!, "test", artifact.ArtifactHash, artifact.LayoutHash);
        var manifest = Manifest(capabilityId!);
        Assert.True(CapabilityDependencyManifestHash.TryCompute(manifest, out var manifestHash, out _));
        var pin = new CapabilityAdmissionPin(identity!, CapabilityKind.Actuator, implementation, capability.Provenance, new CapabilityDependencyArtifactMetadata(null, null), capability.Purpose);
        var snapshot = new CapabilityAdmissionSnapshot(1, workspaceId, manifest, manifestHash!.Value, [pin], [new CapabilityAdmissionEvidence(manifest.SubjectId, capabilityId!, manifest.Required[0].CompatibleVersionRange, false, "Selected", identity, "Selected exact probe.")], Now.AddMinutes(-5));
        var execution = GovernedLoopExecutionBinding.Create(1, "run-effect", artifact.RevisionArtifact.Revision, 1);
        var effective = RequiredAuthority(identity!, sideEffect);
        var boundary = new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var modelRouting = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(intent, execution, profilePin, boundary, Hash('d'), effective, snapshot, Now.AddMinutes(-5));
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(1, GovernedLoopAdmissionContractHash.ComputeIntentHash(intent), execution, profilePin, boundary, Hash('d'), effective, snapshot, modelRouting, GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effective, snapshot, modelRouting), Now.AddMinutes(-5), string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(1, intent, evidence, Now.AddMinutes(-5), string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        var request = new GovernedLoopEffectAttemptRequest(
            receipt,
            execution,
            artifact,
            "action",
            1,
            pin,
            descriptor.OperationId,
            "effect-1",
            "effect-operation-1",
            1,
            "{\"target\":\"alpha\"}",
            effective,
            "effect-correlation-1");
        return new GovernedLoopEffectAttemptTestFixture(request, capability, descriptor);
    }

    internal static AuthorityCeiling RequiredAuthority(CapabilityDescriptorIdentity identity, CapabilitySideEffectClass sideEffect)
        => new([identity], [], 1, sideEffect, false, sideEffect is CapabilitySideEffectClass.ExternalReversible or CapabilitySideEffectClass.Irreversible, sideEffect == CapabilitySideEffectClass.Irreversible);

    internal static GovernedLoopEffectAttempt Prepare(GovernedLoopEffectAttemptRequest request, GovernedActuatorOperationDescriptor descriptor, GovernedActuatorInputEvidence input)
        => GovernedLoopEffectAttemptContract.Prepare(request.ExecutionBinding, request.NodeId, request.NodeAttempt, request.CapabilityPin.DescriptorIdentity, request.CapabilityPin.Implementation, request.ActuatorOperationId, descriptor.ContentHash, request.EffectId, request.IdempotencyOperationId, request.EffectGeneration, input.Fingerprint, HashInput("target:alpha"), Hash('e'), request.AdmissionReceipt.ContentHash, "before-alpha", Now);

    internal static string Hash(char value) => new(value, 64);

    internal static string HashInput(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static CapabilityDependencyManifest Manifest(CapabilityId capabilityId)
    {
        Assert.True(CapabilityId.TryParse("org.example/effect-loop", out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        return new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.LoopPackage, subject!, [new CapabilityDependency(capabilityId, range!)], [], new CapabilityDependencyArtifactMetadata(null, null));
    }
}

internal sealed class InMemoryEffectAttemptStore : IGovernedLoopEffectAttemptStore
{
    internal GovernedLoopEffectAttempt? Current { get; set; }
    internal int ResumeCalls { get; private set; }
    internal int BeginCalls { get; private set; }
    internal int ExchangeCalls { get; private set; }
    internal int? FailExchangeCall { get; set; }
    internal GovernedLoopEffectAttemptStoreStatus ExchangeFailureStatus { get; set; } = GovernedLoopEffectAttemptStoreStatus.Unavailable;
    internal Func<GovernedLoopEffectAttemptStoreResult, GovernedLoopEffectAttemptStoreResult>? MutateResult { get; set; }

    public Task<GovernedLoopEffectAttemptStoreResult> ResumeAsync(string operationId, long effectGeneration, CancellationToken cancellationToken = default)
    {
        ResumeCalls++;
        if (Current is null)
        {
            return Task.FromResult(Apply(new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.NotFound)));
        }
        var lease = Terminal(Current.Payload.Phase) ? null : new TestEffectAttemptLease();
        return Task.FromResult(Apply(new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Replayed, Current, lease)));
    }

    public Task<GovernedLoopEffectAttemptStoreResult> BeginAsync(GovernedLoopEffectAttempt prepared, CancellationToken cancellationToken = default)
    {
        BeginCalls++;
        Current ??= prepared;
        return Task.FromResult(Apply(new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Created, Current, new TestEffectAttemptLease())));
    }

    public Task<GovernedLoopEffectAttemptStoreResult> CompareExchangeAsync(string expectedContentHash, GovernedLoopEffectAttempt replacement, IGovernedLoopEffectAttemptLease lease, CancellationToken cancellationToken = default)
    {
        ExchangeCalls++;
        if (ExchangeCalls == FailExchangeCall)
        {
            return Task.FromResult(Apply(new GovernedLoopEffectAttemptStoreResult(ExchangeFailureStatus, Current)));
        }
        if (Current is null || !string.Equals(Current.ContentHash, expectedContentHash, StringComparison.Ordinal))
        {
            return Task.FromResult(Apply(new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Conflict, Current)));
        }
        Current = replacement;
        return Task.FromResult(Apply(new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Created, replacement)));
    }

    private GovernedLoopEffectAttemptStoreResult Apply(GovernedLoopEffectAttemptStoreResult result) => MutateResult?.Invoke(result) ?? result;

    private static bool Terminal(GovernedLoopEffectPhase phase) => phase is GovernedLoopEffectPhase.DispatchNotStarted or GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.ReconciliationRequired or GovernedLoopEffectPhase.Reconciled;
}

internal sealed class TestEffectAttemptLease : IGovernedLoopEffectAttemptLease
{
    internal bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
