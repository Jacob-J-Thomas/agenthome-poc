using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.CommandActions;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal static class GovernedLoopEffectReconciliationCommandStartupTestFixture
{
    internal static async Task<(CommandActionRuntimeProvider RuntimeProvider, GovernedLoopEffectReconciliationCase Case, GovernedLoopEffectAttempt Attempt)> SeedAsync(
        TestWorkspace workspace,
        bool retainOutcome = true,
        CommandActionOutcomeKind outcomeKind = CommandActionOutcomeKind.Succeeded,
        bool mismatchRunNodeBinding = false,
        bool mismatchCommandParameters = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var registration = GovernedCommandActionFactoryTests.Registration();
        var artifact = GovernedLoopSequentialApplicationTestFixture.CommandActionOnlyArtifact(
            registration,
            mismatchCommandParameters ? new Dictionary<string, string> { ["unexpected"] = "value" } : new Dictionary<string, string>());
        await PersistGraphAsync(paths, workspace.ServerStatePath, artifact);

        var now = GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(2);
        var execution = GovernedLoopExecutionBinding.Create(1, "run-reconciliation-command", artifact.RevisionArtifact.Revision, 1);
        var invocation = GovernedLoopSequentialApplicationTestFixture.InvocationSnapshot(artifact, includeConversation: false);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-sequential", GovernedLoopSequentialApplicationTestFixture.Hash('7'));
        if (!EmbodySense.Core.Common.Authority.Grants.AuthorityGrantId.TryParse("grant-sequential", out var grantId, out _)
            || !EmbodySense.Core.Common.Authority.Grants.AuthorityGrantRevision.TryParse("1", out var grantRevision, out _)
            || !EmbodySense.Core.Common.Authority.AuthorityActorId.TryParse("user-owner", out var actorId, out _))
        {
            throw new InvalidOperationException("The reconciliation command fixture authority identity is invalid.");
        }
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            "admit-reconciliation-command",
            invocation.ContentHash,
            string.Empty,
            publication,
            new EmbodySense.Core.Common.Authority.Grants.Models.AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + GovernedLoopSequentialApplicationTestFixture.Hash('a')),
            actorId!,
            "test"));
        var receipt = CreateAdmissionReceipt(registration, artifact, execution, admissionRequest, workspaceId, now);
        var adapter = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            workspaceId,
            execution,
            admissionRequest.OperationId,
            receipt,
            receipt.ContentHash,
            admissionRequest.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [registration.Template.Capability.Id.Value],
            string.Empty));
        var plan = GovernedLoopSequentialPlanBuilder.Build(artifact).Plan
            ?? throw new InvalidOperationException("The reconciliation command graph did not produce a canonical plan.");
        using var runs = new CustomLoopRunStore(paths);
        var materialized = await new GovernedLoopSequentialRunMaterializer(
            runs,
            new HumanReviewRecoveryCanonicalAuditRecorder(),
            new GovernedLoopSequentialEventIdentityGenerator(),
            new HumanReviewRecoveryCanonicalTimeProvider(now)).MaterializeAsync(new GovernedLoopSequentialMaterializationRequest(
                GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
                admissionRequest,
                receipt,
                artifact,
                plan,
                invocation,
                adapter));
        var admitted = materialized.Run ?? throw new InvalidOperationException($"The reconciliation command run was not materialized: {materialized.Status}. {materialized.Detail}");
        var running = TransitionToRunning(admitted, now.AddMinutes(1));
        RequireUpdated(await runs.UpdateAsync(running, admitted.LifecycleVersion), "running lifecycle");
        var actionNode = plan.Nodes.Single(node => node.Descriptor.Kind == EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Action);
        var selection = GovernedLoopSequentialFrontierMachine.Select(running.Frontier, adapter, plan);
        var startedFrontier = GovernedLoopSequentialFrontierMachine.Start(
            running.Frontier,
            adapter,
            plan,
            actionNode,
            selection.Activation,
            1,
            "attempt-reconciliation-command",
            now.AddMinutes(2)).Frontier
            ?? throw new InvalidOperationException("The reconciliation command activation did not start.");
        var started = running with
        {
            LifecycleVersion = running.LifecycleVersion + 1,
            UpdatedAtUtc = now.AddMinutes(2),
            Frontier = startedFrontier,
        };
        RequireUpdated(await runs.UpdateAsync(started, running.LifecycleVersion), "started frontier");
        var recovered = await new CustomLoopRecoveryService(
            runs,
            new AuditLog(paths),
            new HumanReviewRecoveryCanonicalTimeProvider(now.AddMinutes(3))).RecoverAsync("user-owner");
        var recovery = Assert.Single(recovered);
        Assert.Equal(EmbodySense.Core.Application.Loops.Execution.Custom.Models.CustomLoopRecoveryStatus.NeedsReview, recovery.Status);
        var blocked = await runs.GetAsync(execution.RunId)
            ?? throw new InvalidOperationException("The reconciliation command run disappeared after recovery.");
        Assert.Equal(CustomLoopRunStatus.NeedsReview, blocked.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, blocked.Frontier?.Payload.Status);

        var commandInput = new CommandActionInput(
            1,
            registration.Template.TemplateId,
            registration.Template.TemplateVersion,
            registration.Template.ContentHash,
            []);
        var canonicalInput = CommandActionInputContract.Encode(commandInput, registration.Template);
        if (!GovernedActuatorInputContract.TryCanonicalize(canonicalInput, out var input, out var inputError))
        {
            throw new InvalidOperationException("The reconciliation command input was invalid: " + inputError);
        }
        Assert.True(CommandActionInputContract.TryMaterialize(input!.CanonicalJson, registration.Template, out var commandMaterialized, out var materializationError), materializationError);
        var operationRegistry = GovernedCommandActionFactory.CreateRegistry(
            paths,
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            DenyingCommandActionProcessIsolationBoundary.Instance);
        var descriptor = Assert.Single(operationRegistry.Descriptors);
        var target = Hash("command-target");
        var precondition = Hash("command-precondition");
        var evidenceStore = new CommandActionEvidenceStore(paths);
        var preparation = CommandActionEvidenceContract.CreatePreparation(registration.Template, commandMaterialized!.InputFingerprint, target, precondition, now.AddMinutes(2));
        await evidenceStore.RetainPreparationAsync(preparation);
        var prepared = GovernedLoopEffectAttemptContract.Prepare(
            execution,
            actionNode.NodeId,
            1,
            descriptor.Capability,
            descriptor.Implementation,
            descriptor.OperationId,
            descriptor.ContentHash,
            "effect-reconciliation-command",
            "attempt-reconciliation-command",
            1,
            input.Fingerprint,
            target,
            precondition,
            receipt.ContentHash,
            preparation.EvidenceId,
            now.AddMinutes(2));
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash("command-dispatch-authority"), now.AddMinutes(3));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, now.AddMinutes(4));
        var attempt = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, now.AddMinutes(5));
        var effectStore = new GovernedLoopEffectAttemptStore(paths);
        var created = await effectStore.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, created.Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(prepared.ContentHash, authorized, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(authorized.ContentHash, crossed, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(crossed.ContentHash, attempt, created.Lease!)).Status);
        created.Lease!.Dispose();

        if (retainOutcome)
        {
            var outcome = CommandActionEvidenceContract.CreateOutcome(
                attempt.Payload.EffectId,
                attempt.Payload.OperationId,
                attempt.Payload.EffectGeneration,
                registration.Template,
                commandMaterialized.InputFingerprint,
                target,
                precondition,
                preparation.EvidenceId,
                outcomeKind,
                CommandActionTerminationPosture.Exited,
                outcomeKind == CommandActionOutcomeKind.Succeeded ? 0 : 7,
                outcomeKind == CommandActionOutcomeKind.Succeeded ? "{}" : null,
                outcomeKind == CommandActionOutcomeKind.Succeeded ? null : "[redacted]",
                2,
                0,
                1,
                now.AddMinutes(5));
            await evidenceStore.RetainOutcomeAsync(outcome);
            Assert.True(operationRegistry.TryResolve(descriptor, out var registeredOperation));
            var outcomeProbe = Assert.IsAssignableFrom<IGovernedActuatorOutcomeProbe>(registeredOperation);
            var observed = await outcomeProbe.ProbeAsync(new GovernedActuatorInvocation(
                descriptor,
                attempt.Payload.EffectId,
                attempt.Payload.OperationId,
                attempt.Payload.EffectGeneration,
                input,
                target,
                precondition,
                preparation.EvidenceId));
            Assert.True(observed.Posture == GovernedActuatorProbePosture.OutcomeObserved, $"The command outcome probe returned {observed.Posture} before runtime composition.");
        }

        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(workspaceId, mismatchRunNodeBinding ? actionNode.Ordinal + 1 : actionNode.Ordinal, 1, attempt);
        var metadata = Metadata(descriptor);
        var openedAtUtc = now.AddMinutes(6);
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "case-reconciliation-command",
            binding.ContentHash,
            "source-reconciliation-command",
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            metadata.ContractId,
            metadata.ContractVersion,
            metadata.ContentHash,
            Hash("command-source-authority"),
            openedAtUtc,
            null,
            string.Empty));
        var value = GovernedLoopEffectReconciliationContract.Open(
            "case-reconciliation-command",
            binding,
            metadata,
            [source],
            [Hash("command-case-receipt")],
            openedAtUtc);
        var caseStore = new EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.GovernedLoopEffectReconciliationCaseStore(effectStore);
        var persisted = await caseStore.CompareExchangeAsync(new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseMutationRequest(
            "open-reconciliation-command",
            Hash("open-reconciliation-command-request"),
            "open",
            null,
            null,
            binding,
            value,
            null));
        Assert.Equal(EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseMutationStatus.Applied, persisted.Status);
        return (
            new CommandActionRuntimeProvider([registration], DenyingCapabilityExecutableArtifactResolver.Instance, DenyingCommandActionProcessIsolationBoundary.Instance),
            value,
            attempt);
    }

    private static CustomLoopRunRecord TransitionToRunning(CustomLoopRunRecord admitted, DateTimeOffset updatedAtUtc)
    {
        var lifecycle = new CustomLoopRunEvent(
            admitted.Events[^1].Sequence + 1,
            "event-running-reconciliation-command",
            updatedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "The test run entered its canonical running lifecycle.",
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
            null,
            ControlExpectedLifecycleVersion: admitted.LifecycleVersion);
        return admitted with
        {
            LifecycleVersion = admitted.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = updatedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(0, updatedAtUtc),
            Events = [.. admitted.Events, lifecycle],
        };
    }

    private static void RequireUpdated(CustomLoopRunStoreResult result, string stage)
    {
        if (result.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The reconciliation command {stage} was not persisted: {result.Status}.");
        }
    }

    private static async Task PersistGraphAsync(WorkspacePaths paths, string capabilityTrustRoot, GovernedLoopGraphRevisionArtifact artifact)
    {
        var trust = new FileCapabilityCatalogTrustProvider(capabilityTrustRoot);
        var transaction = new CapabilityAuthorityTransaction(paths);
        var lifecycleStore = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
        var graphStore = new GovernedLoopGraphRevisionStore(paths, lifecycleStore, trust, authorityTransaction: transaction);
        var operationId = artifact.RevisionArtifact.CreationOperationId;
        var requestHash = Hash("command-graph-create-request");
        var authoringHash = Hash("command-graph-authoring-request");
        var recordedAtUtc = artifact.RevisionArtifact.CreatedAtUtc;
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(1, artifact.Graph.GraphId, 1, GovernedLoopRevisionLifecycleStatus.Draft, artifact.RevisionArtifact.Revision, null, operationId, recordedAtUtc);
        var evidence = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "user-owner",
            requestHash,
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            null,
            head,
            artifact.RevisionArtifact.Revision,
            null,
            null,
            Hash("command-graph-evidence"),
            null,
            recordedAtUtc);
        var read = await graphStore.ReadForMutationAsync(artifact.Graph.GraphId, operationId, requestHash, authoringHash);
        Assert.Equal(EmbodySense.Core.Application.Loops.Revisions.Models.GovernedLoopRevisionStoreReadStatus.NotFound, read.Status);
        var committed = await graphStore.CommitAsync(new EmbodySense.Core.Application.Loops.GraphAuthoring.Models.GovernedLoopGraphRevisionStoreMutation(
            new EmbodySense.Core.Application.Loops.Revisions.Models.GovernedLoopRevisionStoreMutation(artifact.Graph.GraphId, read.StoreGeneration, evidence, artifact.RevisionArtifact, head),
            artifact.Graph,
            authoringHash,
            Hash("command-graph-validation")));
        Assert.Equal(EmbodySense.Core.Application.Loops.Revisions.Models.GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
    }

    private static GovernedLoopEffectReconciliationContractMetadata Metadata(GovernedActuatorOperationDescriptor descriptor)
    {
        var discriminator = descriptor.ContentHash[..32];
        var probeHash = HashCommand("probe-contract", descriptor.ContentHash, descriptor.Capability.Hash.Value, descriptor.OperationId);
        return GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "command-reconciliation-" + discriminator,
            1,
            descriptor.Capability,
            descriptor.Implementation,
            descriptor.OperationId,
            descriptor.ContentHash,
            "command-outcome-probe-" + discriminator,
            1,
            probeHash,
            string.Empty));
    }

    private static GovernedLoopAdmissionReceipt CreateAdmissionReceipt(
        CommandActionRegistration registration,
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopExecutionBinding execution,
        GovernedLoopAdmissionRequest request,
        string workspaceId,
        DateTimeOffset now)
    {
        if (!AuthorityProfileId.TryParse("profile-reconciliation-command", out var profileId, out _)
            || !AuthorityProfileRevision.TryParse("1", out var profileRevision, out _)
            || !AuthorityProfileHash.TryParse("sha256:" + Hash("command-profile"), out var profileHash, out _))
        {
            throw new InvalidOperationException("The reconciliation command profile pin is invalid.");
        }
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            workspaceId,
            request.OperationId,
            request.RequestHash,
            request.Publication,
            request.AuthorityGrant,
            artifact.Graph.OwningRole,
            request.ActorId,
            request.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var capabilityAdmission = CreateCapabilityAdmission(registration, artifact, workspaceId, now);
        var effectiveAuthority = CreateRequiredAuthority(registration);
        var grantProfile = new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!);
        var grantBoundary = new AuthorityGrantBoundary(now.AddHours(-1), now.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var evidence = GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(
            intent,
            execution,
            grantProfile,
            grantBoundary,
            Hash("command-dependency-evidence"),
            effectiveAuthority,
            capabilityAdmission,
            now);
        return GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            evidence,
            now,
            string.Empty));
    }

    private static CapabilityAdmissionSnapshot CreateCapabilityAdmission(CommandActionRegistration registration, GovernedLoopGraphRevisionArtifact artifact, string workspaceId, DateTimeOffset now)
    {
        if (!CapabilityId.TryParse("org.example/reconciliation-command-loop", out var subjectId, out _)
            || !CapabilityVersionRange.TryParse("*", out var versionRange, out _)
            || !CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var artifactChecksum, out _))
        {
            throw new InvalidOperationException("The reconciliation command capability manifest identity is invalid.");
        }
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subjectId!,
            [
                new CapabilityDependency(ParseCapabilityId(GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId), versionRange!),
                new CapabilityDependency(registration.Template.Capability.Id, versionRange!),
            ],
            [],
            new CapabilityDependencyArtifactMetadata(artifactChecksum, null));
        if (!CapabilityDependencyManifestHash.TryCompute(manifest, out var manifestHash, out _))
        {
            throw new InvalidOperationException("The reconciliation command capability manifest hash is invalid.");
        }
        var descriptor = registration.Manifest.Descriptor;
        var commandPin = new CapabilityAdmissionPin(
            registration.Template.Capability,
            descriptor.Kind,
            registration.Template.Implementation,
            descriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            descriptor.Purpose);
        var conversationDescriptor = BuiltInCapabilityCatalog.Descriptors.Single(candidate => string.Equals(candidate.Id.Value, GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId, StringComparison.Ordinal));
        if (!CapabilityDescriptorIdentity.TryCreate(conversationDescriptor, out var conversationIdentity, out _))
        {
            throw new InvalidOperationException("The reconciliation command conversation capability identity is invalid.");
        }
        var conversationPin = new CapabilityAdmissionPin(
            conversationIdentity!,
            conversationDescriptor.Kind,
            conversationDescriptor.Implementation,
            conversationDescriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            conversationDescriptor.Purpose);
        return new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            workspaceId,
            manifest,
            manifestHash!.Value,
            [conversationPin, commandPin],
            [
                new CapabilityAdmissionEvidence(subjectId!, conversationIdentity!.Id, versionRange!, false, "Selected", conversationPin.DescriptorIdentity, "Selected exact conversation-turn capability."),
                new CapabilityAdmissionEvidence(subjectId!, registration.Template.Capability.Id, versionRange!, false, "Selected", commandPin.DescriptorIdentity, "Selected exact reconciliation command registration."),
            ],
            now);
    }

    private static CapabilityId ParseCapabilityId(string value)
    {
        if (!CapabilityId.TryParse(value, out var capabilityId, out _))
        {
            throw new InvalidOperationException("The reconciliation command capability identity is invalid.");
        }
        return capabilityId!;
    }

    private static AuthorityCeiling CreateRequiredAuthority(CommandActionRegistration registration)
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

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashCommand(string domain, params string[] values)
    {
        var builder = new StringBuilder("embodysense.command-reconciliation.v1\n").Append(domain).Append('\n');
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
