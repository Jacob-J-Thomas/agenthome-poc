using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Builds exact Human Input lifecycle candidates from canonical state and active grant evidence.</summary>
/// <remarks>Surface proposals provide only successor data. The current binding, eligible respondents, continuation, and
/// grant reference are copied from the canonical aggregate and never reconstructed from browser input.</remarks>
public sealed class HumanInputSupersedeCandidatePreparer : IHumanInputSupersedeCandidatePreparer
{
    private const string PreserveCanonicalPolicyKind = "preserve-canonical";
    private readonly IHumanInputRequestCatalog _catalog;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IHumanInputSupersedeCandidateRegistry _registry;
    private readonly string _workspaceId;
    private readonly string _actor;
    private readonly TimeProvider _timeProvider;
    private readonly IHumanInputRouteIntentSource _routeIntentSource;

    /// <summary>Creates a preparer over one canonical request catalog, grant resolver, candidate registry, actor, and clock.</summary>
    /// <param name="catalog">The canonical request catalog.</param>
    /// <param name="grantResolver">The canonical grant resolver.</param>
    /// <param name="registry">The bounded process-local candidate registry.</param>
    /// <param name="workspaceId">The server-owned workspace identity.</param>
    /// <param name="actor">The server-owned actor attribution.</param>
    /// <param name="timeProvider">The trusted preparation clock.</param>
    /// <param name="routeIntentSource">The deterministic server-owned route source, or the canonical default when omitted.</param>
    public HumanInputSupersedeCandidatePreparer(IHumanInputRequestCatalog catalog, IAuthorityGrantResolver grantResolver, IHumanInputSupersedeCandidateRegistry registry, string workspaceId, string actor, TimeProvider timeProvider, IHumanInputRouteIntentSource? routeIntentSource = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        _workspaceId = workspaceId;
        _actor = actor;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _routeIntentSource = routeIntentSource ?? new CanonicalHumanInputRouteIntentSource();
    }

    /// <inheritdoc />
    public async Task<HumanInputSupersedePreparationResult> PrepareAsync(HumanInputSupersedePreparationInput? input, CancellationToken cancellationToken = default)
    {
        if (!IsShapeValid(input))
        {
            return Result(input?.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_input");
        }

        var proposal = input!;
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetUtcNow(out var now))
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "clock_unavailable");
        }
        if (now == default || proposal.ExpiresAtUtc <= now || proposal.ExpiresAtUtc - now > HumanInputLimits.MaxResponseWindow)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_expiry");
        }

        HumanInputRequestCatalogReadResult read;
        try
        {
            read = await _catalog.ReadAsync(proposal.RequestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "catalog_unavailable");
        }

        if (read is null || read.Status == HumanInputRequestCatalogReadStatus.NotFound)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.NotFound, "request_not_found");
        }

        if (read.Status != HumanInputRequestCatalogReadStatus.Ready || read.Entry is null)
        {
            return Result(proposal.RequestId, MapReadStatus(read?.Status), "request_unavailable");
        }

        var head = read.Entry.Lifecycle.Head;
        var expectedReference = ToReference(proposal.ExpectedRequest!);
        if (head is null || head.CurrentRequest is null || head.Status != HumanInputRequestLifecycleStatus.Pending || expectedReference is null || !Equals(head.CurrentRequest, expectedReference) || head.LifecycleVersion != proposal.ExpectedLifecycleVersion || !string.Equals(proposal.ExpectedLifecycleStatus, HumanInputRequestLifecycleStatus.Pending.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Conflict, "request_state_conflict");
        }

        HumanInputRequest[] current;
        try
        {
            current = read.Entry.Lifecycle.RequestVersions
                .Where(request => request is not null && Matches(request, head.CurrentRequest))
                .Take(2)
                .ToArray();
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "request_evidence_ambiguous");
        }

        var currentIsValid = false;
        try
        {
            currentIsValid = current.Length == 1 && HumanInputRequestHash.Matches(current[0]);
        }
        catch (Exception)
        {
            currentIsValid = false;
        }

        if (!currentIsValid)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "request_evidence_ambiguous");
        }

        HumanInputRequestLifecycleOperationEvidence[] grantEvidence;
        try
        {
            grantEvidence = read.Entry.Lifecycle.Operations
                .Where(operation => operation is not null && operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
                    && operation.GrantReference is not null
                    && Equals(operation.ResultHead, head))
                .OrderByDescending(operation => operation.RecordedAtUtc)
                .ThenByDescending(operation => operation.OperationId, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "grant_evidence_ambiguous");
        }
        if (grantEvidence.Length != 1 || grantEvidence[0].GrantReference is null)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "grant_evidence_ambiguous");
        }

        AuthorityGrantResolution grant;
        try
        {
            grant = await _grantResolver.ResolveAsync(grantEvidence[0].GrantReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "grant_unavailable");
        }

        if (grant is null || grant.Status != AuthorityGrantResolutionStatus.Active || !Equals(grant.RequestedReference, grantEvidence[0].GrantReference))
        {
            return Result(proposal.RequestId, MapGrantStatus(grant, grantEvidence[0].GrantReference), "grant_inactive");
        }

        if (!TryParseSuccessor(proposal, current[0], now, out var candidate, out var failure))
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, failure);
        }

        var grantReference = grantEvidence[0].GrantReference!;
        var registration = new HumanInputSupersedeCandidateRegistration(
            _workspaceId,
            _actor,
            proposal.OperationId,
            proposal.RequestId,
            head.LifecycleVersion,
            expectedReference,
            candidate!,
            grantReference,
            proposal.ExpiresAtUtc);
        if (!_registry.TryRegister(registration, out var candidateKey))
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "candidate_registry_unavailable");
        }

        return new HumanInputSupersedePreparationResult(HumanInputSupersedePreparationStatus.Ready, proposal.RequestId, candidateKey, proposal.ExpiresAtUtc, null);
    }

    /// <inheritdoc />
    public async Task<HumanInputReroutePreparationResult> PrepareRerouteAsync(HumanInputReroutePreparationInput? input, CancellationToken cancellationToken = default)
    {
        if (!IsRerouteShapeValid(input))
        {
            return RerouteResult(input?.RequestId, HumanInputSupersedePreparationStatus.Invalid, null, "invalid_input");
        }

        var proposal = input!;
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetUtcNow(out var now))
        {
            return RerouteResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, null, "clock_unavailable");
        }
        if (!IsCandidateExpiryValid(proposal.CandidateExpiresAtUtc, now))
        {
            return RerouteResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, null, "invalid_candidate_expiry");
        }

        var context = await ReadCandidateContextAsync(proposal.OperationId, proposal.RequestId, ToReference(proposal.ExpectedRequest), proposal.ExpectedLifecycleVersion, proposal.ExpectedLifecycleStatus, cancellationToken).ConfigureAwait(false);
        if (context.Context is null)
        {
            return RerouteResult(proposal.RequestId, context.Status, null, context.Error);
        }
        if (context.Context.Head.LifecycleVersion >= HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion)
        {
            return RerouteResult(proposal.RequestId, HumanInputSupersedePreparationStatus.LimitExceeded, null, "lifecycle_version_limit");
        }

        HumanInputRouteIntentSourceResult routeIntents;
        try
        {
            routeIntents = await _routeIntentSource.ResolveAsync(context.Context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RerouteResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, null, "route_intent_source_unavailable");
        }

        var routeStatus = ValidateRouteIntents(context.Context.Request, routeIntents, out var exclusions);
        if (routeStatus != HumanInputSupersedePreparationStatus.Ready)
        {
            return RerouteResult(proposal.RequestId, routeStatus, null, "route_intent_source_invalid");
        }

        var preparationHash = HumanInputLifecycleCandidateIdentity.Digest(
            "reroute",
            HumanInputRouteIntentContract.ContractId,
            HumanInputRouteIntentContract.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            routeIntents.IntentHash,
            proposal.OperationId,
            proposal.RequestId,
            proposal.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            proposal.ExpectedRequest!.RequestVersionId,
            proposal.ExpectedRequest.RequestHash,
            proposal.ExpectedLifecycleStatus,
            proposal.CandidateExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        var options = new List<(HumanInputRerouteCandidateOption Option, HumanInputSupersedeCandidateRegistration Registration)>();
        foreach (var exclusion in exclusions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = CreateRerouteCandidate(context.Context.Request, proposal, exclusion.Ordinal);
            if (candidate is null || !IsValidTransition(proposal.OperationId, HumanInputRequestLifecycleOperationKind.Reroute, context.Context, candidate, now))
            {
                continue;
            }

            var registration = Registration(proposal.OperationId, proposal.RequestId, context.Context, candidate, proposal.CandidateExpiresAtUtc, HumanInputRequestLifecycleOperationKind.Reroute, preparationHash);
            options.Add((new HumanInputRerouteCandidateOption(string.Empty, $"Alternative route {options.Count + 1}", candidate.EligibleRespondents.Length, proposal.CandidateExpiresAtUtc), registration));
        }

        if (options.Count == 0)
        {
            return RerouteResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Conflict, null, "no_valid_reroute_option");
        }

        if (!_registry.TryRegisterGroup(options.Select(item => item.Registration).ToArray(), out var candidateKeys, out var registrationStatus))
        {
            return RerouteResult(proposal.RequestId, registrationStatus, null, registrationStatus == HumanInputSupersedePreparationStatus.Conflict ? "candidate_registry_conflict" : "candidate_registry_limit");
        }

        var preparedOptions = options.Select((item, index) => item.Option with { CandidateKey = candidateKeys[index] }).ToArray();
        return preparedOptions.Length == 0
            ? RerouteResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Conflict, null, "no_valid_reroute_option")
            : new HumanInputReroutePreparationResult(HumanInputSupersedePreparationStatus.Ready, proposal.RequestId, Array.AsReadOnly(preparedOptions), proposal.CandidateExpiresAtUtc, null);
    }

    /// <inheritdoc />
    public async Task<HumanInputAmendPreparationResult> PrepareAmendAsync(HumanInputAmendPreparationInput? input, CancellationToken cancellationToken = default)
    {
        if (!IsAmendShapeValid(input))
        {
            return AmendResult(input?.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_input");
        }

        var proposal = input!;
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetUtcNow(out var now))
        {
            return AmendResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "clock_unavailable");
        }
        if (!IsCandidateExpiryValid(proposal.CandidateExpiresAtUtc, now) || proposal.RequestExpiresAtUtc <= now)
        {
            return AmendResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_expiry");
        }

        var context = await ReadCandidateContextAsync(proposal.OperationId, proposal.RequestId, ToReference(proposal.ExpectedRequest), proposal.ExpectedLifecycleVersion, proposal.ExpectedLifecycleStatus, cancellationToken).ConfigureAwait(false);
        if (context.Context is null)
        {
            return AmendResult(proposal.RequestId, context.Status, context.Error);
        }
        if (context.Context.Head.LifecycleVersion >= HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion)
        {
            return AmendResult(proposal.RequestId, HumanInputSupersedePreparationStatus.LimitExceeded, "lifecycle_version_limit");
        }
        if (proposal.RequestExpiresAtUtc < context.Context.Request.Timing.RequestedAtUtc || proposal.RequestExpiresAtUtc - context.Context.Request.Timing.RequestedAtUtc > HumanInputLimits.MaxResponseWindow)
        {
            return AmendResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_request_expiry");
        }
        if (!TryParsePrivacy(proposal.PrivacyClass, out var privacy))
        {
            return AmendResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_privacy_class");
        }

        var intent = string.Join("\u001f", proposal.Purpose, proposal.Prompt, privacy, proposal.RequestExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), proposal.CandidateExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var preparationHash = HumanInputLifecycleCandidateIdentity.Digest(
            "amend",
            proposal.OperationId,
            proposal.RequestId,
            proposal.ExpectedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            proposal.ExpectedRequest!.RequestVersionId,
            proposal.ExpectedRequest.RequestHash,
            proposal.ExpectedLifecycleStatus,
            intent);
        var candidate = CreateAmendCandidate(context.Context.Request, proposal, privacy, intent);
        if (candidate is null || !IsValidTransition(proposal.OperationId, HumanInputRequestLifecycleOperationKind.Amend, context.Context, candidate, now))
        {
            return AmendResult(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_amendment");
        }

        var registration = Registration(proposal.OperationId, proposal.RequestId, context.Context, candidate, proposal.CandidateExpiresAtUtc, HumanInputRequestLifecycleOperationKind.Amend, preparationHash);
        if (!_registry.TryRegister(registration, out var candidateKey, out var registrationStatus))
        {
            return AmendResult(proposal.RequestId, registrationStatus, registrationStatus == HumanInputSupersedePreparationStatus.Conflict ? "candidate_registry_conflict" : "candidate_registry_limit");
        }
        return new HumanInputAmendPreparationResult(HumanInputSupersedePreparationStatus.Ready, proposal.RequestId, candidateKey, proposal.CandidateExpiresAtUtc, null);
    }

    private HumanInputSupersedeCandidateRegistration Registration(string operationId, string requestId, HumanInputLifecycleCandidatePreparationContext context, HumanInputRequest candidate, DateTimeOffset expiresAtUtc, HumanInputRequestLifecycleOperationKind kind, string? preparationHash = null)
        => new(_workspaceId, _actor, operationId, requestId, context.Head.LifecycleVersion, context.ExpectedRequest, candidate, context.GrantReference, expiresAtUtc, kind, preparationHash);

    private async Task<(HumanInputLifecycleCandidatePreparationContext? Context, HumanInputSupersedePreparationStatus Status, string Error)> ReadCandidateContextAsync(string operationId, string requestId, HumanInputRequestReference? expectedReference, long expectedLifecycleVersion, string expectedLifecycleStatus, CancellationToken cancellationToken)
    {
        if (expectedReference is null || !string.Equals(expectedReference.RequestId, requestId, StringComparison.Ordinal) || expectedLifecycleVersion < 1 || !string.Equals(expectedLifecycleStatus, HumanInputRequestLifecycleStatus.Pending.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (null, HumanInputSupersedePreparationStatus.Invalid, "invalid_expected_state");
        }

        HumanInputRequestCatalogReadResult read;
        try
        {
            read = await _catalog.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, HumanInputSupersedePreparationStatus.Unavailable, "catalog_unavailable");
        }
        if (read is null || read.Status == HumanInputRequestCatalogReadStatus.NotFound)
        {
            return (null, HumanInputSupersedePreparationStatus.NotFound, "request_not_found");
        }
        if (read.Status != HumanInputRequestCatalogReadStatus.Ready || read.Entry is null)
        {
            return (null, MapReadStatus(read.Status), "request_unavailable");
        }

        var head = read.Entry.Lifecycle.Head;
        if (head is null || head.Status != HumanInputRequestLifecycleStatus.Pending || head.LifecycleVersion != expectedLifecycleVersion || !Equals(head.CurrentRequest, expectedReference) || !HumanInputRequestLifecycleValidator.ValidateHead(head).IsValid)
        {
            return (null, HumanInputSupersedePreparationStatus.Conflict, "request_state_conflict");
        }
        if (read.Entry.Lifecycle.RequestVersions.Count >= HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest)
        {
            return (null, HumanInputSupersedePreparationStatus.LimitExceeded, "request_version_limit");
        }
        HumanInputRequest[] current;
        try
        {
            current = read.Entry.Lifecycle.RequestVersions.Where(request => request is not null && Matches(request, expectedReference)).Take(2).ToArray();
        }
        catch (Exception)
        {
            return (null, HumanInputSupersedePreparationStatus.Ambiguous, "request_evidence_ambiguous");
        }
        if (current.Length != 1 || !HumanInputValidator.ValidateRequest(current[0]).IsValid || !HumanInputRequestHash.Matches(current[0]))
        {
            return (null, HumanInputSupersedePreparationStatus.Ambiguous, "request_evidence_ambiguous");
        }
        HumanInputRequestLifecycleOperationEvidence[] grantEvidence;
        try
        {
            grantEvidence = read.Entry.Lifecycle.Operations.Where(operation => operation is not null && operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed && operation.GrantReference is not null && Equals(operation.ResultHead, head)).OrderByDescending(operation => operation.RecordedAtUtc).ThenByDescending(operation => operation.OperationId, StringComparer.Ordinal).Take(2).ToArray();
        }
        catch (Exception)
        {
            return (null, HumanInputSupersedePreparationStatus.Ambiguous, "grant_evidence_ambiguous");
        }
        if (grantEvidence.Length != 1 || grantEvidence[0].GrantReference is null)
        {
            return (null, HumanInputSupersedePreparationStatus.Ambiguous, "grant_evidence_ambiguous");
        }
        AuthorityGrantResolution grant;
        try
        {
            grant = await _grantResolver.ResolveAsync(grantEvidence[0].GrantReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, HumanInputSupersedePreparationStatus.Unavailable, "grant_unavailable");
        }
        if (grant is null || grant.Status != AuthorityGrantResolutionStatus.Active || !Equals(grant.RequestedReference, grantEvidence[0].GrantReference))
        {
            return (null, MapGrantStatus(grant, grantEvidence[0].GrantReference), "grant_inactive");
        }
        return (new HumanInputLifecycleCandidatePreparationContext(current[0], expectedReference, head, grantEvidence[0].GrantReference!), HumanInputSupersedePreparationStatus.Ready, string.Empty);
    }

    private HumanInputRequest? CreateRerouteCandidate(HumanInputRequest current, HumanInputReroutePreparationInput proposal, int removedIndex)
    {
        if (current.EligibleRespondents.Length <= 1)
        {
            return null;
        }
        var respondents = current.EligibleRespondents.Where((_, index) => index != removedIndex).ToArray();
        var intent = $"remove-{removedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}\u001f{proposal.CandidateExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}";
        var versionId = HumanInputLifecycleCandidateIdentity.RequestVersion("reroute", proposal.OperationId, current.RequestId, current.RequestHash, intent);
        try
        {
            return HumanInputRequestHash.Apply(current with { RequestVersionId = versionId, EligibleRespondents = respondents, RequestHash = string.Empty });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private HumanInputRequest? CreateAmendCandidate(HumanInputRequest current, HumanInputAmendPreparationInput proposal, HumanInputPrivacyClass privacy, string intent)
    {
        var versionId = HumanInputLifecycleCandidateIdentity.RequestVersion("amend", proposal.OperationId, current.RequestId, current.RequestHash, intent);
        try
        {
            return HumanInputRequestHash.Apply(current with { RequestVersionId = versionId, Purpose = proposal.Purpose, Prompt = proposal.Prompt, PrivacyClass = privacy, Timing = new HumanInputTiming(current.Timing.RequestedAtUtc, proposal.RequestExpiresAtUtc), RequestHash = string.Empty });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private bool IsValidTransition(string operationId, HumanInputRequestLifecycleOperationKind kind, HumanInputLifecycleCandidatePreparationContext context, HumanInputRequest candidate, DateTimeOffset now)
    {
        if (!HumanInputRequestSnapshot.TryCapture(candidate, out var captured, out _) || captured is null || !HumanInputValidator.ValidateRequest(captured).IsValid || !HumanInputRequestHash.Matches(captured) || !AuthorityActorId.TryParse(_actor, out var actor, out _) || !AuthorityPurpose.TryParse("human-input.lifecycle", out var purpose, out _) || actor is null || purpose is null || !HumanInputRequestReference.TryCreate(captured, out var candidateReference, out _) || candidateReference is null || context.Head.LifecycleVersion >= HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion)
        {
            return false;
        }
        var resultHead = context.Head with { LifecycleVersion = context.Head.LifecycleVersion + 1, CurrentRequest = candidateReference, LastOperationId = operationId, UpdatedAtUtc = now };
        var authorityHash = HumanInputLifecycleCandidateIdentity.Digest("human-input-candidate-authority", _workspaceId, _actor, operationId, captured.RequestHash);
        var evidence = new HumanInputRequestLifecycleOperationEvidence(1, operationId, captured.RequestHash, kind, HumanInputRequestLifecycleOperationOutcome.Committed, HumanInputRequestLifecycleOperationFailureCode.None, context.Request.RequestId, context.Head.LifecycleVersion, HumanInputRequestLifecycleStatus.Pending, context.ExpectedRequest, context.Request.Binding, context.Head, resultHead, null, null, null, candidateReference, actor, purpose, context.GrantReference, authorityHash, context.GrantReference.ContentHash?.Length == 71 ? context.GrantReference.ContentHash[7..] : authorityHash, now);
        return HumanInputRequestLifecycleValidator.ValidateCommittedTransition(evidence, context.Request, captured).IsValid;
    }

    private static bool IsRerouteShapeValid(HumanInputReroutePreparationInput? input)
        => input is not null && HumanInputIdentifier.IsValid(input.OperationId) && HumanInputIdentifier.IsValid(input.RequestId) && input.ExpectedRequest is not null && string.Equals(input.ExpectedRequest.RequestId, input.RequestId, StringComparison.Ordinal) && HumanInputIdentifier.IsValid(input.ExpectedRequest.RequestVersionId) && IsSha256(input.ExpectedRequest.RequestHash) && input.ExpectedLifecycleVersion >= 1 && !string.IsNullOrWhiteSpace(input.ExpectedLifecycleStatus) && input.CandidateExpiresAtUtc != default && input.CandidateExpiresAtUtc.Offset == TimeSpan.Zero;

    private static bool IsAmendShapeValid(HumanInputAmendPreparationInput? input)
        => input is not null && HumanInputIdentifier.IsValid(input.OperationId) && HumanInputIdentifier.IsValid(input.RequestId) && input.ExpectedRequest is not null && string.Equals(input.ExpectedRequest.RequestId, input.RequestId, StringComparison.Ordinal) && HumanInputIdentifier.IsValid(input.ExpectedRequest.RequestVersionId) && IsSha256(input.ExpectedRequest.RequestHash) && input.ExpectedLifecycleVersion >= 1 && !string.IsNullOrWhiteSpace(input.ExpectedLifecycleStatus) && HumanInputText.IsValid(input.Purpose, HumanInputLimits.MaxPurposeCharacters, true) && HumanInputText.IsValid(input.Prompt, HumanInputLimits.MaxPromptCharacters, true) && !string.IsNullOrWhiteSpace(input.PrivacyClass) && input.RequestExpiresAtUtc != default && input.RequestExpiresAtUtc.Offset == TimeSpan.Zero && input.CandidateExpiresAtUtc != default && input.CandidateExpiresAtUtc.Offset == TimeSpan.Zero;

    private static bool IsCandidateExpiryValid(DateTimeOffset expiresAtUtc, DateTimeOffset now)
        => now != default && expiresAtUtc != default && expiresAtUtc.Offset == TimeSpan.Zero && expiresAtUtc > now && expiresAtUtc - now <= HumanInputLifecycleCandidateLimits.MaxCandidateLifetime;

    private static HumanInputSupersedePreparationStatus MapGrantStatus(AuthorityGrantResolution? resolution, AuthorityGrantReference? expectedReference)
        => resolution is null || expectedReference is null || !Equals(resolution.RequestedReference, expectedReference)
            ? HumanInputSupersedePreparationStatus.Ambiguous
            : resolution.Status switch
            {
                AuthorityGrantResolutionStatus.NotFound or AuthorityGrantResolutionStatus.Invalid => HumanInputSupersedePreparationStatus.NotFound,
                AuthorityGrantResolutionStatus.Unavailable or AuthorityGrantResolutionStatus.ProfileUnavailable or AuthorityGrantResolutionStatus.RoleUnavailable or AuthorityGrantResolutionStatus.LoopUnavailable => HumanInputSupersedePreparationStatus.Unavailable,
                AuthorityGrantResolutionStatus.Unknown or AuthorityGrantResolutionStatus.Ambiguous => HumanInputSupersedePreparationStatus.Ambiguous,
                _ => HumanInputSupersedePreparationStatus.Denied
            };

    private static HumanInputSupersedePreparationStatus ValidateRouteIntents(HumanInputRequest request, HumanInputRouteIntentSourceResult? result, out IReadOnlyList<HumanInputRouteExclusionIntent> exclusions)
    {
        exclusions = [];
        if (result is null)
        {
            return HumanInputSupersedePreparationStatus.Ambiguous;
        }

        if (result.Status != HumanInputRouteIntentSourceStatus.Ready)
        {
            return result.Status switch
            {
                HumanInputRouteIntentSourceStatus.Invalid => HumanInputSupersedePreparationStatus.Invalid,
                HumanInputRouteIntentSourceStatus.Unavailable => HumanInputSupersedePreparationStatus.Unavailable,
                _ => HumanInputSupersedePreparationStatus.Ambiguous
            };
        }

        if (!string.Equals(result.ContractId, HumanInputRouteIntentContract.ContractId, StringComparison.Ordinal)
            || result.ContractVersion != HumanInputRouteIntentContract.Version
            || result.Intents is null
            || result.Intents.Count != request.EligibleRespondents.Length
            || result.Intents.Count is < 1 or > HumanInputLifecycleCandidateLimits.MaxRerouteOptions
            || !IsSha256(result.IntentHash))
        {
            return HumanInputSupersedePreparationStatus.Invalid;
        }

        var ordered = result.Intents.ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var intent = ordered[index];
            if (intent is null || intent.Ordinal != index || !IsSha256(intent.RouteEntryHash)
                || !string.Equals(intent.RouteEntryHash, CanonicalHumanInputRouteIntentSource.RouteEntryHash(request.EligibleRespondents[index]), StringComparison.Ordinal))
            {
                return HumanInputSupersedePreparationStatus.Invalid;
            }
        }

        var expectedHash = HumanInputRouteIntentSourceResult.ComputeIntentHash(request.RequestHash, ordered);
        if (!string.Equals(expectedHash, result.IntentHash, StringComparison.Ordinal))
        {
            return HumanInputSupersedePreparationStatus.Ambiguous;
        }

        exclusions = Array.AsReadOnly(ordered);
        return HumanInputSupersedePreparationStatus.Ready;
    }

    private static bool IsSha256(string? value)
        => value is { Length: HumanInputLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool TryParseSuccessor(HumanInputSupersedePreparationInput input, HumanInputRequest current, DateTimeOffset now, out HumanInputRequest? candidate, out string failure)
    {
        candidate = null;
        failure = "invalid_successor";
        if (input.ResponseSchema.ValueKind != JsonValueKind.Object || input.ResponsePolicy.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var options = JsonOptions();
            var schema = JsonSerializer.Deserialize<HumanInputResponseSchema>(input.ResponseSchema.GetRawText(), options);
            var policy = IsPreserveCanonicalPolicyIntent(input.ResponsePolicy)
                ? current.ResponsePolicy
                : JsonSerializer.Deserialize<HumanInputResponsePolicy>(input.ResponsePolicy.GetRawText(), options);
            if (schema is null || policy is null || !TryParsePrivacy(input.PrivacyClass, out var privacy))
            {
                return false;
            }

            var successor = current with
            {
                RequestId = $"supersede-{Guid.NewGuid():N}",
                RequestVersionId = $"version-{Guid.NewGuid():N}",
                Purpose = input.Purpose,
                Prompt = input.Prompt,
                ResponseSchema = schema,
                PrivacyClass = privacy,
                Timing = new HumanInputTiming(now, input.ExpiresAtUtc),
                ResponsePolicy = policy,
                RequestHash = string.Empty
            };
            candidate = HumanInputRequestHash.Apply(successor);
            if (!HumanInputRequestSnapshot.TryCapture(candidate, out candidate, out _)
                || candidate is null
                || !HumanInputRequestHash.Matches(candidate)
                || !string.Equals(candidate.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            {
                candidate = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            failure = "invalid_successor_json";
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsPreserveCanonicalPolicyIntent(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = value.EnumerateObject().ToArray();
        return properties.Length == 1
            && properties[0].NameEquals("kind")
            && properties[0].Value.ValueKind == JsonValueKind.String
            && string.Equals(properties[0].Value.GetString(), PreserveCanonicalPolicyKind, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static bool TryParsePrivacy(string? value, out HumanInputPrivacyClass privacy)
        => Enum.TryParse(value, ignoreCase: true, out privacy)
            && Enum.IsDefined(privacy)
            && privacy != HumanInputPrivacyClass.Unknown
            && string.Equals(value, privacy.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsShapeValid(HumanInputSupersedePreparationInput? input)
        => input is not null
            && HumanInputIdentifier.IsValid(input.OperationId)
            && !string.IsNullOrWhiteSpace(input.RequestId)
            && input.ExpectedRequest is not null
            && string.Equals(input.ExpectedRequest.RequestId, input.RequestId, StringComparison.Ordinal)
            && input.ExpectedLifecycleVersion >= 1
            && !string.IsNullOrWhiteSpace(input.ExpectedLifecycleStatus)
            && !string.IsNullOrWhiteSpace(input.Purpose)
            && !string.IsNullOrWhiteSpace(input.Prompt)
            && !string.IsNullOrWhiteSpace(input.PrivacyClass);

    private static bool Matches(HumanInputRequest request, HumanInputRequestReference reference)
        => request.SchemaVersion == reference.SchemaVersion
            && string.Equals(request.RequestId, reference.RequestId, StringComparison.Ordinal)
            && string.Equals(request.RequestVersionId, reference.RequestVersionId, StringComparison.Ordinal)
            && string.Equals(request.RequestHash, reference.RequestHash, StringComparison.Ordinal);

    private static HumanInputRequestReference? ToReference(HumanInputSurfaceRequestReference? reference)
        => reference is null ? null : new HumanInputRequestReference(HumanInputRequestReference.CurrentSchemaVersion, reference.RequestId, reference.RequestVersionId, reference.RequestHash);

    private static HumanInputSupersedePreparationStatus MapReadStatus(HumanInputRequestCatalogReadStatus? status)
        => status switch
        {
            HumanInputRequestCatalogReadStatus.Invalid => HumanInputSupersedePreparationStatus.Invalid,
            HumanInputRequestCatalogReadStatus.NotFound => HumanInputSupersedePreparationStatus.NotFound,
            HumanInputRequestCatalogReadStatus.Unavailable => HumanInputSupersedePreparationStatus.Unavailable,
            _ => HumanInputSupersedePreparationStatus.Ambiguous,
        };

    private static HumanInputSupersedePreparationResult Result(string? requestId, HumanInputSupersedePreparationStatus status, string error)
        => new(status, requestId ?? string.Empty, null, null, error);

    private static HumanInputReroutePreparationResult RerouteResult(string? requestId, HumanInputSupersedePreparationStatus status, IReadOnlyList<HumanInputRerouteCandidateOption>? options, string error)
        => new(status, requestId ?? string.Empty, options ?? [], null, error);

    private static HumanInputAmendPreparationResult AmendResult(string? requestId, HumanInputSupersedePreparationStatus status, string error)
        => new(status, requestId ?? string.Empty, null, null, error);

    private bool TryGetUtcNow(out DateTimeOffset now)
    {
        try
        {
            now = _timeProvider.GetUtcNow();
            return now != default;
        }
        catch (Exception)
        {
            now = default;
            return false;
        }
    }
}
