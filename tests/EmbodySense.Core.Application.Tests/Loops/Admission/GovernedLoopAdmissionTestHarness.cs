using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Admission;

internal sealed class GovernedLoopAdmissionTestHarness :
    IGovernedLoopAdmissionStore,
    IGovernedLoopGraphRevisionStore,
    IGovernedLoopGrantBindingSource,
    IAuthorityGrantRoleSource,
    IAuthorityGrantResolver,
    ICapabilityAdmissionService,
    ICapabilityAuthorityTransaction,
    IGovernedLoopAdmissionRunIdentityGenerator
{
    private int _fenceDepth;

    private GovernedLoopAdmissionTestHarness(
        bool includeCapability,
        DateTimeOffset? roleRecordedAtUtc,
        AuthorityGrantCompletionConstraintKind completionConstraint,
        DateTimeOffset? grantEffectiveAtUtc,
        DateTimeOffset? grantExpiresAtUtc)
    {
        var role = AuthorityGrantApplicationTestFixture.Role(capabilityIds: includeCapability ? null : []);
        Role = roleRecordedAtUtc is null
            ? role
            : ContextualRoleRevisionContentHash.Apply(role with
            {
                ContentHash = string.Empty,
                Provenance = role.Provenance with { RecordedAtUtc = roleRecordedAtUtc.Value },
            });
        RolePin = new ContextualRoleRevisionPin(Role.Identity, Role.ContentHash);
        Artifact = AuthorityGrantApplicationTestFixture.GraphArtifact(RolePin, includeCapability ? null : []);
        Publication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            Artifact.RevisionArtifact.Revision,
            "publish-loop",
            AuthorityGrantApplicationTestFixture.Hash64('7'));
        EffectiveCeiling = includeCapability
            ? AuthorityGrantApplicationTestFixture.Ceiling()
            : AuthorityCeilingIntersection.EmptyCeiling();
        var profile = AuthorityGrantApplicationTestFixture.Profile(ceiling: EffectiveCeiling);
        var binding = new AuthorityGrantBinding(
            new AuthorityGrantProfilePin(
                new AuthorityProfileReference(profile.ProfileId, profile.Revision),
                AuthorityGrantApplicationTestFixture.ProfileHash(profile)),
            RolePin,
            Publication);
        Grant = AuthorityGrantApplicationTestFixture.Grant(
            binding: binding,
            ceiling: EffectiveCeiling,
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: grantEffectiveAtUtc,
                expires: grantExpiresAtUtc,
                completionConstraint: completionConstraint));
        GrantReference = new AuthorityGrantReference(Grant.GrantId, Grant.Revision, Grant.ContentHash);
        Request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            "admit-loop-1",
            AuthorityGrantApplicationTestFixture.Hash64('1'),
            string.Empty,
            Publication,
            GrantReference,
            AuthorityGrantApplicationTestFixture.Actor(),
            "web"));
        GraphReadResult = new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Ready, 1, Artifact);
        BindingResolution = new GovernedLoopGrantBindingResolution(
            AuthorityGrantDependencyStatus.Active,
            Publication,
            Artifact,
            RolePin,
            Artifact.Graph.AuthorityCeiling.CapabilityIds,
            AuthorityGrantApplicationTestFixture.Hash64('2'));
        RoleResolution = new AuthorityGrantRoleResolution(
            AuthorityGrantDependencyStatus.Active,
            RolePin,
            Role,
            AuthorityGrantApplicationTestFixture.RoleLifecycle(Role),
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            ContextualRoleInstructionSourceProbeStatus.Ready,
            AuthorityGrantApplicationTestFixture.Hash64('3'));
        GrantResolution = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            GrantReference,
            Grant,
            EffectiveCeiling,
            AuthorityGrantApplicationTestFixture.Hash64('4'),
            AuthorityGrantApplicationTestFixture.Now);
        StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, 1, null);
        CapabilityResult = new CapabilityAdmissionResult(false, null, "No test capability result configured.");
    }

    internal static GovernedLoopAdmissionTestHarness Create(
        bool includeCapability = false,
        DateTimeOffset? roleRecordedAtUtc = null,
        AuthorityGrantCompletionConstraintKind completionConstraint = AuthorityGrantCompletionConstraintKind.None,
        DateTimeOffset? grantEffectiveAtUtc = null,
        DateTimeOffset? grantExpiresAtUtc = null)
        => new(includeCapability, roleRecordedAtUtc, completionConstraint, grantEffectiveAtUtc, grantExpiresAtUtc);

    internal ContextualRoleRevision Role { get; }

    internal ContextualRoleRevisionPin RolePin { get; }

    internal GovernedLoopGraphRevisionArtifact Artifact { get; }

    internal GovernedLoopRevisionPublicationPin Publication { get; }

    internal AuthorityGrant Grant { get; }

    internal AuthorityGrantReference GrantReference { get; }

    internal AuthorityCeiling EffectiveCeiling { get; }

    internal GovernedLoopAdmissionRequest Request { get; }

    internal GovernedLoopAdmissionStoreReadResult StoreReadResult { get; set; }

    internal Queue<GovernedLoopAdmissionStoreReadResult> StoreReadResults { get; } = new();

    internal Func<int, GovernedLoopAdmissionStoreReadResult>? StoreReadResultFactory { get; set; }

    internal Action<int>? AfterStoreRead { get; set; }

    internal Action<string>? AfterMutableRead { get; set; }

    internal GovernedLoopGraphRevisionArtifactReadResult GraphReadResult { get; set; }

    internal GovernedLoopGrantBindingResolution BindingResolution { get; set; }

    internal AuthorityGrantRoleResolution RoleResolution { get; set; }

    internal AuthorityGrantResolution GrantResolution { get; set; }

    internal CapabilityAdmissionResult CapabilityResult { get; set; }

    internal Func<CapabilityDependencyManifest, IReadOnlyCollection<CapabilityId>, CapabilityAdmissionResult>? CapabilityResultFactory { get; set; }

    internal Queue<GovernedLoopAdmissionStoreCommitResult> CommitResults { get; } = new();

    internal Queue<Exception> CommitExceptions { get; } = new();

    internal Func<GovernedLoopAdmissionStoreMutation, GovernedLoopAdmissionStoreCommitResult>? CommitResultFactory { get; set; }

    internal bool ThrowMutableReads { get; set; }

    internal bool ThrowRunIdentityGeneration { get; set; }

    internal int StoreReadCount { get; private set; }

    internal int GraphReadCount { get; private set; }

    internal int BindingReadCount { get; private set; }

    internal int RoleReadCount { get; private set; }

    internal int GrantReadCount { get; private set; }

    internal int CapabilityAdmissionCount { get; private set; }

    internal int FenceExecutionCount { get; private set; }

    internal int RunIdentityGenerationCount { get; private set; }

    internal int CommitCount { get; private set; }

    internal bool CommitObservedInsideFence { get; private set; }

    internal CapabilityDependencyManifest? LastRequirements { get; private set; }

    internal IReadOnlyCollection<CapabilityId>? LastAllowedCapabilityIds { get; private set; }

    internal GovernedLoopAdmissionStoreMutation? LastMutation { get; private set; }

    internal IGovernedLoopAdmissionService CreateService()
        => new GovernedLoopAdmissionService(
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            this,
            this,
            this,
            this,
            this,
            this,
            this,
            this,
            new GovernedLoopAdmissionTestTimeProvider(AuthorityGrantApplicationTestFixture.Now.AddMinutes(1)));

    public Task<GovernedLoopAdmissionStoreReadResult> ReadByOperationAsync(string workspaceId, string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoreReadCount++;
        var result = StoreReadResultFactory?.Invoke(StoreReadCount)
            ?? (StoreReadResults.Count > 0 ? StoreReadResults.Dequeue() : StoreReadResult);
        AfterStoreRead?.Invoke(StoreReadCount);
        return Task.FromResult(result);
    }

    public Task<GovernedLoopAdmissionStoreCommitResult> CommitAsync(GovernedLoopAdmissionStoreMutation mutation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitCount++;
        CommitObservedInsideFence |= _fenceDepth > 0;
        LastMutation = mutation;
        if (CommitExceptions.Count > 0)
        {
            throw CommitExceptions.Dequeue();
        }

        var result = CommitResultFactory?.Invoke(mutation)
            ?? (CommitResults.Count > 0
                ? CommitResults.Dequeue()
                : new GovernedLoopAdmissionStoreCommitResult(GovernedLoopAdmissionStoreCommitStatus.Committed, mutation.ExpectedStoreGeneration + 1, mutation.Outcome));
        return Task.FromResult(result);
    }

    public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(GovernedLoopRevisionReference revision, CancellationToken cancellationToken = default)
    {
        MutableRead(cancellationToken);
        GraphReadCount++;
        AfterMutableRead?.Invoke("graph");
        return Task.FromResult(GraphReadResult);
    }

    public Task<GovernedLoopGrantBindingResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
    {
        MutableRead(cancellationToken);
        BindingReadCount++;
        AfterMutableRead?.Invoke("binding");
        return Task.FromResult(BindingResolution);
    }

    public Task<AuthorityGrantRoleResolution> ResolveAsync(ContextualRoleRevisionPin? pin, CancellationToken cancellationToken = default)
    {
        MutableRead(cancellationToken);
        RoleReadCount++;
        AfterMutableRead?.Invoke("role");
        return Task.FromResult(RoleResolution);
    }

    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        MutableRead(cancellationToken);
        GrantReadCount++;
        AfterMutableRead?.Invoke("grant");
        return Task.FromResult(GrantResolution);
    }

    public Task<CapabilityAdmissionResult> AdmitAsync(
        CapabilityDependencyManifest requirements,
        IReadOnlyCollection<CapabilityId> allowedCapabilityIds,
        CancellationToken cancellationToken = default)
    {
        MutableRead(cancellationToken);
        CapabilityAdmissionCount++;
        LastRequirements = requirements;
        LastAllowedCapabilityIds = allowedCapabilityIds.ToArray();
        AfterMutableRead?.Invoke("capability");
        return Task.FromResult(CapabilityResultFactory?.Invoke(requirements, allowedCapabilityIds) ?? CapabilityResult);
    }

    public Task<CapabilityRevalidationResult> RevalidateAsync(
        CapabilityAdmissionSnapshot snapshot,
        IReadOnlyCollection<CapabilityId> allowedCapabilityIds,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FenceExecutionCount++;
        _fenceDepth++;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _fenceDepth--;
        }
    }

    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
        Func<CancellationToken, Task<bool>> validator,
        CancellationToken cancellationToken = default)
    {
        await validator(cancellationToken);
        return null;
    }

    public string CreateRunId()
    {
        RunIdentityGenerationCount++;
        if (ThrowRunIdentityGeneration)
        {
            throw new InvalidOperationException("Run identity generation must not occur.");
        }

        return $"run-admission-{RunIdentityGenerationCount}";
    }

    public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(
        GovernedLoopGraphRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    private void MutableRead(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowMutableReads)
        {
            throw new InvalidOperationException("Mutable dependencies must not be read during historical replay.");
        }
    }
}
