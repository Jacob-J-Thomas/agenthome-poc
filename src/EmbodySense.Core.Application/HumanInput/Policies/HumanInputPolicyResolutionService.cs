using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Application.HumanInput.Policies;

/// <summary>Resolves two exact immutable Human Input policies into one trusted-time, checkpoint-bindable snapshot without selecting defaults or granting authority.</summary>
public sealed class HumanInputPolicyResolutionService
{
    private readonly IHumanInputPolicySource _source;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a fail-closed exact policy resolution service.</summary>
    /// <param name="source">The authoritative exact-revision source.</param>
    /// <param name="timeProvider">The trusted server-owned UTC time source.</param>
    public HumanInputPolicyResolutionService(IHumanInputPolicySource source, TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Resolves the exact timeout and failure policy revisions bound by one admitted Human Input configuration.</summary>
    /// <param name="request">The server-derived scope, actor, graph revision, node, and complete configuration.</param>
    /// <param name="cancellationToken">A token that cancels the bounded source lookups.</param>
    /// <returns>A snapshot only when all exact identities, scope, canonical hashes, kinds, and trusted time are proved.</returns>
    public async Task<HumanInputPolicyResolutionResult> ResolveAsync(HumanInputPolicyResolutionRequest? request, CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request)
            || !HumanInputPolicyReference.TryParse(request!.Configuration.TimeoutPolicyReference, out var timeoutReference)
            || !HumanInputPolicyReference.TryParse(request.Configuration.FailurePolicyReference, out var failureReference))
        {
            return Result(HumanInputPolicyResolutionStatus.Invalid);
        }

        HumanInputPolicySourceReadResult timeout;
        HumanInputPolicySourceReadResult failure;
        try
        {
            timeout = await _source.ReadAsync(timeoutReference!, cancellationToken).ConfigureAwait(false);
            failure = await _source.ReadAsync(failureReference!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(HumanInputPolicyResolutionStatus.Unavailable);
        }

        var sourceStatus = SourceStatus(timeout, failure);
        if (sourceStatus is not null) return Result(sourceStatus.Value);
        if (!HumanInputPolicyArtifactValidator.Validate(timeout.Policy).IsValid || !HumanInputPolicyArtifactValidator.Validate(failure.Policy).IsValid) return Result(HumanInputPolicyResolutionStatus.Invalid);
        if (!Equals(timeout.Policy!.Reference, timeoutReference) || !Equals(failure.Policy!.Reference, failureReference)) return Result(HumanInputPolicyResolutionStatus.Divergent);
        if (timeout.Policy.Kind != HumanInputPolicyKind.ResponseWindow || failure.Policy.Kind != HumanInputPolicyKind.DeadlineDisposition) return Result(HumanInputPolicyResolutionStatus.WrongKind);
        if (!MatchesScope(timeout.Policy, request!) || !MatchesScope(failure.Policy, request!)) return Result(HumanInputPolicyResolutionStatus.ScopeMismatch);

        var now = UtcNow();
        if (now == default) return Result(HumanInputPolicyResolutionStatus.Unavailable);
        var snapshot = HumanInputPolicyResolutionSnapshot.TryCreate(request!.WorkspaceId, request.GraphId, request.GraphRevisionId, request.NodeId, request.ActorId, timeout.Policy, failure.Policy, now);
        return snapshot is null ? Result(HumanInputPolicyResolutionStatus.Invalid) : new HumanInputPolicyResolutionResult(HumanInputPolicyResolutionStatus.Resolved, snapshot);
    }

    private static bool IsValidRequest(HumanInputPolicyResolutionRequest? request)
        => request is not null
            && HumanInputIdentifier.IsValid(request.WorkspaceId)
            && HumanInputIdentifier.IsValid(request.GraphId)
            && HumanInputIdentifier.IsValid(request.GraphRevisionId)
            && HumanInputIdentifier.IsValid(request.NodeId)
            && HumanInputIdentifier.IsValid(request.ActorId)
            && GovernedLoopHumanInputNodeConfigurationValidator.IsValid(request.Configuration);

    private static HumanInputPolicyResolutionStatus? SourceStatus(HumanInputPolicySourceReadResult? timeout, HumanInputPolicySourceReadResult? failure)
    {
        if (!IsDefined(timeout) || !IsDefined(failure)) return HumanInputPolicyResolutionStatus.Unavailable;
        if (timeout!.Status == HumanInputPolicySourceReadStatus.NotFound || failure!.Status == HumanInputPolicySourceReadStatus.NotFound) return HumanInputPolicyResolutionStatus.NotFound;
        if (timeout.Status != HumanInputPolicySourceReadStatus.Ready || failure.Status != HumanInputPolicySourceReadStatus.Ready || timeout.Policy is null || failure.Policy is null || timeout.StoreGeneration < 0 || failure.StoreGeneration < 0) return HumanInputPolicyResolutionStatus.Unavailable;
        return null;
    }

    private static bool IsDefined(HumanInputPolicySourceReadResult? result) => result is not null && Enum.IsDefined(result.Status) && result.Status != HumanInputPolicySourceReadStatus.Unknown;

    private static bool MatchesScope(HumanInputPolicyArtifact policy, HumanInputPolicyResolutionRequest request)
        => string.Equals(policy.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(policy.GraphId, request.GraphId, StringComparison.Ordinal)
            && string.Equals(policy.AuthorityActorId, request.ActorId, StringComparison.Ordinal);

    private DateTimeOffset UtcNow()
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            return now != default && now.Offset == TimeSpan.Zero ? now : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static HumanInputPolicyResolutionResult Result(HumanInputPolicyResolutionStatus status) => new(status, null);
}
