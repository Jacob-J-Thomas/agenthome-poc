using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Orchestrates exact immutable effect-reconciliation stages using server-owned ports only.</summary>
/// <remarks>
/// This service never invokes an actuator or a registered probe. It accepts only the exact effect and
/// ReviewBlocked frontier reconstructed by <see cref="IGovernedLoopEffectReconciliationInputSource"/>,
/// and every mutating stage is one compare-exchange through the canonical case store.
/// </remarks>
public sealed class GovernedLoopEffectReconciliationService : IGovernedLoopEffectReconciliationService
{
    private const string AuthorizationPurpose = "effect-reconciliation";
    private const string OpenPurpose = "effect-reconciliation.open";
    private const string AssessPurpose = "effect-reconciliation.assess";
    private const string DisposePurpose = "effect-reconciliation.dispose";
    private const string ResolvePurpose = "effect-reconciliation.resolve";

    private readonly IGovernedLoopEffectReconciliationCaseStore _caseStore;
    private readonly IGovernedLoopEffectReconciliationAuthorizationSource _authorizationSource;
    private readonly IGovernedLoopEffectReconciliationInputSource _inputSource;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the reconciliation orchestrator.</summary>
    /// <param name="caseStore">The sole canonical immutable reconciliation case store.</param>
    /// <param name="authorizationSource">The server-owned exact-purpose authorization source.</param>
    /// <param name="inputSource">The source of exact current effect and ReviewBlocked frontier input.</param>
    /// <param name="timeProvider">The trusted server clock used for fresh evidence boundaries.</param>
    /// <exception cref="ArgumentNullException">Thrown when a dependency is <see langword="null"/>.</exception>
    public GovernedLoopEffectReconciliationService(
        IGovernedLoopEffectReconciliationCaseStore caseStore,
        IGovernedLoopEffectReconciliationAuthorizationSource authorizationSource,
        IGovernedLoopEffectReconciliationInputSource inputSource,
        TimeProvider? timeProvider = null)
    {
        _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
        _authorizationSource = authorizationSource ?? throw new ArgumentNullException(nameof(authorizationSource));
        _inputSource = inputSource ?? throw new ArgumentNullException(nameof(inputSource));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationOperationResult> OpenAsync(GovernedLoopEffectReconciliationOpenRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateOpenRequest(request, out var caseId, out var binding, out var metadata, out var sources, out var receipts))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var now = TrustedNow();
        if (now is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable);
        }

        GovernedLoopEffectReconciliationCase candidate;
        try
        {
            candidate = GovernedLoopEffectReconciliationContract.Open(caseId!, binding!, metadata!, sources!, receipts!, now.Value);
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }
        catch (InvalidOperationException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var reference = Reference(candidate);
        var authorization = await AuthorizeAsync(AuthorizationPurpose, reference, binding!, cancellationToken);
        var authorizationStatus = AuthorizationStatus(authorization, AuthorizationPurpose, reference, binding!);
        if (authorizationStatus != GovernedLoopEffectReconciliationOperationStatus.Applied)
        {
            return Result(authorizationStatus);
        }

        var input = await ReadInputAsync(reference, binding!, cancellationToken);
        if (input.Status != GovernedLoopEffectReconciliationInputReadStatus.Found)
        {
            return Result(MapInputStatus(input.Status));
        }

        if (!MatchesInput(input, reference, binding!, candidate))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Corrupt);
        }

        if (!TryCreateMutationRequest(request!.OperationId!, OpenPurpose, reference, binding!, candidate, null, null, StableOpenFingerprint(metadata!, sources!, receipts!), cancellationToken, out var openMutation, stableOperationHash: true))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var mutation = await CompareExchangeAsync(openMutation!, cancellationToken);
        return MapMutation(mutation);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationOperationResult> ReadAsync(GovernedLoopEffectReconciliationCaseReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        GovernedLoopEffectReconciliationCaseReadResult read;
        try
        {
            read = await _caseStore.ReadAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable);
        }

        if (read is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable);
        }

        return read.Status switch
        {
            GovernedLoopEffectReconciliationCaseReadStatus.Found when read.Case is not null && MatchesReference(read.Case, request.Reference)
                => Result(GovernedLoopEffectReconciliationOperationStatus.Found, read.Case),
            GovernedLoopEffectReconciliationCaseReadStatus.Found => Result(GovernedLoopEffectReconciliationOperationStatus.Corrupt),
            GovernedLoopEffectReconciliationCaseReadStatus.NotFound => Result(GovernedLoopEffectReconciliationOperationStatus.NotFound),
            GovernedLoopEffectReconciliationCaseReadStatus.Invalid => Result(GovernedLoopEffectReconciliationOperationStatus.Invalid),
            GovernedLoopEffectReconciliationCaseReadStatus.Corrupt => Result(GovernedLoopEffectReconciliationOperationStatus.Corrupt),
            GovernedLoopEffectReconciliationCaseReadStatus.Unavailable => Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable),
            _ => Result(GovernedLoopEffectReconciliationOperationStatus.Unknown)
        };
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationOperationResult> AssessAsync(GovernedLoopEffectReconciliationAssessmentRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateMutationRequest(request?.OperationId, request?.Case, out var operationId, out var reference))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var current = await ReadCaseAsync(reference!, cancellationToken);
        if (current.Status != GovernedLoopEffectReconciliationOperationStatus.Found || current.Case is null)
        {
            return current;
        }

        var now = TrustedNow(current.Case.UpdatedAtUtc);
        if (now is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable);
        }

        var authorization = await AuthorizeAsync(AuthorizationPurpose, reference!, current.Case.Binding, cancellationToken);
        var authorizationStatus = AuthorizationStatus(authorization, AuthorizationPurpose, reference!, current.Case.Binding);
        if (authorizationStatus != GovernedLoopEffectReconciliationOperationStatus.Applied)
        {
            return Result(authorizationStatus);
        }

        if (current.Case.Disposition is not null || current.Case.Resolution is not null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Conflict, current.Case, await ReadEffectForConflictAsync(reference!, current.Case.Binding, cancellationToken));
        }

        var input = await ReadInputAsync(reference!, current.Case.Binding, cancellationToken);
        if (input.Status != GovernedLoopEffectReconciliationInputReadStatus.Found)
        {
            return Result(MapInputStatus(input.Status));
        }

        if (!MatchesInput(input, reference!, current.Case.Binding, current.Case))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Corrupt);
        }

        var assessment = CreateAssessment(current.Case, authorization!.AuthorityEvidenceHash!, now.Value, request!.SafeDetail);
        if (assessment is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        GovernedLoopEffectReconciliationCase next;
        try
        {
            next = GovernedLoopEffectReconciliationContract.Create(
                current.Case.CaseId,
                current.Case.CaseVersion + 1,
                current.Case.Binding,
                current.Case.ContractMetadata,
                current.Case.EvidenceSources,
                current.Case.ObservationHistory,
                [.. current.Case.AssessmentHistory, assessment],
                assessment.ContentHash,
                null,
                null,
                current.Case.CaseReceiptHashes,
                current.Case.ContentHash,
                current.Case.OpenedAtUtc,
                now.Value);
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }
        catch (InvalidOperationException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        if (!TryCreateMutationRequest(operationId!, AssessPurpose, reference!, current.Case.Binding, next, null, current.Case, StableCommandFingerprint(request!.SafeDetail), cancellationToken, out var assessmentMutation))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var mutation = await CompareExchangeAsync(assessmentMutation!, cancellationToken);
        return MapMutation(mutation);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationOperationResult> DisposeAsync(GovernedLoopEffectReconciliationDispositionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateMutationRequest(request?.OperationId, request?.Case, out var operationId, out var reference)
            || !GovernedLoopEffectReconciliationStateMatrix.IsSupported(request!.Kind))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var current = await ReadCaseAsync(reference!, cancellationToken);
        if (current.Status != GovernedLoopEffectReconciliationOperationStatus.Found || current.Case is null)
        {
            return current;
        }

        var now = TrustedNow(current.Case.UpdatedAtUtc);
        if (now is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable);
        }

        var authorization = await AuthorizeAsync(AuthorizationPurpose, reference!, current.Case.Binding, cancellationToken);
        var authorizationStatus = AuthorizationStatus(authorization, AuthorizationPurpose, reference!, current.Case.Binding);
        if (authorizationStatus != GovernedLoopEffectReconciliationOperationStatus.Applied)
        {
            return Result(authorizationStatus);
        }

        var currentAssessment = CurrentAssessment(current.Case);
        if (currentAssessment is null || current.Case.Disposition is not null || current.Case.Resolution is not null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Conflict, current.Case, await ReadEffectForConflictAsync(reference!, current.Case.Binding, cancellationToken));
        }

        if (!GovernedLoopEffectReconciliationStateMatrix.IsDispositionAllowed(currentAssessment.Kind, request.Kind))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var input = await ReadInputAsync(reference!, current.Case.Binding, cancellationToken);
        if (input.Status != GovernedLoopEffectReconciliationInputReadStatus.Found)
        {
            return Result(MapInputStatus(input.Status));
        }

        if (!MatchesInput(input, reference!, current.Case.Binding, current.Case))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Corrupt);
        }

        var disposition = CreateDisposition(current.Case, currentAssessment, request.Kind, authorization!.AuthorityEvidenceHash!, now.Value, request.SafeDetail);
        if (disposition is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        GovernedLoopEffectReconciliationCase next;
        try
        {
            next = GovernedLoopEffectReconciliationContract.Create(
                current.Case.CaseId,
                current.Case.CaseVersion + 1,
                current.Case.Binding,
                current.Case.ContractMetadata,
                current.Case.EvidenceSources,
                current.Case.ObservationHistory,
                current.Case.AssessmentHistory,
                current.Case.CurrentAssessmentHash,
                disposition,
                null,
                current.Case.CaseReceiptHashes,
                current.Case.ContentHash,
                current.Case.OpenedAtUtc,
                now.Value);
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }
        catch (InvalidOperationException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        if (!TryCreateMutationRequest(operationId!, DisposePurpose, reference!, current.Case.Binding, next, null, current.Case, StableDispositionFingerprint(request.Kind, request.SafeDetail), cancellationToken, out var dispositionMutation))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var mutation = await CompareExchangeAsync(dispositionMutation!, cancellationToken);
        return MapMutation(mutation);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationOperationResult> ResolveAsync(GovernedLoopEffectReconciliationResolutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateMutationRequest(request?.OperationId, request?.Case, out var operationId, out var reference))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var current = await ReadCaseAsync(reference!, cancellationToken);
        if (current.Status != GovernedLoopEffectReconciliationOperationStatus.Found || current.Case is null)
        {
            return current;
        }

        var now = TrustedNow(current.Case.UpdatedAtUtc);
        if (now is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable);
        }

        var authorization = await AuthorizeAsync(AuthorizationPurpose, reference!, current.Case.Binding, cancellationToken);
        var authorizationStatus = AuthorizationStatus(authorization, AuthorizationPurpose, reference!, current.Case.Binding);
        if (authorizationStatus != GovernedLoopEffectReconciliationOperationStatus.Applied)
        {
            return Result(authorizationStatus);
        }

        var currentAssessment = CurrentAssessment(current.Case);
        if (currentAssessment is null || current.Case.Disposition is null || current.Case.Resolution is not null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Conflict, current.Case, await ReadEffectForConflictAsync(reference!, current.Case.Binding, cancellationToken));
        }

        var outcome = GovernedLoopEffectReconciliationStateMatrix.GetAcceptedOutcome(currentAssessment.Kind);
        if (outcome is null || current.Case.Disposition.Kind == GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var input = await ReadInputAsync(reference!, current.Case.Binding, cancellationToken);
        if (input.Status != GovernedLoopEffectReconciliationInputReadStatus.Found)
        {
            return Result(MapInputStatus(input.Status));
        }

        if (!MatchesInput(input, reference!, current.Case.Binding, current.Case))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Corrupt);
        }

        if (!TryOutcomeEvidence(current.Case, currentAssessment, outcome.Value, out var evidenceId, out var evidenceHash))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var resolution = CreateResolution(current.Case, currentAssessment, authorization!.AuthorityEvidenceHash!, outcome.Value, evidenceId, evidenceHash, now.Value, request!.SafeDetail);
        if (resolution is null)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        GovernedLoopEffectReconciliationCase next;
        GovernedLoopEffectAttempt successor;
        try
        {
            next = GovernedLoopEffectReconciliationContract.Create(
                current.Case.CaseId,
                current.Case.CaseVersion + 1,
                current.Case.Binding,
                current.Case.ContractMetadata,
                current.Case.EvidenceSources,
                current.Case.ObservationHistory,
                current.Case.AssessmentHistory,
                current.Case.CurrentAssessmentHash,
                current.Case.Disposition,
                resolution,
                current.Case.CaseReceiptHashes,
                current.Case.ContentHash,
                current.Case.OpenedAtUtc,
                now.Value);
            successor = GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(input.EffectHead!, next);
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }
        catch (InvalidOperationException)
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        if (!TryCreateMutationRequest(operationId!, ResolvePurpose, reference!, current.Case.Binding, next, successor, current.Case, StableCommandFingerprint(request!.SafeDetail), cancellationToken, out var resolutionMutation))
        {
            return Result(GovernedLoopEffectReconciliationOperationStatus.Invalid);
        }

        var mutation = await CompareExchangeAsync(resolutionMutation!, cancellationToken);
        return MapMutation(mutation);
    }

    private async Task<GovernedLoopEffectReconciliationOperationResult> ReadCaseAsync(GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken)
        => await ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(reference), cancellationToken);

    private async Task<GovernedLoopEffectReconciliationInputReadResult> ReadInputAsync(GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationBinding binding, CancellationToken cancellationToken)
    {
        try
        {
            return await _inputSource.ReadAsync(new GovernedLoopEffectReconciliationInputReadRequest(reference, binding), cancellationToken)
                ?? new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Unavailable, null, null, null, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Unavailable, null, null, null, null, null);
        }
    }

    private async Task<GovernedLoopEffectReconciliationAuthorizationResult?> AuthorizeAsync(string purpose, GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationBinding binding, CancellationToken cancellationToken)
    {
        try
        {
            return await _authorizationSource.AuthorizeAsync(new GovernedLoopEffectReconciliationAuthorizationRequest(purpose, reference, binding), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<GovernedLoopEffectReconciliationCaseMutationResult?> CompareExchangeAsync(GovernedLoopEffectReconciliationCaseMutationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _caseStore.CompareExchangeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<GovernedLoopEffectAttempt?> ReadEffectForConflictAsync(GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationBinding binding, CancellationToken cancellationToken)
    {
        var input = await ReadInputAsync(reference, binding, cancellationToken);
        return input.Status == GovernedLoopEffectReconciliationInputReadStatus.Found ? input.EffectHead : null;
    }

    private DateTimeOffset? TrustedNow(DateTimeOffset? notBefore = null)
    {
        try
        {
            var value = _timeProvider.GetUtcNow();
            return value.Offset == TimeSpan.Zero && value != default && (notBefore is null || value >= notBefore.Value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryValidateOpenRequest(
        GovernedLoopEffectReconciliationOpenRequest? request,
        out string? caseId,
        out GovernedLoopEffectReconciliationBinding? binding,
        out GovernedLoopEffectReconciliationContractMetadata? metadata,
        out IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource>? sources,
        out IReadOnlyList<string>? receipts)
    {
        caseId = request?.CaseId;
        binding = request?.Binding;
        metadata = request?.ContractMetadata;
        sources = request?.EvidenceSources;
        receipts = request?.CaseReceiptHashes;
        return request is not null
            && IsIdentifier(request.OperationId)
            && IsIdentifier(caseId)
            && binding is not null
            && metadata is not null
            && sources is not null
            && receipts is not null
            && GovernedLoopEffectReconciliationContractValidator.Validate(binding).IsValid
            && GovernedLoopEffectReconciliationContractValidator.Validate(metadata).IsValid
            && sources.Count <= GovernedLoopEffectReconciliationContractLimits.MaxEvidenceSources
            && receipts.Count <= GovernedLoopEffectReconciliationContractLimits.MaxCaseReceipts;
    }

    private static bool TryValidateMutationRequest(string? operationId, GovernedLoopEffectReconciliationCaseReference? reference, out string? exactOperationId, out GovernedLoopEffectReconciliationCaseReference? exactReference)
    {
        exactOperationId = operationId;
        exactReference = reference;
        return IsIdentifier(operationId) && reference is not null;
    }

    private static bool IsIdentifier(string? value)
        => value is not null && CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters);

    private static bool IsSha256(string? value)
        => value is { Length: GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool MatchesInput(GovernedLoopEffectReconciliationInputReadResult input, GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationBinding binding, GovernedLoopEffectReconciliationCase reconciliationCase)
        => input.Status == GovernedLoopEffectReconciliationInputReadStatus.Found
            && input.Case is not null
            && Equals(input.Case, reference)
            && input.Binding is not null
            && Equals(input.Binding, binding)
            && input.EffectHead is not null
            && string.Equals(input.EffectHead.ContentHash, binding.CurrentAttemptHash, StringComparison.Ordinal)
            && input.EffectHead.Payload.Phase == GovernedLoopEffectPhase.ReconciliationRequired
            && GovernedLoopEffectReconciliationContract.Validate(reconciliationCase, input.EffectHead).IsValid
            && string.Equals(reconciliationCase.Binding.ContentHash, binding.ContentHash, StringComparison.Ordinal)
            && string.Equals(reconciliationCase.CaseId, reference.CaseId, StringComparison.Ordinal);

    private static bool MatchesReference(GovernedLoopEffectReconciliationCase value, GovernedLoopEffectReconciliationCaseReference reference)
        => string.Equals(value.CaseId, reference.CaseId, StringComparison.Ordinal)
            && value.CaseVersion == reference.CaseVersion
            && string.Equals(value.ContentHash, reference.ContentHash, StringComparison.Ordinal)
            && string.Equals(value.Binding.ContentHash, reference.BindingHash, StringComparison.Ordinal);

    private static GovernedLoopEffectReconciliationAssessment? CurrentAssessment(GovernedLoopEffectReconciliationCase value)
    {
        if (value.CurrentAssessmentHash is null || value.AssessmentHistory.Count == 0)
        {
            return null;
        }

        var matches = value.AssessmentHistory.Where(item => string.Equals(item.ContentHash, value.CurrentAssessmentHash, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 && GovernedLoopEffectReconciliationContractValidator.Validate(matches[0]).IsValid ? matches[0] : null;
    }

    private DateTimeOffset? TrustedObservationTime(GovernedLoopEffectReconciliationCase value, DateTimeOffset now)
        => now >= value.OpenedAtUtc ? now : null;

    private GovernedLoopEffectReconciliationAssessment? CreateAssessment(GovernedLoopEffectReconciliationCase value, string authorityEvidenceHash, DateTimeOffset now, string? safeDetail)
    {
        if (!IsSha256(authorityEvidenceHash) || TrustedObservationTime(value, now) is null)
        {
            return null;
        }

        var authoritative = value.EvidenceSources
            .Where(source => source.Kind == GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative
                && source.ReliabilityPosture == GovernedLoopEffectReconciliationReliabilityPosture.Authoritative
                && (source.RetiredAtUtc is null || source.RetiredAtUtc > now))
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var observations = value.ObservationHistory
            .Where(observation => authoritative.TryGetValue(observation.SourceId, out var source)
                && string.Equals(observation.CaseId, value.CaseId, StringComparison.Ordinal)
                && string.Equals(observation.BindingHash, value.Binding.ContentHash, StringComparison.Ordinal)
                && string.Equals(observation.SourceRegistrationHash, source.ContentHash, StringComparison.Ordinal)
                && observation.Kind == GovernedLoopEffectReconciliationObservationKind.Evidence
                && observation.ReliabilityPosture == GovernedLoopEffectReconciliationReliabilityPosture.Authoritative
                && observation.ObservedOutcome != GovernedLoopEffectReconciliationObservedOutcome.Unknown
                && observation.RecordedAtUtc <= now
                && observation.ObservedAtUtc is not null
                && observation.ObservedAtUtc.Value >= value.OpenedAtUtc
                && observation.ObservedAtUtc.Value <= now)
            .ToArray();
        var distinctOutcomes = observations.Select(item => item.ObservedOutcome).Distinct().ToArray();
        var kind = distinctOutcomes.Length switch
        {
            0 => GovernedLoopEffectReconciliationAssessmentKind.Inconclusive,
            > 1 => GovernedLoopEffectReconciliationAssessmentKind.Conflicting,
            _ => distinctOutcomes[0] switch
            {
                GovernedLoopEffectReconciliationObservedOutcome.NotApplied => GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied,
                GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded => GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded,
                GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed => GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed,
                GovernedLoopEffectReconciliationObservedOutcome.AppliedOutcomeUnknown => GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown,
                _ => GovernedLoopEffectReconciliationAssessmentKind.Inconclusive
            }
        };

        try
        {
            var candidate = new GovernedLoopEffectReconciliationAssessment(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                value.CaseId,
                value.Binding.ContentHash,
                Identifier("assessment", value.AssessmentHistory.Count + 1),
                kind,
                observations.Select(item => item.ContentHash).OrderBy(hash => hash, StringComparer.Ordinal).ToArray(),
                authorityEvidenceHash,
                now,
                safeDetail,
                string.Empty);
            return GovernedLoopEffectReconciliationContractHash.Apply(candidate);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static GovernedLoopEffectReconciliationDisposition? CreateDisposition(GovernedLoopEffectReconciliationCase value, GovernedLoopEffectReconciliationAssessment assessment, GovernedLoopEffectReconciliationDispositionKind kind, string authorityEvidenceHash, DateTimeOffset now, string? safeDetail)
    {
        if (!IsSha256(authorityEvidenceHash))
        {
            return null;
        }

        try
        {
            return GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationDisposition(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                value.CaseId,
                value.Binding.ContentHash,
                Identifier("disposition", value.CaseVersion + 1),
                kind,
                assessment.ContentHash,
                authorityEvidenceHash,
                now,
                safeDetail,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static GovernedLoopEffectReconciliationResolution? CreateResolution(GovernedLoopEffectReconciliationCase value, GovernedLoopEffectReconciliationAssessment assessment, string authorityEvidenceHash, GovernedLoopEffectOutcome outcome, string? evidenceId, string? evidenceHash, DateTimeOffset now, string? safeDetail)
    {
        if (!GovernedLoopEffectReconciliationStateMatrix.IsResolutionOutcomeAllowed(assessment.Kind, outcome)
            || !IsSha256(authorityEvidenceHash))
        {
            return null;
        }

        try
        {
            return GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationResolution(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                value.CaseId,
                value.Binding.ContentHash,
                Identifier("resolution", value.CaseVersion + 1),
                assessment.ContentHash,
                value.Disposition!.ContentHash,
                outcome,
                evidenceId,
                evidenceHash,
                authorityEvidenceHash,
                now,
                safeDetail,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryOutcomeEvidence(GovernedLoopEffectReconciliationCase value, GovernedLoopEffectReconciliationAssessment assessment, GovernedLoopEffectOutcome outcome, out string? evidenceId, out string? evidenceHash)
    {
        evidenceId = null;
        evidenceHash = null;
        if (outcome == GovernedLoopEffectOutcome.NotApplied)
        {
            return true;
        }

        var expected = outcome == GovernedLoopEffectOutcome.Succeeded
            ? GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded
            : GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed;
        var observation = value.ObservationHistory.FirstOrDefault(item => assessment.ObservationHashes.Contains(item.ContentHash, StringComparer.Ordinal) && item.ObservedOutcome == expected && item.EvidenceReference is not null && IsSha256(item.EvidenceHash));
        if (observation is null)
        {
            return false;
        }

        evidenceId = observation.EvidenceReference;
        evidenceHash = observation.EvidenceHash;
        return true;
    }

    private static GovernedLoopEffectReconciliationCaseReference Reference(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);

    private static bool TryCreateMutationRequest(
        string operationId,
        string purpose,
        GovernedLoopEffectReconciliationCaseReference reference,
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationCase replacement,
        GovernedLoopEffectAttempt? successor,
        GovernedLoopEffectReconciliationCase? predecessor,
        string semanticFingerprint,
        CancellationToken cancellationToken,
        out GovernedLoopEffectReconciliationCaseMutationRequest? mutation,
        bool stableOperationHash = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedVersion = predecessor?.CaseVersion;
            var expectedHash = predecessor?.ContentHash;
            mutation = new GovernedLoopEffectReconciliationCaseMutationRequest(
                operationId,
                RequestHash(operationId, purpose, reference, binding, semanticFingerprint, includeReferenceContentHash: !stableOperationHash),
                purpose,
                expectedVersion,
                expectedHash,
                binding,
                replacement,
                successor);
            return true;
        }
        catch (ArgumentException)
        {
            mutation = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            mutation = null;
            return false;
        }
    }

    private static string RequestHash(string operationId, string purpose, GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationBinding binding, string semanticFingerprint, bool includeReferenceContentHash)
    {
        var builder = new StringBuilder(4096);
        Append(builder, "embodysense.governed-loop-effect-reconciliation-operation.v1");
        Append(builder, operationId);
        Append(builder, purpose);
        Append(builder, reference.CaseId);
        Append(builder, reference.CaseVersion);
        if (includeReferenceContentHash)
        {
            Append(builder, reference.ContentHash);
        }

        Append(builder, reference.BindingHash);
        Append(builder, binding.ContentHash);
        Append(builder, semanticFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string StableOpenFingerprint(GovernedLoopEffectReconciliationContractMetadata metadata, IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource> sources, IReadOnlyList<string> receipts)
    {
        var builder = new StringBuilder(4096);
        Append(builder, metadata.ContentHash);
        foreach (var source in sources)
        {
            Append(builder, source.ContentHash);
        }

        foreach (var receipt in receipts)
        {
            Append(builder, receipt);
        }

        return builder.ToString();
    }

    private static string StableCommandFingerprint(string? value) => value ?? string.Empty;

    private static string StableDispositionFingerprint(GovernedLoopEffectReconciliationDispositionKind kind, string? safeDetail)
    {
        var builder = new StringBuilder(256);
        Append(builder, (long)kind);
        Append(builder, safeDetail);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }

    private static void Append(StringBuilder builder, long value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static string Identifier(string prefix, long number) => $"{prefix}-{number.ToString("D2", CultureInfo.InvariantCulture)}";

    private static GovernedLoopEffectReconciliationOperationStatus AuthorizationStatus(GovernedLoopEffectReconciliationAuthorizationResult? authorization, string purpose, GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationBinding binding)
    {
        if (authorization is null || !string.Equals(authorization.Purpose, purpose, StringComparison.Ordinal) || !Equals(authorization.Case, reference) || !Equals(authorization.Binding, binding))
        {
            return GovernedLoopEffectReconciliationOperationStatus.Unavailable;
        }

        return authorization.Status switch
        {
            GovernedLoopEffectReconciliationAuthorizationStatus.Ready when IsSha256(authorization.AuthorityEvidenceHash) => GovernedLoopEffectReconciliationOperationStatus.Applied,
            GovernedLoopEffectReconciliationAuthorizationStatus.Denied => GovernedLoopEffectReconciliationOperationStatus.Denied,
            GovernedLoopEffectReconciliationAuthorizationStatus.Invalid => GovernedLoopEffectReconciliationOperationStatus.Invalid,
            GovernedLoopEffectReconciliationAuthorizationStatus.Corrupt => GovernedLoopEffectReconciliationOperationStatus.Corrupt,
            GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable => GovernedLoopEffectReconciliationOperationStatus.Unavailable,
            _ => GovernedLoopEffectReconciliationOperationStatus.Unknown
        };
    }

    private static GovernedLoopEffectReconciliationOperationStatus MapInputStatus(GovernedLoopEffectReconciliationInputReadStatus status)
        => status switch
        {
            GovernedLoopEffectReconciliationInputReadStatus.NotFound => GovernedLoopEffectReconciliationOperationStatus.NotFound,
            GovernedLoopEffectReconciliationInputReadStatus.Conflict => GovernedLoopEffectReconciliationOperationStatus.Conflict,
            GovernedLoopEffectReconciliationInputReadStatus.Invalid => GovernedLoopEffectReconciliationOperationStatus.Invalid,
            GovernedLoopEffectReconciliationInputReadStatus.Corrupt => GovernedLoopEffectReconciliationOperationStatus.Corrupt,
            GovernedLoopEffectReconciliationInputReadStatus.Unavailable => GovernedLoopEffectReconciliationOperationStatus.Unavailable,
            _ => GovernedLoopEffectReconciliationOperationStatus.Unknown
        };

    private static GovernedLoopEffectReconciliationOperationResult MapMutation(GovernedLoopEffectReconciliationCaseMutationResult? mutation)
        => mutation is null
            ? Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable)
            : mutation.Status switch
            {
                GovernedLoopEffectReconciliationCaseMutationStatus.Applied => Result(GovernedLoopEffectReconciliationOperationStatus.Applied, mutation.Case, mutation.EffectHead),
                GovernedLoopEffectReconciliationCaseMutationStatus.Replayed => Result(GovernedLoopEffectReconciliationOperationStatus.Replayed, mutation.Case, mutation.EffectHead),
                GovernedLoopEffectReconciliationCaseMutationStatus.Conflict => Result(GovernedLoopEffectReconciliationOperationStatus.Conflict, mutation.Case, mutation.EffectHead),
                GovernedLoopEffectReconciliationCaseMutationStatus.Invalid => Result(GovernedLoopEffectReconciliationOperationStatus.Invalid),
                GovernedLoopEffectReconciliationCaseMutationStatus.Corrupt => Result(GovernedLoopEffectReconciliationOperationStatus.Corrupt),
                GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable => Result(GovernedLoopEffectReconciliationOperationStatus.Unavailable),
                GovernedLoopEffectReconciliationCaseMutationStatus.CapacityExceeded => Result(GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded),
                GovernedLoopEffectReconciliationCaseMutationStatus.RepairRequired => Result(GovernedLoopEffectReconciliationOperationStatus.RepairRequired),
                _ => Result(GovernedLoopEffectReconciliationOperationStatus.Unknown)
            };

    private static GovernedLoopEffectReconciliationOperationResult Result(GovernedLoopEffectReconciliationOperationStatus status, GovernedLoopEffectReconciliationCase? value = null, GovernedLoopEffectAttempt? effectHead = null)
    {
        var requiresCase = status is GovernedLoopEffectReconciliationOperationStatus.Applied
            or GovernedLoopEffectReconciliationOperationStatus.Replayed
            or GovernedLoopEffectReconciliationOperationStatus.Found;
        var requiresEffect = status is GovernedLoopEffectReconciliationOperationStatus.Applied or GovernedLoopEffectReconciliationOperationStatus.Replayed;
        var allowsConflictPayload = status == GovernedLoopEffectReconciliationOperationStatus.Conflict;
        if (requiresCase && value is null
            || requiresEffect && effectHead is null
            || !requiresCase && !allowsConflictPayload && (value is not null || effectHead is not null)
            || allowsConflictPayload && value is null && effectHead is not null)
        {
            return new GovernedLoopEffectReconciliationOperationResult(GovernedLoopEffectReconciliationOperationStatus.Unavailable, null, null);
        }

        return new GovernedLoopEffectReconciliationOperationResult(status, value, effectHead);
    }
}
