using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

internal static class HumanInputBrowserFixture
{
    private static readonly DateTimeOffset _stableDependencyTimestamp = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset _stableGrantStartsAtUtc = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _stableGrantExpiresAtUtc = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string AuthorityProfileId = "human-review-browser";
    private const string GraphId = "browser-human-input-graph";
    private const string RevisionId = "revision-1";
    private const string NodeId = "human-input";
    private const string CheckpointId = "checkpoint-one";
    private const string RoleId = "browser-human-input-role";
    private const string ActorId = "user-owner";
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";

    internal static async Task SeedPendingAsync(
        WorkspacePaths paths,
        string requestId,
        string prompt,
        string capabilityTrustRoot,
        TimeSpan? requestLifetime = null,
        HumanInputPrivacyClass privacyClass = HumanInputPrivacyClass.Private,
        string? eligibleRespondentId = null,
        DateTimeOffset? requestExpiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityTrustRoot);

        var now = requestExpiresAtUtc is { } expiresAtUtc
            ? expiresAtUtc.ToUniversalTime() - HumanInputLimits.MinResponseWindow
            : requestLifetime is { } lifetime && lifetime <= TimeSpan.FromMinutes(2)
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.UtcNow.AddMinutes(-5);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var dependencies = await EnsureAuthorityDependenciesAsync(paths, capabilityTrustRoot, workspaceId, now).ConfigureAwait(false);
        var request = CreateRequest(workspaceId, requestId, prompt, dependencies.Publication, now, requestLifetime, requestExpiresAtUtc, privacyClass, eligibleRespondentId);
        await SeedRequestLifecycleAsync(paths, capabilityTrustRoot, request, dependencies.GrantReference, now).ConfigureAwait(false);
    }

    internal static async Task SeedWaitingAsync(WorkspacePaths paths, string requestId, string prompt, string capabilityTrustRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityTrustRoot);

        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var dependencies = await EnsureAuthorityDependenciesAsync(paths, capabilityTrustRoot, workspaceId, now).ConfigureAwait(false);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(dependencies.Artifact);
        var plan = planResult.Plan ?? throw new InvalidOperationException($"The browser Human Input continuation graph was not plannable: {planResult.Status}.");
        var runId = requestId + "-run";
        var execution = GovernedLoopExecutionBinding.Create(1, runId, dependencies.Publication.Revision, 1);
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(1, prompt, new CustomLoopModelSnapshot("provider", "model"), null, now, context.SourceManifest, string.Empty));
        var admissionOperationId = "browser-human-input-admit-" + requestId;
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(1, admissionOperationId, invocation.ContentHash, string.Empty, dependencies.Publication, dependencies.GrantReference, ParseActor(ActorId), "web"));
        var intent = new GovernedLoopAdmissionIntent(1, workspaceId, admissionRequest.OperationId, admissionRequest.RequestHash, dependencies.Publication, dependencies.GrantReference, dependencies.Artifact.Graph.OwningRole, ParseActor(ActorId), "web", dependencies.Artifact.ArtifactHash, dependencies.Artifact.LayoutHash);
        var receipt = CreateAdmissionReceipt(dependencies.Artifact, execution, intent, workspaceId, now, dependencies.GrantProfile, dependencies.GrantBoundary, dependencies.DependencyEvidenceHash, dependencies.EffectiveAuthority);
        var admissionOutcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            intent,
            GovernedLoopAdmissionDisposition.Admitted,
            receipt,
            null,
            now,
            string.Empty));
        var admissionValidation = GovernedLoopAdmissionValidator.Validate(admissionOutcome);
        if (!admissionValidation.IsValid)
        {
            throw new InvalidOperationException("The browser Human Input fixture admission outcome is invalid: " + string.Join(',', admissionValidation.Errors));
        }

        var canonicalAdmissionStore = new GovernedLoopAdmissionStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        var admissionRead = await canonicalAdmissionStore.ReadByOperationAsync(workspaceId, admissionRequest.OperationId).ConfigureAwait(false);
        if (admissionRead.Status != GovernedLoopAdmissionStoreReadStatus.NotFound || admissionRead.Outcome is not null)
        {
            throw new InvalidOperationException($"The browser Human Input fixture admission identity was not empty: {admissionRead.Status}.");
        }

        var admissionCommit = await canonicalAdmissionStore.CommitAsync(new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            admissionRead.StoreGeneration,
            admissionOutcome)).ConfigureAwait(false);
        if (admissionCommit.Status != GovernedLoopAdmissionStoreCommitStatus.Committed)
        {
            throw new InvalidOperationException($"The browser Human Input fixture admission outcome was not committed: {admissionCommit.Status}.");
        }

        var adapter = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(1, workspaceId, execution, admissionRequest.OperationId, receipt, receipt.ContentHash, admissionRequest.RequestHash, invocation.ContentHash, dependencies.Artifact.ArtifactHash, dependencies.Artifact.LayoutHash, [], string.Empty));
        var projected = GovernedLoopSequentialLegacyDefinitionProjector.Project(adapter, invocation, plan, dependencies.Artifact);
        var definition = projected.Definition ?? throw new InvalidOperationException($"The browser Human Input continuation graph definition was not projected: {projected.Status}.");
        var admittedEvent = CreateAdmittedEvent(adapter, now);
        var initialized = GovernedLoopSequentialFrontierMachine.Initialize(adapter, plan, admittedEvent.EventId, admittedEvent.EventId, admittedEvent.SequentialNodeEvidence!.OutcomeArtifactHash, admittedEvent.TimestampUtc).Frontier as GovernedLoopFrontierPosture
            ?? throw new InvalidOperationException("The browser Human Input continuation frontier was not initialized.");
        var admissionAuditEvent = new CustomLoopRunEvent(2, "browser-human-input-admission-audit-" + runId, now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null);
        var seed = CustomLoopAdmissionRequestHash.Apply(new CustomLoopRunRecord(1, runId, definition.Id, 1, CustomLoopRunStatus.Admitted, now, now, null, "web", invocation.ModelSnapshot, admissionOperationId, ActorId, string.Empty, definition, prompt, null, context, CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admittedEvent, admissionAuditEvent], null, null, null)
        {
            CapabilityAdmission = receipt.Evidence.CapabilityAdmission,
            SequentialInvocationSnapshot = invocation,
            SequentialAdapterBinding = adapter,
            Frontier = initialized,
        });
        if (!CustomLoopRunValidator.HasCompleteAdmissionAudit(seed))
        {
            throw new InvalidOperationException("The browser Human Input fixture admission audit was not complete before run creation.");
        }
        using var runs = new CustomLoopRunStore(paths);
        var created = await runs.CreateAsync(seed).ConfigureAwait(false);
        if (created.Status is not (CustomLoopRunStoreStatus.Created or CustomLoopRunStoreStatus.AlreadyCreated))
        {
            throw new InvalidOperationException($"The browser Human Input continuation run was not created: {created.Status}.");
        }

        var selected = GovernedLoopSequentialFrontierMachine.Select(initialized, adapter, plan);
        var node = plan.Nodes.Single(item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanInput);
        var activation = selected.Activation ?? throw new InvalidOperationException("The browser Human Input continuation node was not ready.");
        var started = GovernedLoopSequentialFrontierMachine.Start(initialized, adapter, plan, node, activation, 1, "browser-human-input-claim-" + requestId, now.AddMinutes(1)).Frontier as GovernedLoopFrontierPosture
            ?? throw new InvalidOperationException("The browser Human Input continuation node did not start.");
        var runningEvent = new CustomLoopRunEvent(seed.Events[^1].Sequence + 1, "browser-human-input-running-" + runId, now.AddMinutes(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered its canonical running lifecycle.", [], null, null, null, null, null, null, null, null, null, null, ControlExpectedLifecycleVersion: seed.LifecycleVersion);
        var running = seed with
        {
            LifecycleVersion = 2,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = now.AddMinutes(1),
            ExecutionClock = new CustomLoopExecutionClock(0, now.AddMinutes(1)),
            Frontier = started,
            Events = [.. seed.Events, runningEvent],
        };
        var updated = await runs.UpdateAsync(running, seed.LifecycleVersion).ConfigureAwait(false);
        if (updated.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Human Input continuation run did not start: {updated.Status}.");
        }

        var runningActivation = started.Payload.Nodes[activation.ActivationOrdinal];
        var waitingFrontier = GovernedLoopSequentialFrontierMachine.ParkRunningHumanInput(started, adapter, plan, node, runningActivation, 1, runningActivation.AttemptOperationId!, now.AddMinutes(2)).Frontier as GovernedLoopFrontierPosture
            ?? throw new InvalidOperationException("The browser Human Input continuation frontier did not park.");
        var configuration = dependencies.Artifact.Graph.Nodes.Single(item => string.Equals(item.Id, node.NodeId, StringComparison.Ordinal)).HumanInputConfiguration
            ?? throw new InvalidOperationException("The browser Human Input continuation configuration was missing.");
        var checkpoint = CreateCheckpoint(adapter, running, node, waitingFrontier.Payload.Nodes[runningActivation.ActivationOrdinal], waitingFrontier, configuration, requestId, now.AddMinutes(2));
        var lifecycleEvent = new CustomLoopRunEvent(running.Events[^1].Sequence + 1, "browser-human-input-waiting-" + requestId, now.AddMinutes(2), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Ordered execution is parked on the exact durable Human Input checkpoint.", [], null, null, null, null, null, null, null, null, null, null);
        var waiting = running with
        {
            LifecycleVersion = 3,
            Status = CustomLoopRunStatus.Waiting,
            UpdatedAtUtc = now.AddMinutes(2),
            ExecutionClock = new CustomLoopExecutionClock(120_000, null),
            Frontier = waitingFrontier,
            HumanInputWaitingCheckpoints = [checkpoint],
            Events = [.. running.Events, lifecycleEvent],
        };
        var validation = CustomLoopRunValidator.Validate(waiting);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("The browser Human Input continuation run was invalid: " + string.Join("; ", validation.Errors.Select(error => error.Code)));
        }

        updated = await runs.UpdateAsync(waiting, running.LifecycleVersion).ConfigureAwait(false);
        if (updated.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Human Input continuation checkpoint was not persisted: {updated.Status}.");
        }

        var publication = CreatePublicationCommand(runId, checkpoint.Request, dependencies.GrantReference);
        await SeedRequestLifecycleAsync(paths, capabilityTrustRoot, checkpoint.Request, dependencies.GrantReference, now.AddMinutes(2), publication.OperationId, publication.RequestHash, "human-input-checkpoint-publication").ConfigureAwait(false);
    }

    private static async Task SeedRequestLifecycleAsync(WorkspacePaths paths, string capabilityTrustRoot, HumanInputRequest request, AuthorityGrantReference grantReference, DateTimeOffset now, string? operationIdOverride = null, string? requestHashOverride = null, string purposeText = "Create one exact browser Human Input request.")
    {
        var operationId = operationIdOverride ?? "browser-human-input-create-" + request.RequestId;
        var requestHash = requestHashOverride ?? Hash("request-create-" + request.RequestId);
        var head = CreateHead(request, 1, HumanInputRequestLifecycleStatus.Pending, operationId, now);
        var evidence = new HumanInputRequestLifecycleOperationEvidence(
            1,
            operationId,
            requestHash,
            HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationOutcome.Committed,
            HumanInputRequestLifecycleOperationFailureCode.None,
            request.RequestId,
            0,
            HumanInputRequestLifecycleStatus.Unknown,
            null,
            null,
            null,
            head,
            null,
            null,
            null,
            CreateReference(request),
            ParseActor(ActorId),
            ParsePurpose(purposeText),
            grantReference,
            Hash("authority-" + request.RequestId),
            Hash("dependency-" + request.RequestId),
            now);

        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        var read = await store.ReadForMutationAsync(request.RequestId, operationId, requestHash).ConfigureAwait(false);
        if (read.Status == HumanInputRequestLifecycleStoreReadStatus.Ready)
        {
            if (read.PrimarySnapshot?.Head.CurrentRequest.RequestHash != request.RequestHash)
            {
                throw new InvalidOperationException("The browser Human Input fixture request was already persisted with a different exact request.");
            }

            return;
        }

        if (read.Status != HumanInputRequestLifecycleStoreReadStatus.NotFound)
        {
            throw new InvalidOperationException($"The browser Human Input fixture request was not empty: {read.Status}.");
        }

        var committed = await store.CommitAsync(new HumanInputRequestLifecycleStoreMutation(read.StoreGeneration, evidence, request, head, null)).ConfigureAwait(false);
        if (committed.Status is not (HumanInputRequestLifecycleStoreCommitStatus.Committed or HumanInputRequestLifecycleStoreCommitStatus.Replayed))
        {
            throw new InvalidOperationException($"The browser Human Input fixture request was not persisted: {committed.Status}.");
        }
    }

    private static HumanInputRequestLifecycleCommand CreatePublicationCommand(string runId, HumanInputRequest request, AuthorityGrantReference grantReference)
    {
        var operationMaterial = string.Join('\n', "embodysense-human-input-checkpoint-publication-v1", runId, request.Binding.CheckpointId, request.RequestId, request.RequestVersionId, request.RequestHash);
        var operationId = "human-input-publication-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operationMaterial)));
        return HumanInputRequestLifecycleCommandHash.Apply(new HumanInputRequestLifecycleCommand(
            HumanInputRequestLifecycleCommand.CurrentSchemaVersion,
            operationId,
            HumanInputRequestLifecycleOperationKind.Create,
            request.RequestId,
            0,
            HumanInputRequestLifecycleStatus.Unknown,
            null,
            null,
            request,
            grantReference,
            ParsePurpose("human-input-checkpoint-publication"),
            string.Empty));
    }

    internal static async Task<HumanInputRequestLifecycleStoreReadResult> ReadAsync(WorkspacePaths paths, string capabilityTrustRoot, string requestId)
    {
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        return await store.ReadAsync(requestId).ConfigureAwait(false);
    }

    internal static async Task<HumanInputResponseLifecycleStoreReadResult> ReadResponsesAsync(WorkspacePaths paths, string capabilityTrustRoot, string requestId)
    {
        var lifecycle = await ReadAsync(paths, capabilityTrustRoot, requestId).ConfigureAwait(false);
        if (lifecycle.PrimarySnapshot is null)
        {
            return new HumanInputResponseLifecycleStoreReadResult(HumanInputResponseLifecycleStoreReadStatus.NotFound, lifecycle.StoreGeneration, null, null);
        }

        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        return await ((IHumanInputResponseLifecycleStore)store).ReadAsync(lifecycle.PrimarySnapshot.Head.CurrentRequest).ConfigureAwait(false);
    }

    private static HumanInputRequest CreateRequest(string workspaceId, string requestId, string prompt, GovernedLoopRevisionPublicationPin publication, DateTimeOffset requestedAtUtc, TimeSpan? requestLifetime, DateTimeOffset? requestExpiresAtUtc, HumanInputPrivacyClass privacyClass, string? eligibleRespondentId)
    {
        var binding = new HumanInputRequestBinding(workspaceId, publication.Revision.GraphId, publication.Revision.RevisionId, NodeId, requestId, CheckpointId);
        var expiresAtUtc = requestExpiresAtUtc?.ToUniversalTime() ?? requestedAtUtc.Add(requestLifetime ?? TimeSpan.FromHours(1));
        var request = new HumanInputRequest(1, requestId, requestId + "-v1", binding, "Collect one bounded browser response.", prompt, new HumanInputResponseSchema(HumanInputResponseKind.Text, 128, null, null, null), privacyClass, [new HumanInputEligibleRespondent(eligibleRespondentId ?? WorkspaceActors.Web, "web-respondent", "web")], new HumanInputTiming(requestedAtUtc, expiresAtUtc), new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null), new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, NodeId, CheckpointId), string.Empty);
        return HumanInputRequestHash.Apply(request);
    }

    private static HumanInputRequestLifecycleHead CreateHead(HumanInputRequest request, long lifecycleVersion, HumanInputRequestLifecycleStatus status, string operationId, DateTimeOffset updatedAtUtc)
        => new(1, request.RequestId, lifecycleVersion, status, CreateReference(request), 0, null, null, operationId, updatedAtUtc);

    private static HumanInputRequestReference CreateReference(HumanInputRequest request)
    {
        if (!HumanInputRequestReference.TryCreate(request, out var reference, out var validation) || reference is null)
        {
            throw new InvalidOperationException(string.Join(',', validation.Errors.Select(error => error.Code)));
        }

        return reference;
    }

    private static async Task<(AuthorityGrantReference GrantReference, GovernedLoopRevisionPublicationPin Publication, GovernedLoopGraphRevisionArtifact Artifact, AuthorityGrant Grant, AuthorityGrantProfilePin GrantProfile, AuthorityGrantBoundary GrantBoundary, AuthorityCeiling EffectiveAuthority, string DependencyEvidenceHash)> EnsureAuthorityDependenciesAsync(WorkspacePaths paths, string capabilityTrustRoot, string workspaceId, DateTimeOffset now)
    {
        var transaction = new CapabilityAuthorityTransaction(paths);
        var role = CreateRole(workspaceId, _stableDependencyTimestamp);
        using var roleStore = new ContextualRoleRevisionStore(paths, workspaceId, authorityTransaction: transaction);
        var roleRead = await roleStore.ReadAsync(new ContextualRoleRevisionReadRequest(role.Identity)).ConfigureAwait(false);
        if (roleRead.Status == ContextualRoleRevisionReadStatus.NotFound)
        {
            var roleRequest = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("create-browser-human-input-role", string.Empty, ContextualRoleRevisionMutationKind.Create, role.Identity.RoleId, "browser-e2e", role, null, now));
            var roleMutation = await roleStore.MutateAsync(roleRequest).ConfigureAwait(false);
            if (roleMutation.Status != ContextualRoleRevisionMutationStatus.Accepted)
            {
                throw new InvalidOperationException($"The browser Human Input fixture role was not persisted: {roleMutation.Status}.");
            }
        }
        else if (roleRead.Status != ContextualRoleRevisionReadStatus.Found || roleRead.Revision is null || !string.Equals(roleRead.Revision.ContentHash, role.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The browser Human Input fixture role could not be reused exactly: {roleRead.Status}.");
        }

        var trust = new FileCapabilityCatalogTrustProvider(capabilityTrustRoot);
        var lifecycleStore = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
        var graphStore = new GovernedLoopGraphRevisionStore(paths, lifecycleStore, trust, authorityTransaction: transaction);
        var artifact = CreateArtifact(role, _stableDependencyTimestamp);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "browser-human-input-publish", Hash("publication"));
        await EnsurePublishedGraphAsync(graphStore, artifact, publication, _stableDependencyTimestamp).ConfigureAwait(false);

        var authorityStore = new AuthorityProfileStore(paths, trust, authorityTransaction: transaction);
        var profileRead = await authorityStore.ReadAsync(AuthorityProfileId).ConfigureAwait(false);
        if (profileRead.Record is null || profileRead.Record.CurrentHash is null || profileRead.Record.CurrentProfile.Status != AuthorityProfileStatus.Active)
        {
            throw new InvalidOperationException($"The browser Human Input fixture authority profile was not active: {profileRead.Status}.");
        }

        var binding = new AuthorityGrantBinding(new AuthorityGrantProfilePin(new AuthorityProfileReference(profileRead.Record.ProfileId, profileRead.Record.CurrentProfile.Revision), profileRead.Record.CurrentHash), new ContextualRoleRevisionPin(role.Identity, role.ContentHash), publication);
        var grantIdText = "grant-browser-human-input-" + RequestIdFragment(workspaceId);
        if (!AuthorityGrantId.TryParse(grantIdText, out var grantId, out _)
            || !AuthorityGrantRevision.TryParse("1", out var grantRevision, out _)
            || !AuthorityActorId.TryParse(ActorId, out var actor, out _)
            || !AuthorityPurpose.TryParse("Browser Human Input fixture grant.", out var purpose, out _))
        {
            throw new InvalidOperationException("The browser Human Input fixture grant identity is invalid.");
        }

        var grant = AuthorityGrantHash.Apply(new AuthorityGrant(1, grantId!, grantRevision!, null, null, AuthorityGrantLifecycleStatus.Active, binding, CreateConversationTurnAuthorityCeiling(), new AuthorityGrantBoundary(_stableGrantStartsAtUtc, _stableGrantExpiresAtUtc, AuthorityGrantCompletionConstraintKind.None), actor!, purpose!, _stableDependencyTimestamp, string.Empty));
        var grantOperationId = "create-browser-human-input-grant-" + RequestIdFragment(workspaceId);
        var grantRequestHash = Hash("grant-request-" + workspaceId);
        var observed = await authorityStore.ReadForMutationAsync(grant.GrantId, grantOperationId, grantRequestHash).ConfigureAwait(false);
        if (observed.Status == AuthorityGrantStoreReadStatus.NotFound)
        {
            var grantEvidence = new AuthorityGrantOperationEvidence(1, grantOperationId, grantRequestHash, AuthorityGrantOperationKind.Create, AuthorityGrantOperationOutcome.Committed, AuthorityGrantOperationFailureCode.None, grant.GrantId, 0, new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash), actor!, purpose!, Hash("grant-authority-" + workspaceId), Hash("grant-dependency-" + workspaceId), _stableDependencyTimestamp);
            var commit = await authorityStore.CommitAsync(new AuthorityGrantStoreMutation(observed.StoreGeneration, grant, grantEvidence)).ConfigureAwait(false);
            if (commit.Status is not (AuthorityGrantStoreCommitStatus.Committed or AuthorityGrantStoreCommitStatus.Replayed))
            {
                throw new InvalidOperationException($"The browser Human Input fixture grant was not persisted: {commit.Status}.");
            }
        }
        else if (observed.Status != AuthorityGrantStoreReadStatus.Ready)
        {
            throw new InvalidOperationException($"The browser Human Input fixture grant could not be read: {observed.Status}.");
        }

        var publicationSource = new GovernedLoopPublishedRevisionSource(lifecycleStore, transaction);
        var bindingSource = new GovernedLoopGrantBindingSource(publicationSource, graphStore, transaction);
        var roleSource = new AuthorityGrantRoleSource(workspaceId, roleStore, roleStore, new WorkspaceContextualRoleInstructionSourceProbe(paths), transaction);
        var resolver = new AuthorityGrantResolver(authorityStore, new AuthorityGrantProfileSource(authorityStore), roleSource, publicationSource, bindingSource, transaction);
        var resolved = await resolver.ResolveAsync(new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash)).ConfigureAwait(false);
        if (resolved.Status != AuthorityGrantResolutionStatus.Active || string.IsNullOrWhiteSpace(resolved.DependencyEvidenceHash))
        {
            throw new InvalidOperationException($"The browser Human Input fixture grant dependencies were not active: {resolved.Status}.");
        }

        return (new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash), publication, artifact, grant, binding.Profile, grant.Boundary, resolved.EffectiveCeiling, resolved.DependencyEvidenceHash);
    }

    private static async Task EnsurePublishedGraphAsync(GovernedLoopGraphRevisionStore graphStore, GovernedLoopGraphRevisionArtifact artifact, GovernedLoopRevisionPublicationPin publication, DateTimeOffset now)
    {
        var createOperationId = artifact.RevisionArtifact.CreationOperationId;
        var createRequestHash = Hash("graph-create-" + GraphId);
        var createAuthoringHash = Hash("graph-authoring-" + GraphId);
        var read = await graphStore.ReadForMutationAsync(GraphId, createOperationId, createRequestHash, createAuthoringHash).ConfigureAwait(false);
        if (read.Status == GovernedLoopRevisionStoreReadStatus.NotFound)
        {
            var draftHead = GovernedLoopRevisionLifecycleHeadFactory.Create(1, GraphId, 1, GovernedLoopRevisionLifecycleStatus.Draft, artifact.RevisionArtifact.Revision, null, createOperationId, now);
            var draftEvidence = GovernedLoopRevisionOperationEvidenceFactory.Create(1, createOperationId, ActorId, createRequestHash, GovernedLoopRevisionOperationKind.CreateDraft, GovernedLoopRevisionOperationOutcome.Committed, GovernedLoopRevisionOperationFailureCode.None, null, draftHead, artifact.RevisionArtifact.Revision, null, null, Hash("graph-evidence-" + GraphId), null, now);
            var draftCommit = await graphStore.CommitAsync(new GovernedLoopGraphRevisionStoreMutation(new GovernedLoopRevisionStoreMutation(GraphId, read.StoreGeneration, draftEvidence, artifact.RevisionArtifact, draftHead), artifact.Graph, createAuthoringHash, Hash("graph-validation-" + GraphId))).ConfigureAwait(false);
            if (draftCommit.Status is not (GovernedLoopRevisionStoreCommitStatus.Committed or GovernedLoopRevisionStoreCommitStatus.Replayed))
            {
                throw new InvalidOperationException($"The browser Human Input fixture graph draft was not persisted: {draftCommit.Status}.");
            }

            var publishRequestHash = Hash("graph-publish-request-" + GraphId);
            var publishAuthoringHash = Hash("graph-publish-authoring-" + GraphId);
            var publishedHead = GovernedLoopRevisionLifecycleHeadFactory.Create(1, GraphId, 2, GovernedLoopRevisionLifecycleStatus.Published, null, publication, publication.PublicationOperationId, now.AddSeconds(1));
            var publishedEvidence = GovernedLoopRevisionOperationEvidenceFactory.Create(1, publication.PublicationOperationId, "browser-e2e", publishRequestHash, GovernedLoopRevisionOperationKind.Publish, GovernedLoopRevisionOperationOutcome.Committed, GovernedLoopRevisionOperationFailureCode.None, draftHead, publishedHead, null, artifact.RevisionArtifact.Revision, null, Hash("graph-publish-evidence-" + GraphId), publication.ValidationEvidenceHash, now.AddSeconds(1));
            var publishRead = await graphStore.ReadForMutationAsync(GraphId, publication.PublicationOperationId, publishRequestHash, publishAuthoringHash).ConfigureAwait(false);
            if (publishRead.Status != GovernedLoopRevisionStoreReadStatus.Ready)
            {
                throw new InvalidOperationException($"The browser Human Input fixture graph draft could not be reread: {publishRead.Status}.");
            }

            var publishCommit = await graphStore.CommitAsync(new GovernedLoopGraphRevisionStoreMutation(new GovernedLoopRevisionStoreMutation(GraphId, publishRead.StoreGeneration, publishedEvidence, null, publishedHead), null, publishAuthoringHash, publication.ValidationEvidenceHash)).ConfigureAwait(false);
            if (publishCommit.Status is not (GovernedLoopRevisionStoreCommitStatus.Committed or GovernedLoopRevisionStoreCommitStatus.Replayed))
            {
                throw new InvalidOperationException($"The browser Human Input fixture graph was not published: {publishCommit.Status}.");
            }
        }
        else if (read.Status != GovernedLoopRevisionStoreReadStatus.Ready)
        {
            throw new InvalidOperationException($"The browser Human Input fixture graph could not be read: {read.Status}.");
        }
    }

    private static GovernedLoopAdmissionReceipt CreateAdmissionReceipt(GovernedLoopGraphRevisionArtifact artifact, GovernedLoopExecutionBinding execution, GovernedLoopAdmissionIntent intent, string workspaceId, DateTimeOffset evaluatedAtUtc, AuthorityGrantProfilePin grantProfile, AuthorityGrantBoundary grantBoundary, string dependencyEvidenceHash, AuthorityCeiling effectiveAuthority)
    {
        if (!CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _)
            || !CapabilityVersionRange.TryParse("*", out var versions, out _)
            || !CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _))
        {
            throw new InvalidOperationException("The browser Human Input fixture capability identity is invalid.");
        }

        var dependencies = artifact.Graph.AuthorityCeiling.CapabilityIds.Select(value =>
        {
            if (!CapabilityId.TryParse(value, out var id, out _))
            {
                throw new InvalidOperationException("The browser Human Input fixture capability dependency is invalid.");
            }

            return new CapabilityDependency(id!, versions!);
        }).ToArray();
        var manifest = new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.LoopPackage, subject!, dependencies, [], new CapabilityDependencyArtifactMetadata(checksum, null));
        var capabilities = CreateCapabilityAdmission(manifest, workspaceId, evaluatedAtUtc);
        var modelRouting = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(intent, execution, grantProfile, grantBoundary, dependencyEvidenceHash, effectiveAuthority, capabilities, evaluatedAtUtc);
        var admissionEvidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(1, GovernedLoopAdmissionContractHash.ComputeIntentHash(intent), execution, grantProfile, grantBoundary, dependencyEvidenceHash, effectiveAuthority, capabilities, modelRouting, GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilities, modelRouting), evaluatedAtUtc, string.Empty));
        return GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(1, intent, admissionEvidence, evaluatedAtUtc, string.Empty));
    }

    private static AuthorityCeiling CreateConversationTurnAuthorityCeiling()
    {
        var descriptor = BuiltInCapabilityCatalog.Descriptors.SingleOrDefault(item => string.Equals(item.Id.Value, ConversationTurnCapabilityId, StringComparison.Ordinal));
        if (descriptor is null || !CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _))
        {
            throw new InvalidOperationException("The browser Human Input fixture conversation-turn capability descriptor is unavailable.");
        }

        return new AuthorityCeiling([identity!], descriptor.Requirements.DataClasses, 1, descriptor.SideEffectClass, false, false, false);
    }

    private static CapabilityAdmissionSnapshot CreateCapabilityAdmission(CapabilityDependencyManifest requirements, string workspaceId, DateTimeOffset admittedAtUtc)
    {
        _ = CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _);
        var pins = requirements.Required.Select(dependency =>
        {
            var descriptor = BuiltInCapabilityCatalog.Descriptors.SingleOrDefault(item => item.Id.Equals(dependency.CapabilityId));
            if (descriptor is null
                || !CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _))
            {
                throw new InvalidOperationException("The browser Human Input fixture capability descriptor is unavailable.");
            }

            return new CapabilityAdmissionPin(identity!, descriptor.Kind, descriptor.Implementation, descriptor.Provenance, new CapabilityDependencyArtifactMetadata(null, null), descriptor.Purpose);
        }).ToArray();
        var evidence = requirements.Required.Select(dependency =>
        {
            var pin = pins.Single(item => item.DescriptorIdentity.Id.Equals(dependency.CapabilityId));
            return new CapabilityAdmissionEvidence(requirements.SubjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, false, "Selected", pin.DescriptorIdentity, "Selected exact browser test capability pin.");
        }).ToArray();
        return new CapabilityAdmissionSnapshot(1, workspaceId, requirements, requirementsHash!.Value, pins, evidence, admittedAtUtc);
    }

    private static GovernedLoopGraphRevisionArtifact CreateArtifact(ContextualRoleRevision role, DateTimeOffset now)
    {
        var configuration = new GovernedLoopHumanInputNodeConfiguration(1, "text", "Collect one bounded browser response.", "Provide one bounded response.", new HumanInputResponseSchema(HumanInputResponseKind.Text, 128, null, null, null), HumanInputPrivacyClass.Private, [new HumanInputEligibleRespondent(WorkspaceActors.Web, "web-respondent", "web")], new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null), "timeout-policy-one@revision-one", "failure-policy-one@revision-one");
        var nodes = new GovernedLoopNodeDefinition[]
        {
            Trigger("trigger"),
            new GovernedLoopNodeDefinition(NodeId, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion), [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>(), null, null, null, configuration),
            Exit("exit"),
        };
        var edges = new GovernedLoopControlEdgeDefinition[]
        {
            new("trigger-to-human-input", "trigger", NodeId, GovernedLoopControlCondition.Always),
            new("human-input-to-exit", NodeId, "exit", GovernedLoopControlCondition.Success),
        };
        var graph = GovernedLoopGraphDefinition.Create(1, GraphId, RevisionId, "Park one exact durable browser Human Input request.", new ContextualRoleRevisionPin(role.Identity, role.ContentHash), "trigger", ["exit"], GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]), [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)], nodes, edges, [new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")], new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]), new GovernedLoopDisplayMetadata("Browser Human Input graph", "The fixture uses only the canonical input gate.", nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()), DefaultRoutingPolicy());
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "browser-human-input-create", ActorId, now);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    private static GovernedLoopNodeDefinition Trigger(string id)
        => new(id, GovernedLoopSequentialNodeDescriptors.ManualTrigger, [new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true), new GovernedLoopPortDefinition("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, "text", true)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition Exit(string id)
        => new(id, GovernedLoopSequentialNodeDescriptors.SuccessExit, [new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true), new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)], GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]), new Dictionary<string, string>());

    private static GovernedModelRoutingPolicy DefaultRoutingPolicy()
    {
        if (!CapabilityId.TryParse("org.embodysense/model-profile/codex", out var profileId, out _)
            || !CapabilityDataClass.TryParse("public", out var publicData, out _))
        {
            throw new InvalidOperationException("The browser Human Input fixture routing identity is invalid.");
        }

        var privacy = GovernedModelPrivacyRequirement.Create(1, true, CapabilityEgressMode.None, [], [publicData!], ["local"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited);
        var unbounded = GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelMonetaryLimit.Unbounded);
        return GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(profileId!), [], GovernedModelProfileRequirements.Create(1, [GovernedModelModality.Text], [], 1, 1, privacy, GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded)));
    }

    private static ContextualRoleRevision CreateRole(string workspaceId, DateTimeOffset now)
        => ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(1, new ContextualRoleRevisionIdentity(RoleId, 1), string.Empty, "Browser Human Input role", "Test-only role for exact server-owned browser Human Input authority.", ContextualRoleStatus.Published, new ContextualRoleProvenance("browser-e2e", now, now), new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)), new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role", ContextualRoleInstructionClassification.RoleInstruction), new ContextualRolePolicyMaxima(ImmutableArray.Create(ConversationTurnCapabilityId))));

    private static CustomLoopRunEvent CreateAdmittedEvent(GovernedLoopSequentialAdapterBinding binding, DateTimeOffset now)
    {
        var runEvent = new CustomLoopRunEvent(1, "browser-human-input-admitted", now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(1, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, binding.WorkspaceId, binding.ExecutionBinding.RunId, binding.ExecutionBinding.Revision, binding.ExecutionBinding.ExecutionGeneration, 0, 1, "trigger", 1, null, null, GovernedLoopControlCondition.Always, ["trigger-to-human-input"], [], null, null, CustomLoopSequentialNodeDisposition.Completed, CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent), string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static GovernedLoopHumanInputWaitingCheckpoint CreateCheckpoint(GovernedLoopSequentialAdapterBinding binding, CustomLoopRunRecord run, GovernedLoopSequentialPlanNode node, GovernedLoopNodeExecutionEvidence activation, GovernedLoopFrontierPosture frontier, GovernedLoopHumanInputNodeConfiguration configuration, string requestId, DateTimeOffset resolvedAtUtc)
    {
        var timeout = HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "timeout-policy-one", "revision-one", HumanInputPolicyKind.ResponseWindow, binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, run.AdmissionActor, 3_600_000, HumanInputTerminalDisposition.Unknown, string.Empty));
        var failure = HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "failure-policy-one", "revision-one", HumanInputPolicyKind.DeadlineDisposition, binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, run.AdmissionActor, null, HumanInputTerminalDisposition.Expired, string.Empty));
        var resolution = HumanInputPolicyResolutionSnapshot.TryCreate(binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, binding.ExecutionBinding.Revision.RevisionId, node.NodeId, run.AdmissionActor, timeout, failure, resolvedAtUtc)
            ?? throw new InvalidOperationException("The browser Human Input fixture policy resolution was invalid.");
        var request = HumanInputRequestHash.Apply(new HumanInputRequest(1, requestId, requestId + "-v1", new HumanInputRequestBinding(binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, binding.ExecutionBinding.Revision.RevisionId, node.NodeId, run.Id, CheckpointId), configuration.Purpose!, configuration.Prompt!, configuration.ResponseSchema!, configuration.PrivacyClass, configuration.EligibleRespondents!.Select(item => item!).ToArray(), new HumanInputTiming(resolution.ResolvedAtUtc, resolution.ExpiresAtUtc), configuration.ResponsePolicy!, new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, node.NodeId, CheckpointId), string.Empty));
        var published = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(1, 1, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, resolvedAtUtc, null, null, null, null, null, string.Empty, string.Empty));
        return GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, new GovernedLoopHumanInputWaitingCheckpointBinding(1, binding.WorkspaceId, binding.ExecutionBinding, binding.AdmissionReceipt.Intent.Publication, binding.GraphArtifactHash, binding.GraphLayoutHash, binding.AdmissionReceiptHash, frontier.Payload.FrontierVersion, frontier.Payload.ContentHash, activation.ActivationOrdinal, activation.CycleId, activation.CycleIteration, node.NodeId, activation.VisitOrdinal, CheckpointId), configuration, resolution, request, GovernedLoopHumanInputWaitingCheckpointPosture.Pending, [published], string.Empty));
    }

    private static string RequestIdFragment(string workspaceId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId)))[..16].ToLowerInvariant();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AuthorityActorId ParseActor(string value)
        => AuthorityActorId.TryParse(value, out var actor, out _) ? actor! : throw new InvalidOperationException("The browser Human Input fixture actor is invalid.");

    private static AuthorityPurpose ParsePurpose(string value)
        => AuthorityPurpose.TryParse(value, out var purpose, out _) ? purpose! : throw new InvalidOperationException("The browser Human Input fixture purpose is invalid.");
}
