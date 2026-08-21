using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Delegation;
using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Tests.Loops.Admission;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

internal sealed class AuthorityDelegationServiceTestHarness :
    IAuthorityGrantResolver,
    IAuthorityDelegationOriginResolver,
    IAuthorityDelegationTargetResolver,
    IAuthorityDelegationCompletionSource,
    ICapabilityAuthorityTransaction
{
    private AuthorityDelegationServiceTestHarness(
        GovernedLoopAdmissionReceipt receipt,
        AuthorityGrant grant,
        DateTimeOffset now)
    {
        Receipt = receipt;
        Grant = grant;
        Time = new FixedAuthorityDelegationTimeProvider(now);
        Target = new AuthorityDelegationTargetBinding(
            AuthorityDelegationTargetKind.Role,
            receipt.Intent.Role,
            null,
            null,
            Hash('6'));
        Boundary = new AuthorityDelegationBoundary(now, now.AddMinutes(30), AuthorityDelegationCompletionConstraintKind.None);
        Assert.True(AuthorityPurpose.TryParse("Delegate one exact bounded operation.", out var purpose, out _));
        Request = new AuthorityDelegationCreateRequest(
            receipt,
            "origin-node",
            1,
            "delegation-operation-1",
            Target,
            receipt.Evidence.EffectiveAuthority,
            [],
            "role-execution",
            "bounded-operation",
            purpose!,
            Boundary);
        GrantResolution = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            receipt.Intent.AuthorityGrant,
            grant,
            receipt.Evidence.EffectiveAuthority,
            receipt.Evidence.GrantDependencyEvidenceHash,
            now,
            grant);
        OriginResolution = CreateOriginResolution(AuthorityDelegationOriginResolutionStatus.Current);
        TargetResolution = CreateTargetResolution(AuthorityDelegationTargetResolutionStatus.Active);
        CompletionResolution = new AuthorityDelegationCompletionResolution(AuthorityDelegationCompletionStatus.Active);
    }

    internal GovernedLoopAdmissionReceipt Receipt { get; private set; }

    internal AuthorityGrant Grant { get; private set; }

    internal AuthorityDelegationTargetBinding Target { get; }

    internal AuthorityDelegationBoundary Boundary { get; }

    internal AuthorityDelegationCreateRequest Request { get; set; }

    internal AuthorityGrantResolution GrantResolution { get; set; }

    internal AuthorityDelegationOriginResolution OriginResolution { get; set; }

    internal AuthorityDelegationTargetResolution TargetResolution { get; set; }

    internal AuthorityDelegationCompletionResolution CompletionResolution { get; set; }

    internal FixedAuthorityDelegationTimeProvider Time { get; }

    internal Func<AuthorityGrantReference?, CancellationToken, Task<AuthorityGrantResolution>>? GrantCallback { get; set; }

    internal Func<AuthorityDelegationCreateRequest, CancellationToken, Task<AuthorityDelegationOriginResolution>>? CreateOriginCallback { get; set; }

    internal Func<AuthorityDelegationUseRequest, CancellationToken, Task<AuthorityDelegationOriginResolution>>? UseOriginCallback { get; set; }

    internal Func<AuthorityDelegationTargetBinding, CancellationToken, Task<AuthorityDelegationTargetResolution>>? TargetCallback { get; set; }

    internal Func<CancellationToken, Task<AuthorityDelegationCompletionResolution>>? CompletionCallback { get; set; }

    internal Func<Func<CancellationToken, Task<AuthorityDelegationServiceResult>>, CancellationToken, Task<AuthorityDelegationServiceResult>>? TransactionCallback { get; set; }

    internal List<string> Calls { get; } = [];

    internal int TransactionCount { get; private set; }

    internal int GrantCount { get; private set; }

    internal int OriginCount { get; private set; }

    internal int TargetCount { get; private set; }

    internal int CompletionCount { get; private set; }

    internal static async Task<AuthorityDelegationServiceTestHarness> CreateAsync()
    {
        var admission = GovernedLoopAdmissionTestHarness.Create();
        var result = await admission.CreateService().AdmitAsync(admission.Request);
        Assert.Equal(GovernedLoopAdmissionStatus.Admitted, result.Status);
        var receipt = Assert.IsType<GovernedLoopAdmissionReceipt>(result.Outcome?.Receipt);
        return new AuthorityDelegationServiceTestHarness(receipt, admission.Grant, receipt.RecordedAtUtc.AddMinutes(1));
    }

    internal IAuthorityDelegationEnvelopeService CreateService()
        => new AuthorityDelegationEnvelopeService(this, this, this, this, this, Time);

    internal AuthorityDelegationOriginResolution CreateOriginResolution(
        AuthorityDelegationOriginResolutionStatus status,
        string? evidenceHash = null,
        AuthorityDelegationTargetBinding? target = null,
        AuthorityCeiling? parentAuthority = null,
        IReadOnlyList<CapabilityAdmissionPin>? parentPins = null,
        AuthorityPurpose? purpose = null)
        => new(
            status,
            Receipt.Intent.WorkspaceId,
            Receipt.Evidence.Binding,
            Request.OriginNodeId,
            Request.OriginNodeAttempt,
            target ?? Request.Target,
            Request.TargetClass,
            Request.OperationClass,
            purpose ?? Request.Purpose,
            Request.Boundary.CompletionConstraint,
            Receipt.Evidence.EffectiveAuthority,
            parentAuthority ?? Receipt.Evidence.EffectiveAuthority,
            parentPins ?? CurrentParentPins(),
            evidenceHash ?? Hash('8'));

    internal AuthorityDelegationTargetResolution CreateTargetResolution(
        AuthorityDelegationTargetResolutionStatus status,
        string? maximumEvidenceHash = null,
        AuthorityDelegationTargetBinding? target = null,
        string? workspaceId = null)
    {
        var capabilityIds = Request.DelegatedCeiling.Capabilities
            .Select(capability => capability.Id.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new AuthorityDelegationTargetResolution(
            status,
            target ?? Request.Target,
            workspaceId ?? Receipt.Intent.WorkspaceId,
            capabilityIds,
            capabilityIds,
            capabilityIds,
            maximumEvidenceHash ?? Hash('7'));
    }

    internal void RebindParentGrant(AuthorityGrant grant)
    {
        var grantReference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        var intent = Receipt.Intent with { AuthorityGrant = grantReference };
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            Receipt.Evidence.SchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            Receipt.Evidence.Binding,
            grant.Binding.Profile,
            grant.Boundary,
            Receipt.Evidence.GrantDependencyEvidenceHash,
            grant.RequestedCeiling,
            Receipt.Evidence.CapabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(
                intent,
                grant.RequestedCeiling,
                Receipt.Evidence.CapabilityAdmission),
            Receipt.Evidence.EvaluatedAtUtc,
            string.Empty));
        Receipt = GovernedLoopAdmissionContractHash.Apply(Receipt with
        {
            Intent = intent,
            Evidence = evidence,
            ContentHash = string.Empty,
        });
        Grant = grant;
        Request = Request with
        {
            ParentAdmission = Receipt,
            DelegatedCeiling = grant.RequestedCeiling,
            DelegatedCapabilityPins = CurrentParentPins(),
        };
        GrantResolution = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            grantReference,
            grant,
            grant.RequestedCeiling,
            Receipt.Evidence.GrantDependencyEvidenceHash,
            Time.UtcNow,
            grant);
        OriginResolution = CreateOriginResolution(AuthorityDelegationOriginResolutionStatus.Current);
        TargetResolution = CreateTargetResolution(AuthorityDelegationTargetResolutionStatus.Active);
    }

    internal AuthorityDelegationEnvelope CreateEnvelopeForCurrentContext()
    {
        var parentPins = CurrentParentPins();
        var parentEvidence = AuthorityDelegationContractHash.Apply(new AuthorityDelegationParentEvidenceReference(
            Receipt.Intent.WorkspaceId,
            Receipt.Evidence.Binding,
            Request.OriginNodeId,
            Request.OriginNodeAttempt,
            Receipt.ContentHash,
            Receipt.Intent.ActorId,
            Receipt.Intent.AuthorityGrant,
            Grant.Binding,
            OriginResolution.EvidenceHash,
            GrantResolution.DependencyEvidenceHash,
            Time.UtcNow,
            string.Empty));
        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            Receipt.Evidence.EffectiveAuthority,
            parentPins,
            TargetResolution.RoleCapabilityIds,
            TargetResolution.LoopCapabilityIds,
            TargetResolution.NodeCapabilityIds,
            Request.DelegatedCeiling,
            Request.DelegatedCapabilityPins,
            parentEvidence.ContentHash,
            TargetResolution.TargetMaximumEvidenceHash);
        Assert.NotNull(proof);
        var revocation = AuthorityDelegationContractHash.Apply(new AuthorityDelegationRevocationLink(
            parentEvidence.GrantReference,
            parentEvidence.ParentAdmissionReceiptHash,
            parentEvidence.WorkspaceId,
            parentEvidence.ParentExecution.RunId,
            parentEvidence.ParentExecution.ExecutionGeneration,
            string.Empty));
        var envelope = AuthorityDelegationContractHash.Apply(new AuthorityDelegationEnvelope(
            AuthorityDelegationEnvelope.CurrentSchemaVersion,
            Request.EnvelopeId,
            parentEvidence,
            Request.Target,
            Request.DelegatedCeiling,
            Request.DelegatedCapabilityPins,
            Request.TargetClass,
            Request.OperationClass,
            Request.Purpose,
            Request.Boundary,
            revocation,
            proof,
            Time.UtcNow,
            string.Empty));
        Assert.True(AuthorityDelegationContractValidator.Validate(envelope).IsValid);
        return envelope;
    }

    private IReadOnlyList<CapabilityAdmissionPin> CurrentParentPins()
        => Receipt.Evidence.CapabilityAdmission.Pins
            .Where(pin => Receipt.Evidence.EffectiveAuthority.Capabilities.Contains(pin.DescriptorIdentity))
            .OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .ThenBy(pin => pin.DescriptorIdentity.Version.Value, StringComparer.Ordinal)
            .ThenBy(pin => pin.DescriptorIdentity.Hash.Value, StringComparer.Ordinal)
            .ToArray();

    internal AuthorityDelegationUseRequest UseRequest(AuthorityDelegationEnvelope envelope)
        => new(
            envelope,
            envelope.ParentEvidence.WorkspaceId,
            envelope.ParentEvidence.ParentExecution,
            envelope.ParentEvidence.OriginNodeId,
            envelope.ParentEvidence.OriginNodeAttempt,
            envelope.Target,
            envelope.TargetClass,
            envelope.OperationClass,
            envelope.Purpose);

    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("grant");
        GrantCount++;
        return GrantCallback?.Invoke(reference, cancellationToken) ?? Task.FromResult(GrantResolution);
    }

    public Task<AuthorityDelegationOriginResolution> ResolveForCreationAsync(AuthorityDelegationCreateRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("origin");
        OriginCount++;
        return CreateOriginCallback?.Invoke(request, cancellationToken) ?? Task.FromResult(OriginResolution);
    }

    public Task<AuthorityDelegationOriginResolution> ResolveForUseAsync(AuthorityDelegationUseRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("origin");
        OriginCount++;
        return UseOriginCallback?.Invoke(request, cancellationToken) ?? Task.FromResult(OriginResolution);
    }

    public Task<AuthorityDelegationTargetResolution> ResolveAsync(AuthorityDelegationTargetBinding target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("target");
        TargetCount++;
        return TargetCallback?.Invoke(target, cancellationToken) ?? Task.FromResult(TargetResolution);
    }

    public Task<AuthorityDelegationCompletionResolution> ResolveAsync(
        string workspaceId,
        GovernedLoopExecutionBinding parentExecution,
        AuthorityDelegationTargetBinding target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("completion");
        CompletionCount++;
        return CompletionCallback?.Invoke(cancellationToken) ?? Task.FromResult(CompletionResolution);
    }

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransactionCount++;
        Calls.Add("transaction");
        if (typeof(TResult) == typeof(AuthorityDelegationServiceResult) && TransactionCallback is not null)
        {
            var result = await TransactionCallback(
                async token =>
                {
                    var value = await operation(token);
                    return (AuthorityDelegationServiceResult)(object)value!;
                },
                cancellationToken);
            return (TResult)(object)result;
        }

        return await operation(cancellationToken);
    }

    public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
        Func<CancellationToken, Task<bool>> validator,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    internal static string Hash(char value) => new(value, AuthorityDelegationContractLimits.Sha256HexCharacters);
}
