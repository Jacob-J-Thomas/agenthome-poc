using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Exposes one bounded surface-neutral projection over the canonical Human Input ledger and lifecycle services.</summary>
/// <remarks>Reads always remain available through the one authenticated canonical store. Mutations require an explicit
/// server-owned provider; callers may submit only stable operation identity, exact optimistic request state, untrusted response
/// data, and an opaque candidate selector. Actor, role, workspace, time, request binding, routing, grant, and authority evidence
/// are composed or resolved server-side and are never accepted from an interface payload.</remarks>
public sealed class HumanInputRuntimeFacade
{
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly IHumanInputRequestCatalog _catalog;
    private readonly IHumanInputRequestLifecycleStore _lifecycleStore;
    private readonly IAgentRuntimeHumanInputAuthorityProvider? _provider;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IHumanInputResponseLifecycleStore _responseStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspaceId;

    internal HumanInputRuntimeFacade(
        string workspaceId,
        IHumanInputRequestCatalog catalog,
        IHumanInputRequestLifecycleStore lifecycleStore,
        IHumanInputResponseLifecycleStore responseStore,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider timeProvider,
        IAgentRuntimeHumanInputAuthorityProvider? provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _workspaceId = workspaceId;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _lifecycleStore = lifecycleStore ?? throw new ArgumentNullException(nameof(lifecycleStore));
        _responseStore = responseStore ?? throw new ArgumentNullException(nameof(responseStore));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _provider = provider;
    }

    /// <summary>Reads the first bounded canonical Human Input posture page.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The canonical redacted posture page.</returns>
    public Task<HumanInputRequestPosturePage> ListAsync(CancellationToken cancellationToken = default)
        => ListAsync(new HumanInputRequestPosturePageRequest(50), cancellationToken);

    /// <summary>Reads one bounded canonical Human Input posture page.</summary>
    /// <param name="request">The finite page bound and opaque cursor.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The canonical redacted posture page.</returns>
    public async Task<HumanInputRequestPosturePage> ListAsync(
        HumanInputRequestPosturePageRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return new HumanInputRequestPosturePage(HumanInputRequestPosturePageStatus.Invalid, 0, [], null);
        }

        var page = await _catalog.ListAsync(
            new HumanInputRequestCatalogPageRequest(request.MaximumCount, request.Cursor),
            cancellationToken).ConfigureAwait(false);
        if (page.Status != HumanInputRequestCatalogPageStatus.Ready)
        {
            return new HumanInputRequestPosturePage(MapPageStatus(page.Status), page.StoreGeneration, [], null);
        }

        var projections = page.Entries.Select(MapPosture).ToArray();
        return new HumanInputRequestPosturePage(
            HumanInputRequestPosturePageStatus.Ready,
            page.StoreGeneration,
            Array.AsReadOnly(projections),
            page.NextCursor);
    }

    /// <summary>Reads one exact canonical Human Input posture without following request lineage.</summary>
    /// <param name="requestId">The exact stable request identifier.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The exact redacted posture or fail-closed result.</returns>
    public async Task<HumanInputRequestPostureReadResult> ReadAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var result = await _catalog.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
        return new HumanInputRequestPostureReadResult(
            MapReadStatus(result.Status),
            result.StoreGeneration,
            result.Entry is null ? null : MapPosture(result.Entry));
    }

    /// <summary>Submits one exact lifecycle operation through canonical persistence and server-owned authority.</summary>
    /// <param name="input">The authority-free interface operation intent.</param>
    /// <param name="cancellationToken">A token that cancels before durable lifecycle intent begins.</param>
    /// <returns>The canonical redacted lifecycle operation result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before durable intent begins.</exception>
    public async Task<HumanInputOperationResult> SubmitLifecycleAsync(
        HumanInputLifecycleOperationInput? input,
        CancellationToken cancellationToken = default)
    {
        if (input is null || !AuthorityPurpose.TryParse(input.Reason, out var reason, out _))
        {
            return Invalid(input?.OperationId);
        }

        if (_provider is null)
        {
            return Unavailable(input.OperationId);
        }

        AgentRuntimeHumanInputLifecycleTerms terms;
        try
        {
            terms = await _provider.ResolveLifecycleTermsAsync(
                new AgentRuntimeHumanInputLifecycleTermsRequest(
                    input.OperationId,
                    input.Kind,
                    input.RequestId,
                    input.ExpectedLifecycleVersion,
                    input.ExpectedLifecycleStatus,
                    input.ExpectedRequest,
                    input.CandidateKey,
                    input.Reason),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(input.OperationId);
        }

        if (terms is null || terms.Status != AgentRuntimeHumanInputAuthorityStatus.Ready
            || RequiresCandidate(input.Kind) != (terms.CandidateRequest is not null)
            || RequiresGrant(input.Kind) != (terms.GrantReference is not null))
        {
            return terms?.Status == AgentRuntimeHumanInputAuthorityStatus.Denied
                ? Denied(input.OperationId)
                : Unavailable(input.OperationId);
        }

        var expectedBinding = await ResolveExpectedBindingAsync(input.RequestId, input.ExpectedRequest, input.Kind, cancellationToken).ConfigureAwait(false);
        if (input.Kind != HumanInputRequestLifecycleOperationKind.Create && expectedBinding is null)
        {
            return new HumanInputOperationResult(HumanInputOperationStatus.Conflict, input.OperationId, null, null, []);
        }

        HumanInputRequestLifecycleCommand command;
        try
        {
            command = HumanInputRequestLifecycleCommandHash.Apply(new HumanInputRequestLifecycleCommand(
                HumanInputRequestLifecycleCommand.CurrentSchemaVersion,
                input.OperationId,
                input.Kind,
                input.RequestId,
                input.ExpectedLifecycleVersion,
                input.ExpectedLifecycleStatus,
                input.ExpectedRequest,
                expectedBinding,
                terms.CandidateRequest,
                terms.GrantReference,
                reason!,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return Invalid(input.OperationId);
        }

        var service = new HumanInputRequestLifecycleService(
            _lifecycleStore,
            new AgentRuntimeHumanInputLifecycleActorAuthorizer(_provider),
            _grantResolver,
            _authorityTransaction,
            _workspaceId,
            _timeProvider);
        var result = await service.MutateAsync(command, cancellationToken).ConfigureAwait(false);
        return await MapLifecycleResultAsync(result, input.RequestId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Submits one exact response operation through canonical persistence and server-owned authentication.</summary>
    /// <param name="input">The authority-free interface response intent.</param>
    /// <param name="cancellationToken">A token that cancels before durable response intent begins.</param>
    /// <returns>The canonical redacted response operation result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before durable intent begins.</exception>
    public async Task<HumanInputOperationResult> SubmitResponseAsync(
        HumanInputResponseOperationInput? input,
        CancellationToken cancellationToken = default)
    {
        if (input is null || input.ExpectedRequest is null)
        {
            return Invalid(input?.OperationId);
        }

        if (_provider is null)
        {
            return Unavailable(input.OperationId);
        }

        var observed = await _responseStore.ReadAsync(input.ExpectedRequest, cancellationToken).ConfigureAwait(false);
        if (observed.Status != HumanInputResponseLifecycleStoreReadStatus.Ready || observed.Snapshot is null)
        {
            return MapResponseReadFailure(input.OperationId, observed.Status);
        }

        var expectedRequest = observed.Snapshot.Request.RequestVersions.SingleOrDefault(
            request => Matches(request, input.ExpectedRequest));
        if (expectedRequest is null || !string.Equals(input.RequestId, input.ExpectedRequest.RequestId, StringComparison.Ordinal))
        {
            return new HumanInputOperationResult(HumanInputOperationStatus.Conflict, input.OperationId, null, null, []);
        }

        var targets = ImmutableArray<HumanInputResponseReference>.Empty;
        if (input.Kind is HumanInputResponseOperationKind.Withdraw or HumanInputResponseOperationKind.Select)
        {
            var target = observed.Snapshot.Responses.SingleOrDefault(
                response => string.Equals(response.ResponseId, input.ResponseId, StringComparison.Ordinal));
            if (target is null)
            {
                return new HumanInputOperationResult(HumanInputOperationStatus.NotFound, input.OperationId, null, null, []);
            }

            if (!HumanInputResponseReference.TryCreate(expectedRequest, target, out var targetReference, out _)
                || targetReference is null)
            {
                return new HumanInputOperationResult(HumanInputOperationStatus.Ambiguous, input.OperationId, null, null, []);
            }

            targets = ImmutableArray.Create(targetReference);
        }

        HumanInputResponseLifecycleCommand command;
        try
        {
            command = HumanInputResponseLifecycleCommandHash.Apply(new HumanInputResponseLifecycleCommand(
                HumanInputResponseLifecycleCommand.CurrentSchemaVersion,
                input.OperationId,
                input.Kind,
                input.RequestId,
                input.ExpectedLifecycleVersion,
                input.ExpectedLifecycleStatus,
                input.ExpectedRequest,
                expectedRequest.Binding,
                input.Kind == HumanInputResponseOperationKind.Submit ? input.ResponseId : null,
                input.Kind == HumanInputResponseOperationKind.Submit ? input.Value : null,
                input.Kind == HumanInputResponseOperationKind.Submit ? input.Explanation : null,
                targets,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return Invalid(input.OperationId);
        }

        var service = new HumanInputResponseLifecycleService(
            _responseStore,
            new AgentRuntimeHumanInputResponseActorAuthenticator(_provider),
            _authorityTransaction,
            _workspaceId,
            _timeProvider);
        var result = await service.MutateAsync(command, cancellationToken).ConfigureAwait(false);
        return await MapResponseResultAsync(result, input.RequestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanInputRequestBinding?> ResolveExpectedBindingAsync(
        string requestId,
        HumanInputRequestReference? expectedRequest,
        HumanInputRequestLifecycleOperationKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == HumanInputRequestLifecycleOperationKind.Create)
        {
            return null;
        }

        if (expectedRequest is null || !string.Equals(requestId, expectedRequest.RequestId, StringComparison.Ordinal))
        {
            return null;
        }

        var observed = await _catalog.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
        return observed.Entry?.Lifecycle.RequestVersions.SingleOrDefault(request => Matches(request, expectedRequest))?.Binding;
    }

    private async Task<HumanInputOperationResult> MapLifecycleResultAsync(
        HumanInputRequestLifecycleMutationResult result,
        string requestId,
        CancellationToken cancellationToken)
    {
        var posture = await TryReadPostureAsync(requestId, cancellationToken).ConfigureAwait(false);
        var evidence = result.Proof is null
            ? null
            : new HumanInputOperationEvidence(
                result.Proof.OperationId,
                result.Proof.Kind.ToString(),
                result.Proof.Outcome.ToString(),
                result.Proof.FailureCode.ToString(),
                result.Proof.TargetRequestId,
                result.Proof.PreviousLifecycleVersion,
                result.Proof.ResultLifecycleVersion,
                result.Proof.RecordedAtUtc);
        var errors = result.ValidationErrors.Select(error => new HumanInputOperationValidationError(
            error.Code.ToString(), error.Path, error.Message)).ToArray();
        return new HumanInputOperationResult(
            MapLifecycleStatus(result.Status),
            result.OperationId,
            evidence,
            posture,
            Array.AsReadOnly(errors));
    }

    private async Task<HumanInputOperationResult> MapResponseResultAsync(
        HumanInputResponseLifecycleMutationResult result,
        string requestId,
        CancellationToken cancellationToken)
    {
        var posture = await TryReadPostureAsync(requestId, cancellationToken).ConfigureAwait(false);
        var evidence = result.Operation is null
            ? null
            : new HumanInputOperationEvidence(
                result.Operation.OperationId,
                result.Operation.Kind.ToString(),
                result.Operation.Outcome.ToString(),
                result.Operation.FailureCode.ToString(),
                result.Operation.RequestId,
                result.Operation.PreviousLifecycleVersion,
                result.Operation.ResultLifecycleVersion,
                result.Operation.RecordedAtUtc);
        var errors = result.ValidationErrors.Select(error => new HumanInputOperationValidationError(
            error.Code.ToString(), error.Path, error.Message)).ToArray();
        return new HumanInputOperationResult(
            MapResponseStatus(result.Status),
            result.OperationId,
            evidence,
            posture,
            Array.AsReadOnly(errors));
    }

    private async Task<HumanInputRequestPosture?> TryReadPostureAsync(string requestId, CancellationToken cancellationToken)
    {
        var current = await _catalog.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
        return current.Status == HumanInputRequestCatalogReadStatus.Ready && current.Entry is not null
            ? MapPosture(current.Entry)
            : null;
    }

    private static HumanInputRequestPosture MapPosture(HumanInputRequestCatalogEntry entry)
    {
        var head = entry.Lifecycle.Head;
        var request = entry.Lifecycle.RequestVersions.Single(requestVersion => Matches(requestVersion, head.CurrentRequest));
        var withdrawn = entry.Responses.Operations
            .Where(operation => operation.Outcome == HumanInputResponseOperationOutcome.Committed
                && operation.Kind == HumanInputResponseOperationKind.Withdraw)
            .SelectMany(operation => operation.TargetResponses)
            .Select(reference => reference.ResponseId)
            .ToHashSet(StringComparer.Ordinal);
        var accepted = entry.Responses.Responses.Count;
        var active = entry.Responses.Responses.Count(response => !withdrawn.Contains(response.ResponseId));
        return new HumanInputRequestPosture(
            head.SchemaVersion,
            head.RequestId,
            head.LifecycleVersion,
            head.Status,
            head.CurrentRequest with { },
            new HumanInputRequestPresentation(
                request.RequestVersionId,
                request.RequestHash,
                request.Purpose,
                request.Prompt,
                request.ResponseSchema,
                request.PrivacyClass,
                request.Timing,
                request.ResponsePolicy.Kind,
                request.ResponsePolicy.RequiredResponseCount),
            head.ReminderCount,
            head.SupersedesRequestId,
            head.SupersededByRequestId,
            head.UpdatedAtUtc,
            accepted,
            active,
            accepted - active,
            head.Status == HumanInputRequestLifecycleStatus.Answered,
            LatestConflict(entry));
    }

    private static HumanInputRequestConflict? LatestConflict(HumanInputRequestCatalogEntry entry)
    {
        var lifecycle = entry.Lifecycle.Operations
            .Where(operation => operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Conflict)
            .Select(operation => new HumanInputRequestConflict(
                operation.OperationId,
                "Lifecycle",
                operation.Kind.ToString(),
                operation.FailureCode.ToString(),
                operation.RecordedAtUtc));
        var responses = entry.Responses.Operations
            .Where(operation => operation.Outcome == HumanInputResponseOperationOutcome.Conflict)
            .Select(operation => new HumanInputRequestConflict(
                operation.OperationId,
                "Response",
                operation.Kind.ToString(),
                operation.FailureCode.ToString(),
                operation.RecordedAtUtc));
        return lifecycle
            .Concat(responses)
            .OrderByDescending(conflict => conflict.RecordedAtUtc)
            .ThenByDescending(conflict => conflict.OperationId, StringComparer.Ordinal)
            .ThenByDescending(conflict => conflict.OperationFamily, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool Matches(HumanInputRequest request, HumanInputRequestReference reference)
        => request.SchemaVersion == reference.SchemaVersion
            && string.Equals(request.RequestId, reference.RequestId, StringComparison.Ordinal)
            && string.Equals(request.RequestVersionId, reference.RequestVersionId, StringComparison.Ordinal)
            && string.Equals(request.RequestHash, reference.RequestHash, StringComparison.Ordinal);

    private static bool RequiresCandidate(HumanInputRequestLifecycleOperationKind kind)
        => kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;

    private static bool RequiresGrant(HumanInputRequestLifecycleOperationKind kind)
        => kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Remind
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;

    private static HumanInputRequestPosturePageStatus MapPageStatus(HumanInputRequestCatalogPageStatus status)
        => status switch
        {
            HumanInputRequestCatalogPageStatus.Ready => HumanInputRequestPosturePageStatus.Ready,
            HumanInputRequestCatalogPageStatus.Invalid => HumanInputRequestPosturePageStatus.Invalid,
            HumanInputRequestCatalogPageStatus.Stale => HumanInputRequestPosturePageStatus.Stale,
            HumanInputRequestCatalogPageStatus.Unavailable => HumanInputRequestPosturePageStatus.Unavailable,
            _ => HumanInputRequestPosturePageStatus.Ambiguous,
        };

    private static HumanInputRequestPostureReadStatus MapReadStatus(HumanInputRequestCatalogReadStatus status)
        => status switch
        {
            HumanInputRequestCatalogReadStatus.Ready => HumanInputRequestPostureReadStatus.Ready,
            HumanInputRequestCatalogReadStatus.Invalid => HumanInputRequestPostureReadStatus.Invalid,
            HumanInputRequestCatalogReadStatus.NotFound => HumanInputRequestPostureReadStatus.NotFound,
            HumanInputRequestCatalogReadStatus.Unavailable => HumanInputRequestPostureReadStatus.Unavailable,
            _ => HumanInputRequestPostureReadStatus.Ambiguous,
        };

    private static HumanInputOperationStatus MapLifecycleStatus(HumanInputRequestLifecycleMutationStatus status)
        => status switch
        {
            HumanInputRequestLifecycleMutationStatus.Committed => HumanInputOperationStatus.Committed,
            HumanInputRequestLifecycleMutationStatus.Replayed => HumanInputOperationStatus.Replayed,
            HumanInputRequestLifecycleMutationStatus.Invalid => HumanInputOperationStatus.Invalid,
            HumanInputRequestLifecycleMutationStatus.Conflict => HumanInputOperationStatus.Conflict,
            HumanInputRequestLifecycleMutationStatus.NotFound => HumanInputOperationStatus.NotFound,
            HumanInputRequestLifecycleMutationStatus.Denied => HumanInputOperationStatus.Denied,
            HumanInputRequestLifecycleMutationStatus.LimitExceeded => HumanInputOperationStatus.LimitExceeded,
            HumanInputRequestLifecycleMutationStatus.GrantUnavailable or HumanInputRequestLifecycleMutationStatus.Unavailable => HumanInputOperationStatus.Unavailable,
            _ => HumanInputOperationStatus.Ambiguous,
        };

    private static HumanInputOperationStatus MapResponseStatus(HumanInputResponseLifecycleMutationStatus status)
        => status switch
        {
            HumanInputResponseLifecycleMutationStatus.Committed => HumanInputOperationStatus.Committed,
            HumanInputResponseLifecycleMutationStatus.Replayed => HumanInputOperationStatus.Replayed,
            HumanInputResponseLifecycleMutationStatus.Invalid => HumanInputOperationStatus.Invalid,
            HumanInputResponseLifecycleMutationStatus.Conflict => HumanInputOperationStatus.Conflict,
            HumanInputResponseLifecycleMutationStatus.NotFound => HumanInputOperationStatus.NotFound,
            HumanInputResponseLifecycleMutationStatus.Denied or HumanInputResponseLifecycleMutationStatus.Ineligible => HumanInputOperationStatus.Denied,
            HumanInputResponseLifecycleMutationStatus.Late => HumanInputOperationStatus.Late,
            HumanInputResponseLifecycleMutationStatus.LimitExceeded => HumanInputOperationStatus.LimitExceeded,
            HumanInputResponseLifecycleMutationStatus.Unavailable => HumanInputOperationStatus.Unavailable,
            _ => HumanInputOperationStatus.Ambiguous,
        };

    private static HumanInputOperationResult MapResponseReadFailure(
        string operationId,
        HumanInputResponseLifecycleStoreReadStatus status)
        => status switch
        {
            HumanInputResponseLifecycleStoreReadStatus.NotFound => new HumanInputOperationResult(HumanInputOperationStatus.NotFound, operationId, null, null, []),
            HumanInputResponseLifecycleStoreReadStatus.Unavailable => Unavailable(operationId),
            _ => new HumanInputOperationResult(HumanInputOperationStatus.Ambiguous, operationId, null, null, []),
        };

    private static HumanInputOperationResult Invalid(string? operationId)
        => new(HumanInputOperationStatus.Invalid, operationId ?? string.Empty, null, null, []);

    private static HumanInputOperationResult Denied(string operationId)
        => new(HumanInputOperationStatus.Denied, operationId, null, null, []);

    private static HumanInputOperationResult Unavailable(string? operationId)
        => new(HumanInputOperationStatus.Unavailable, operationId ?? string.Empty, null, null, []);
}
