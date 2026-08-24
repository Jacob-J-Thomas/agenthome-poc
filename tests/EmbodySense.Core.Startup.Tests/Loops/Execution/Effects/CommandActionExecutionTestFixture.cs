using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

internal sealed class CommandActionExecutionTestFixture
{
    private const string WorkspaceId = "workspace-sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private CommandActionExecutionTestFixture(CommandActionRegistration registration, GovernedLoopCommandActionExecutionRequest request)
    {
        Registration = registration;
        Request = request;
    }

    internal CommandActionRegistration Registration { get; }
    internal GovernedLoopCommandActionExecutionRequest Request { get; }

    internal static CommandActionExecutionTestFixture Create()
    {
        var registration = GovernedCommandActionFactoryTests.Registration();
        var artifact = GovernedLoopSequentialApplicationTestFixture.CommandActionOnlyArtifact(registration, new Dictionary<string, string>());
        var execution = GovernedLoopExecutionBinding.Create(1, "run-command-action-1", artifact.RevisionArtifact.Revision, 1);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, execution.Revision, "publish-command-action", Hash('7'));
        Assert.True(AuthorityGrantId.TryParse("grant-command-action", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        Assert.True(AuthorityProfileId.TryParse("profile-command-action", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash('8'), out var profileHash, out _));
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actorId, out _));
        var invocation = GovernedLoopSequentialApplicationTestFixture.InvocationSnapshot(artifact);
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            "admit-command-action",
            invocation.ContentHash,
            string.Empty,
            publication,
            new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + Hash('a')),
            actorId!,
            "test"));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            WorkspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            publication,
            admissionRequest.AuthorityGrant,
            artifact.Graph.OwningRole,
            admissionRequest.ActorId,
            admissionRequest.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var capabilityAdmission = CapabilityAdmission(registration);
        var effectiveAuthority = RequiredAuthority(registration);
        var grantProfile = new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!);
        var grantBoundary = new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var evidence = GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(
            intent,
            execution,
            grantProfile,
            grantBoundary,
            Hash('4'),
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
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            WorkspaceId,
            execution,
            admissionRequest.OperationId,
            receipt,
            receipt.ContentHash,
            admissionRequest.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(adapterBinding, admissionRequest, receipt, invocation, artifact);
        var anchor = Assert.IsType<GovernedLoopSequentialRunAnchor>(anchorResult.Anchor);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(planResult.Plan);
        var node = Assert.Single(plan.Nodes, candidate => CommandActionNodeDescriptors.Matches(candidate.Descriptor, registration.Template));
        const int Attempt = 1;
        const string AttemptOperationId = "attempt-command-action-1";
        var activation = GovernedLoopNodeExecutionEvidence.CreateActivation(
            node.Ordinal,
            node.Ordinal,
            1,
            node.NodeId,
            node.Descriptor,
            node.IncomingControlEdgeIds,
            node.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Running,
            Attempt,
            AttemptOperationId);
        return new CommandActionExecutionTestFixture(registration, new GovernedLoopCommandActionExecutionRequest(
            new GovernedLoopSequentialNodeDispatchRequest(
                GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion,
                anchor,
                plan,
                node,
                activation,
                Attempt),
            artifact,
            AttemptOperationId));
    }

    private static CapabilityAdmissionSnapshot CapabilityAdmission(CommandActionRegistration registration)
    {
        Assert.True(CapabilityId.TryParse("org.example/command-action-loop", out var subjectId, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subjectId!,
            [new CapabilityDependency(registration.Template.Capability.Id, range!)],
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
        Assert.True(CapabilityDependencyManifestHash.TryCompute(manifest, out var manifestHash, out _));
        var descriptor = registration.Manifest.Descriptor;
        var pin = new CapabilityAdmissionPin(
            registration.Template.Capability,
            descriptor.Kind,
            registration.Template.Implementation,
            descriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            descriptor.Purpose);
        return new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            WorkspaceId,
            manifest,
            manifestHash!.Value,
            [pin],
            [new CapabilityAdmissionEvidence(subjectId!, registration.Template.Capability.Id, range!, false, "Selected", pin.DescriptorIdentity, "Selected exact command Action registration.")],
            Now);
    }

    private static AuthorityCeiling RequiredAuthority(CommandActionRegistration registration)
    {
        var capability = registration.Manifest.Descriptor;
        return new AuthorityCeiling(
            [registration.Template.Capability],
            capability.Requirements.DataClasses,
            1,
            capability.SideEffectClass,
            false,
            capability.SideEffectClass is CapabilitySideEffectClass.ExternalReversible or CapabilitySideEffectClass.Irreversible,
            capability.SideEffectClass == CapabilitySideEffectClass.Irreversible);
    }

    private static string Hash(char value) => new(value, 64);

    internal static readonly DateTimeOffset Now = new(2026, 8, 23, 23, 0, 0, TimeSpan.Zero);
}
