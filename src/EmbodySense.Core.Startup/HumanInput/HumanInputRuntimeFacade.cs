using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
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
    private readonly IHumanInputSupersedeCandidatePreparer? _candidatePreparer;

    internal HumanInputRuntimeFacade(
        string workspaceId,
        IHumanInputRequestCatalog catalog,
        IHumanInputRequestLifecycleStore lifecycleStore,
        IHumanInputResponseLifecycleStore responseStore,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider timeProvider,
        IAgentRuntimeHumanInputAuthorityProvider? provider)
        : this(workspaceId, catalog, lifecycleStore, responseStore, grantResolver, authorityTransaction, timeProvider, provider, null)
    {
    }

    internal HumanInputRuntimeFacade(
        string workspaceId,
        IHumanInputRequestCatalog catalog,
        IHumanInputRequestLifecycleStore lifecycleStore,
        IHumanInputResponseLifecycleStore responseStore,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider timeProvider,
        IAgentRuntimeHumanInputAuthorityProvider? provider,
        IHumanInputSupersedeCandidatePreparer? candidatePreparer)
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
        _candidatePreparer = candidatePreparer;
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

    /// <summary>Prepares one exact successor candidate while retaining binding and grant material inside Startup.</summary>
    /// <param name="input">The bounded successor proposal and exact optimistic target terms.</param>
    /// <param name="cancellationToken">A token that cancels preparation before registry retention.</param>
    /// <returns>An opaque candidate key or a fail-closed preparation disposition.</returns>
    public Task<HumanInputSupersedePreparationResult> PrepareSupersedeAsync(HumanInputSupersedePreparationInput? input, CancellationToken cancellationToken = default)
        => _candidatePreparer is null
            ? Task.FromResult(new HumanInputSupersedePreparationResult(HumanInputSupersedePreparationStatus.Unavailable, input?.RequestId ?? string.Empty, null, null, "candidate_preparer_unavailable"))
            : _candidatePreparer.PrepareAsync(input, cancellationToken);

    /// <summary>Prepares bounded opaque server-generated reroute alternatives from canonical respondent routing.</summary>
    /// <param name="input">The exact optimistic request identity and short candidate expiry.</param>
    /// <param name="cancellationToken">A token that cancels before preparation completes.</param>
    /// <returns>Generic opaque options or a value-free fail-closed result.</returns>
    public Task<HumanInputReroutePreparationResult> PrepareRerouteAsync(HumanInputReroutePreparationInput? input, CancellationToken cancellationToken = default)
        => _candidatePreparer is null
            ? Task.FromResult(new HumanInputReroutePreparationResult(HumanInputSupersedePreparationStatus.Unavailable, input?.RequestId ?? string.Empty, [], null, "candidate_preparer_unavailable"))
            : _candidatePreparer.PrepareRerouteAsync(input, cancellationToken);

    /// <summary>Prepares one bounded opaque server-generated amend candidate from canonical request state.</summary>
    /// <param name="input">The exact optimistic request identity and bounded amend terms.</param>
    /// <param name="cancellationToken">A token that cancels before preparation completes.</param>
    /// <returns>An opaque candidate key or a value-free fail-closed result.</returns>
    public Task<HumanInputAmendPreparationResult> PrepareAmendAsync(HumanInputAmendPreparationInput? input, CancellationToken cancellationToken = default)
        => _candidatePreparer is null
            ? Task.FromResult(new HumanInputAmendPreparationResult(HumanInputSupersedePreparationStatus.Unavailable, input?.RequestId ?? string.Empty, null, null, "candidate_preparer_unavailable"))
            : _candidatePreparer.PrepareAmendAsync(input, cancellationToken);

    /// <summary>Submits one Web/other surface lifecycle intent using Startup-owned primitive boundary types.</summary>
    /// <param name="input">The surface operation with no lower-layer or authority types.</param>
    /// <param name="cancellationToken">A token that cancels before durable intent begins.</param>
    /// <returns>The canonical redacted lifecycle outcome.</returns>
    public Task<HumanInputOperationResult> SubmitSurfaceLifecycleAsync(HumanInputSurfaceLifecycleOperationInput? input, CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            return Task.FromResult(Invalid(null));
        }

        if (!TryParseEnum(input.Kind, out HumanInputRequestLifecycleOperationKind kind)
            || !TryParseEnum(input.ExpectedLifecycleStatus, out HumanInputRequestLifecycleStatus status)
            || !HumanInputIdentifier.IsValid(input.OperationId))
        {
            return Task.FromResult(Invalid(input?.OperationId));
        }

        var expected = ToReference(input.ExpectedRequest);
        return SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(input.OperationId, kind, input.RequestId, input.ExpectedLifecycleVersion, status, expected, input.CandidateKey, input.Reason), cancellationToken);
    }

    /// <summary>Submits one Web/other surface response intent using Startup-owned primitive boundary types.</summary>
    /// <param name="input">The surface operation with an untrusted JSON response value.</param>
    /// <param name="cancellationToken">A token that cancels before durable intent begins.</param>
    /// <returns>The canonical redacted response outcome.</returns>
    public Task<HumanInputOperationResult> SubmitSurfaceResponseAsync(HumanInputSurfaceResponseOperationInput? input, CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            return Task.FromResult(Invalid(null));
        }

        if (!TryParseEnum(input.Kind, out HumanInputResponseOperationKind kind)
            || !TryParseEnum(input.ExpectedLifecycleStatus, out HumanInputRequestLifecycleStatus status)
            || input.ExpectedRequest is null
            || !HumanInputIdentifier.IsValid(input.OperationId))
        {
            return Task.FromResult(Invalid(input?.OperationId));
        }

        HumanInputResponseValue? value = null;
        if (kind == HumanInputResponseOperationKind.Submit)
        {
            try
            {
                if (input.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                {
                    return Task.FromResult(Invalid(input.OperationId));
                }

                value = JsonSerializer.Deserialize<HumanInputResponseValue>(input.Value.GetRawText(), SurfaceJsonOptions());
            }
            catch (JsonException)
            {
                return Task.FromResult(Invalid(input.OperationId));
            }

            if (value is null)
            {
                return Task.FromResult(Invalid(input.OperationId));
            }
        }

        return SubmitResponseAsync(new HumanInputResponseOperationInput(input.OperationId, kind, input.RequestId, input.ExpectedLifecycleVersion, status, ToReference(input.ExpectedRequest)!, input.ResponseId, value, input.Explanation), cancellationToken);
    }

    /// <summary>Submits one exact lifecycle operation through canonical persistence and server-owned authority.</summary>
    /// <param name="input">The authority-free interface operation intent.</param>
    /// <param name="cancellationToken">A token that cancels before durable lifecycle intent begins.</param>
    /// <returns>The canonical redacted lifecycle operation result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before durable intent begins.</exception>
    /// <remarks>When one exact same-target operation is already durable, the facade reconstructs its server-owned binding,
    /// candidate, grant, and reason from authenticated evidence instead of resolving mutable current terms. The lifecycle service
    /// still reauthorizes the current actor before returning replay evidence. A resolvable Reroute candidate key must match the
    /// durable selection, so another option for the same operation identity cannot replay a prior route. Candidate keys remain
    /// process-local and are not durable replay authority: after registry loss or restart, the durable operation can still replay.
    /// Other candidate-bearing operations replay from durable evidence because their preparation admits only one candidate for an
    /// operation identity.</remarks>
    public async Task<HumanInputOperationResult> SubmitLifecycleAsync(
        HumanInputLifecycleOperationInput? input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            return Invalid(null);
        }

        if (!HumanInputIdentifier.IsValid(input.OperationId) || !AuthorityPurpose.TryParse(input.Reason, out var reason, out _))
        {
            return Invalid(input?.OperationId);
        }

        if (_provider is null)
        {
            return Unavailable(input.OperationId);
        }

        var observed = await ReadCatalogAsync(input.RequestId, cancellationToken).ConfigureAwait(false);
        var observedFailure = MapReplayTargetReadFailure(input.OperationId, observed);
        if (observedFailure is not null)
        {
            return observedFailure;
        }

        if (observed.Entry is not null)
        {
            HumanInputRequestLifecycleOperationEvidence[] persisted;
            try
            {
                persisted = observed.Entry.Lifecycle.Operations
                    .Where(operation => string.Equals(operation.OperationId, input.OperationId, StringComparison.Ordinal)
                        && string.Equals(operation.TargetRequestId, input.RequestId, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
            }
            catch (Exception)
            {
                return Ambiguous(input.OperationId);
            }

            if (persisted.Length > 1)
            {
                return Ambiguous(input.OperationId);
            }

            if (persisted.Length == 1)
            {
                return await SubmitPersistedLifecycleReplayAsync(input, observed.Entry, persisted[0], reason!, cancellationToken).ConfigureAwait(false);
            }
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
            || input.Kind != HumanInputRequestLifecycleOperationKind.Remind && RequiresGrant(input.Kind) != (terms.GrantReference is not null))
        {
            return terms?.Status == AgentRuntimeHumanInputAuthorityStatus.Denied
                ? Denied(input.OperationId)
                : Unavailable(input.OperationId);
        }

        HumanInputRequestBinding? expectedBinding = null;
        HumanInputRequestCatalogEntry? expectedEntry = null;
        if (input.Kind != HumanInputRequestLifecycleOperationKind.Create)
        {
            var expected = await ReadExpectedRequestAsync(input.RequestId, input.ExpectedRequest, cancellationToken).ConfigureAwait(false);
            var readFailure = MapExpectedRequestReadFailure(input.OperationId, expected);
            if (readFailure is not null)
            {
                return readFailure;
            }

            var matchingRequests = expected.Entry!.Lifecycle.RequestVersions
                .Where(request => Matches(request, input.ExpectedRequest!))
                .Take(2)
                .ToArray();
            if (matchingRequests.Length == 0)
            {
                return new HumanInputOperationResult(HumanInputOperationStatus.Conflict, input.OperationId, null, null, []);
            }

            if (matchingRequests.Length != 1)
            {
                return new HumanInputOperationResult(HumanInputOperationStatus.Ambiguous, input.OperationId, null, null, []);
            }

            expectedEntry = expected.Entry;
            expectedBinding = matchingRequests[0].Binding;
        }

        if (input.Kind == HumanInputRequestLifecycleOperationKind.Remind && terms.GrantReference is null)
        {
            var grant = await ResolveCurrentGrantAsync(expectedEntry ?? observed.Entry!, input.OperationId, input.ExpectedLifecycleVersion, input.ExpectedLifecycleStatus.ToString(), input.ExpectedRequest, cancellationToken).ConfigureAwait(false);
            if (grant.Status != HumanInputOperationStatus.Committed || grant.GrantReference is null)
            {
                return grant.Failure!;
            }

            terms = terms with { GrantReference = grant.GrantReference };
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

        return await MutateLifecycleAsync(command, input.RequestId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Submits one exact response operation through canonical persistence and server-owned authentication.</summary>
    /// <param name="input">The authority-free interface response intent.</param>
    /// <param name="cancellationToken">A token that cancels before durable response intent begins.</param>
    /// <returns>The canonical redacted response operation result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before durable intent begins.</exception>
    /// <remarks>When an exact response operation is already durable, the facade verifies the caller's complete input against
    /// its canonical command hash and reconstructs the original optimistic request terms from authenticated evidence. A later
    /// lifecycle posture therefore cannot turn an ambiguous-transport retry into a different operation.</remarks>
    public async Task<HumanInputOperationResult> SubmitResponseAsync(
        HumanInputResponseOperationInput? input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            return Invalid(null);
        }

        if (input.ExpectedRequest is null || !HumanInputIdentifier.IsValid(input.OperationId))
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

        HumanInputResponseOperationEvidence[] persisted;
        try
        {
            persisted = observed.Snapshot.Operations
                .Where(operation => string.Equals(operation.OperationId, input.OperationId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
        }
        catch (Exception)
        {
            return Ambiguous(input.OperationId);
        }

        if (persisted.Length > 1)
        {
            return Ambiguous(input.OperationId);
        }

        if (persisted.Length == 1)
        {
            return await SubmitPersistedResponseReplayAsync(input, observed.Snapshot, persisted[0], cancellationToken).ConfigureAwait(false);
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

        var service = CreateResponseLifecycleService();
        var result = await service.MutateAsync(command, cancellationToken).ConfigureAwait(false);
        return await MapResponseResultAsync(result, input.RequestId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<HumanInputRequestCatalogReadResult> ReadExpectedRequestAsync(
        string requestId,
        HumanInputRequestReference? expectedRequest,
        CancellationToken cancellationToken)
    {
        if (expectedRequest is null || !string.Equals(requestId, expectedRequest.RequestId, StringComparison.Ordinal))
        {
            return new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Invalid, 0, null);
        }

        return await ReadCatalogAsync(requestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanInputRequestCatalogReadResult> ReadCatalogAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _catalog.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
            return result ?? new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ambiguous, 0, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Unavailable, 0, null);
        }
    }

    private async Task<HumanInputOperationResult> SubmitPersistedLifecycleReplayAsync(
        HumanInputLifecycleOperationInput input,
        HumanInputRequestCatalogEntry target,
        HumanInputRequestLifecycleOperationEvidence evidence,
        AuthorityPurpose surfaceReason,
        CancellationToken cancellationToken)
    {
        bool evidenceIsValid;
        try
        {
            evidenceIsValid = HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid;
        }
        catch (Exception)
        {
            return Ambiguous(input.OperationId);
        }

        if (!evidenceIsValid)
        {
            return Ambiguous(input.OperationId);
        }

        if (!MatchesReplayIntent(input, evidence, surfaceReason))
        {
            return Conflict(input.OperationId);
        }

        var rerouteSelectionFailure = await ValidateRerouteReplayCandidateSelectionAsync(input, evidence, cancellationToken).ConfigureAwait(false);
        if (rerouteSelectionFailure is not null)
        {
            return rerouteSelectionFailure;
        }

        HumanInputRequest? candidate = null;
        if (evidence.CandidateRequest is { } candidateReference)
        {
            var candidateRead = string.Equals(candidateReference.RequestId, input.RequestId, StringComparison.Ordinal)
                ? new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 0, target)
                : await ReadCatalogAsync(candidateReference.RequestId, cancellationToken).ConfigureAwait(false);
            var candidateFailure = MapReplayCandidateReadFailure(input.OperationId, candidateRead);
            if (candidateFailure is not null)
            {
                return candidateFailure;
            }

            try
            {
                var candidates = candidateRead.Entry!.Lifecycle.RequestVersions
                    .Where(request => Matches(request, candidateReference))
                    .Take(2)
                    .ToArray();
                if (candidates.Length != 1)
                {
                    return Ambiguous(input.OperationId);
                }

                candidate = candidates[0];
            }
            catch (Exception)
            {
                return Ambiguous(input.OperationId);
            }
        }

        HumanInputRequestLifecycleCommand command;
        try
        {
            command = new HumanInputRequestLifecycleCommand(
                HumanInputRequestLifecycleCommand.CurrentSchemaVersion,
                input.OperationId,
                input.Kind,
                input.RequestId,
                input.ExpectedLifecycleVersion,
                input.ExpectedLifecycleStatus,
                input.ExpectedRequest,
                evidence.ExpectedBinding,
                candidate,
                evidence.GrantReference,
                evidence.Reason,
                evidence.RequestHash);
            if (!HumanInputRequestLifecycleCommandHash.Matches(command))
            {
                return Ambiguous(input.OperationId);
            }
        }
        catch (ArgumentException)
        {
            return Ambiguous(input.OperationId);
        }

        return await MutateLifecycleAsync(command, input.RequestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanInputOperationResult?> ValidateRerouteReplayCandidateSelectionAsync(
        HumanInputLifecycleOperationInput input,
        HumanInputRequestLifecycleOperationEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (input.Kind != HumanInputRequestLifecycleOperationKind.Reroute
            || evidence.Outcome != HumanInputRequestLifecycleOperationOutcome.Committed)
        {
            return null;
        }

        if (evidence.CandidateRequest is not { } expectedCandidate)
        {
            return Ambiguous(input.OperationId);
        }

        AgentRuntimeHumanInputLifecycleTerms terms;
        try
        {
            terms = await _provider!.ResolveLifecycleTermsAsync(
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

        if (terms is null)
        {
            return Unavailable(input.OperationId);
        }

        if (terms.Status == AgentRuntimeHumanInputAuthorityStatus.Unavailable)
        {
            return string.IsNullOrWhiteSpace(input.CandidateKey)
                ? null
                : Conflict(input.OperationId);
        }

        if (terms.Status != AgentRuntimeHumanInputAuthorityStatus.Ready
            || terms.CandidateRequest is null || terms.GrantReference is null)
        {
            return terms.Status == AgentRuntimeHumanInputAuthorityStatus.Denied
                ? Denied(input.OperationId)
                : Unavailable(input.OperationId);
        }

        return Matches(terms.CandidateRequest, expectedCandidate)
            ? null
            : Conflict(input.OperationId);
    }

    private async Task<HumanInputOperationResult> SubmitPersistedResponseReplayAsync(
        HumanInputResponseOperationInput input,
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        HumanInputResponseOperationEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (!HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence, out var captured, out _)
            || captured is null
            || !HumanInputResponseEligibilityEvidenceHash.Matches(captured)
            || !HumanInputResponseOperationCausality.Matches(captured, snapshot))
        {
            return Ambiguous(input.OperationId);
        }

        if (!MatchesResponseReplayIntent(input, captured))
        {
            return await ConflictResponseReplayAsync(input, cancellationToken).ConfigureAwait(false);
        }

        HumanInputResponseLifecycleCommand command;
        try
        {
            command = HumanInputResponseLifecycleCommandHash.Apply(new HumanInputResponseLifecycleCommand(
                HumanInputResponseLifecycleCommand.CurrentSchemaVersion,
                input.OperationId,
                input.Kind,
                input.RequestId,
                captured.ExpectedLifecycleVersion,
                captured.ExpectedLifecycleStatus,
                captured.Request,
                captured.ExpectedBinding,
                input.Kind == HumanInputResponseOperationKind.Submit ? input.ResponseId : null,
                input.Kind == HumanInputResponseOperationKind.Submit ? input.Value : null,
                input.Kind == HumanInputResponseOperationKind.Submit ? input.Explanation : null,
                captured.TargetResponses,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return Invalid(input.OperationId);
        }

        var result = await CreateResponseLifecycleService().MutateAsync(command, cancellationToken).ConfigureAwait(false);
        return await MapResponseResultAsync(result, input.RequestId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<HumanInputOperationResult> ConflictResponseReplayAsync(
        HumanInputResponseOperationInput input,
        CancellationToken cancellationToken)
        => new(
            HumanInputOperationStatus.Conflict,
            input.OperationId,
            null,
            await TryReadPostureAsync(input.RequestId, cancellationToken).ConfigureAwait(false),
            []);

    private async Task<HumanInputOperationResult> MutateLifecycleAsync(
        HumanInputRequestLifecycleCommand command,
        string requestId,
        CancellationToken cancellationToken)
    {
        var service = new HumanInputRequestLifecycleService(
            _lifecycleStore,
            new AgentRuntimeHumanInputLifecycleActorAuthorizer(_provider!),
            _grantResolver,
            _authorityTransaction,
            _workspaceId,
            _timeProvider);
        var result = await service.MutateAsync(command, cancellationToken).ConfigureAwait(false);
        return await MapLifecycleResultAsync(result, requestId, CancellationToken.None).ConfigureAwait(false);
    }

    private HumanInputResponseLifecycleService CreateResponseLifecycleService()
        => new(
            _responseStore,
            new AgentRuntimeHumanInputResponseActorAuthenticator(_provider!),
            _authorityTransaction,
            _workspaceId,
            _timeProvider);

    private async Task<(HumanInputOperationStatus Status, AuthorityGrantReference? GrantReference, HumanInputOperationResult? Failure)> ResolveCurrentGrantAsync(
        HumanInputRequestCatalogEntry entry,
        string operationId,
        long expectedLifecycleVersion,
        string expectedLifecycleStatus,
        HumanInputRequestReference? expectedRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            var head = entry.Lifecycle.Head;
            if (head is null
                || head.Status != HumanInputRequestLifecycleStatus.Pending
                || head.LifecycleVersion != expectedLifecycleVersion
                || !string.Equals(expectedLifecycleStatus, HumanInputRequestLifecycleStatus.Pending.ToString(), StringComparison.OrdinalIgnoreCase)
                || !Equals(head.CurrentRequest, expectedRequest))
            {
                return (HumanInputOperationStatus.Conflict, null, Conflict(operationId));
            }

            var evidence = entry.Lifecycle.Operations
                .Where(operation => operation is not null && operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
                    && operation.GrantReference is not null
                    && Equals(operation.ResultHead, head))
                .OrderByDescending(operation => operation.RecordedAtUtc)
                .ThenByDescending(operation => operation.OperationId, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (evidence.Length != 1
                || evidence[0].GrantReference is null
                || !HumanInputRequestLifecycleValidator.ValidateEvidence(evidence[0]).IsValid
                || !Equals(evidence[0].ResultHead, head))
            {
                return (HumanInputOperationStatus.Ambiguous, null, Ambiguous(operationId));
            }

            var resolution = await _grantResolver.ResolveAsync(evidence[0].GrantReference, cancellationToken).ConfigureAwait(false);
            if (resolution is null || resolution.Status != AuthorityGrantResolutionStatus.Active || !Equals(resolution.RequestedReference, evidence[0].GrantReference))
            {
                var status = MapGrantFailureStatus(resolution, evidence[0].GrantReference);
                return status switch
                {
                    HumanInputOperationStatus.NotFound => (status, null, new HumanInputOperationResult(status, operationId, null, null, [])),
                    HumanInputOperationStatus.Unavailable => (status, null, Unavailable(operationId)),
                    HumanInputOperationStatus.Ambiguous => (status, null, Ambiguous(operationId)),
                    _ => (HumanInputOperationStatus.Denied, null, Denied(operationId))
                };
            }

            return (HumanInputOperationStatus.Committed, evidence[0].GrantReference, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (HumanInputOperationStatus.Unavailable, null, Unavailable(operationId));
        }
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
        try
        {
            var current = await _catalog.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
            return current.Status == HumanInputRequestCatalogReadStatus.Ready && current.Entry is not null
                ? MapPosture(current.Entry)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
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
                request.ResponsePolicy.RequiredResponseCount,
                BoundedEligibleRespondentCount(request.EligibleRespondents),
                BoundedContinuationPolicyKind(request.ContinuationBinding)),
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

    private static int BoundedEligibleRespondentCount(HumanInputEligibleRespondent[]? respondents)
        => respondents is { Length: >= 1 and <= HumanInputLimits.MaxEligibleRespondents }
            ? respondents.Length
            : 0;

    private static HumanInputContinuationPolicyKind BoundedContinuationPolicyKind(HumanInputContinuationBinding? continuation)
        => continuation?.Kind == HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly
            ? continuation.Kind
            : HumanInputContinuationPolicyKind.Unknown;

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

    private static HumanInputOperationStatus MapGrantFailureStatus(AuthorityGrantResolution? resolution, AuthorityGrantReference? expectedReference)
        => resolution is null || expectedReference is null || !Equals(resolution.RequestedReference, expectedReference)
            ? HumanInputOperationStatus.Ambiguous
            : resolution.Status switch
            {
                AuthorityGrantResolutionStatus.NotFound or AuthorityGrantResolutionStatus.Invalid => HumanInputOperationStatus.NotFound,
                AuthorityGrantResolutionStatus.Unavailable or AuthorityGrantResolutionStatus.ProfileUnavailable or AuthorityGrantResolutionStatus.RoleUnavailable or AuthorityGrantResolutionStatus.LoopUnavailable => HumanInputOperationStatus.Unavailable,
                AuthorityGrantResolutionStatus.Unknown or AuthorityGrantResolutionStatus.Ambiguous => HumanInputOperationStatus.Ambiguous,
                _ => HumanInputOperationStatus.Denied
            };

    private static bool MatchesReplayIntent(
        HumanInputLifecycleOperationInput input,
        HumanInputRequestLifecycleOperationEvidence evidence,
        AuthorityPurpose surfaceReason)
        => input.Kind == evidence.Kind
            && string.Equals(input.RequestId, evidence.TargetRequestId, StringComparison.Ordinal)
            && input.ExpectedLifecycleVersion == evidence.ExpectedLifecycleVersion
            && input.ExpectedLifecycleStatus == evidence.ExpectedLifecycleStatus
            && Equals(input.ExpectedRequest, evidence.ExpectedRequest)
            && Equals(surfaceReason, evidence.Reason);

    private static bool MatchesResponseReplayIntent(
        HumanInputResponseOperationInput input,
        HumanInputResponseOperationEvidence evidence)
    {
        if (input.Kind != evidence.Kind
            || !string.Equals(input.RequestId, evidence.Request.RequestId, StringComparison.Ordinal)
            || input.ExpectedLifecycleVersion != evidence.ExpectedLifecycleVersion
            || input.ExpectedLifecycleStatus != evidence.ExpectedLifecycleStatus
            || !Equals(input.ExpectedRequest, evidence.Request))
        {
            return false;
        }

        return input.Kind switch
        {
            HumanInputResponseOperationKind.Submit => true,
            HumanInputResponseOperationKind.Withdraw or HumanInputResponseOperationKind.Select => evidence.TargetResponses.Length == 1
                && string.Equals(input.ResponseId, evidence.TargetResponses[0].ResponseId, StringComparison.Ordinal),
            _ => false,
        };
    }

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

    private static HumanInputOperationResult? MapExpectedRequestReadFailure(
        string operationId,
        HumanInputRequestCatalogReadResult result)
        => result.Status switch
        {
            HumanInputRequestCatalogReadStatus.Ready when result.Entry is not null => null,
            HumanInputRequestCatalogReadStatus.Invalid => Invalid(operationId),
            HumanInputRequestCatalogReadStatus.NotFound => new HumanInputOperationResult(HumanInputOperationStatus.NotFound, operationId, null, null, []),
            HumanInputRequestCatalogReadStatus.Unavailable => Unavailable(operationId),
            HumanInputRequestCatalogReadStatus.Ambiguous => new HumanInputOperationResult(HumanInputOperationStatus.Ambiguous, operationId, null, null, []),
            _ => new HumanInputOperationResult(HumanInputOperationStatus.Ambiguous, operationId, null, null, []),
        };

    private static HumanInputOperationResult? MapReplayTargetReadFailure(
        string operationId,
        HumanInputRequestCatalogReadResult result)
        => result.Status switch
        {
            HumanInputRequestCatalogReadStatus.Ready when result.Entry is not null => null,
            HumanInputRequestCatalogReadStatus.NotFound => null,
            HumanInputRequestCatalogReadStatus.Invalid => Invalid(operationId),
            HumanInputRequestCatalogReadStatus.Unavailable => Unavailable(operationId),
            _ => Ambiguous(operationId),
        };

    private static HumanInputOperationResult? MapReplayCandidateReadFailure(
        string operationId,
        HumanInputRequestCatalogReadResult result)
        => result.Status switch
        {
            HumanInputRequestCatalogReadStatus.Ready when result.Entry is not null => null,
            HumanInputRequestCatalogReadStatus.Unavailable => Unavailable(operationId),
            _ => Ambiguous(operationId),
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

    private static HumanInputOperationResult Conflict(string operationId)
        => new(HumanInputOperationStatus.Conflict, operationId, null, null, []);

    private static HumanInputOperationResult Ambiguous(string operationId)
        => new(HumanInputOperationStatus.Ambiguous, operationId, null, null, []);

    private static HumanInputOperationResult Unavailable(string? operationId)
        => new(HumanInputOperationStatus.Unavailable, operationId ?? string.Empty, null, null, []);

    private static HumanInputRequestReference? ToReference(HumanInputSurfaceRequestReference? reference)
        => reference is null
            ? null
            : new HumanInputRequestReference(HumanInputRequestReference.CurrentSchemaVersion, reference.RequestId, reference.RequestVersionId, reference.RequestHash);

    private static bool TryParseEnum<T>(string? value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(value, parsed.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions SurfaceJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }
}
