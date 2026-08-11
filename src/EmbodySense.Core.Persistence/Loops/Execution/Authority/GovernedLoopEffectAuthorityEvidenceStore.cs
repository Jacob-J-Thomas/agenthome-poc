using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;
using EmbodySense.Core.Persistence.Loops.Revisions;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority;

/// <summary>Persists one bounded, authenticated, append-only effect-authority decision ledger.</summary>
/// <remarks>
/// Mutable workspace artifacts are untrusted evidence. A server-owned monotonic trust provider authenticates the
/// complete ledger against its physical workspace, while a shared authority transaction and retained-handle path
/// session serialize every append. Decisions are keyed by their globally stable effect-operation identity; exact
/// replays never append twice and identity reuse with any different immutable content fails closed as a conflict.
/// Historical evidence is never rewritten or evicted automatically.
/// </remarks>
public sealed class GovernedLoopEffectAuthorityEvidenceStore : IGovernedLoopEffectAuthorityEvidenceStore, IGovernedLoopEffectAuthorityUsageStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly GovernedLoopEffectAuthorityEvidenceStorePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly GovernedLoopEffectAuthorityEvidenceStoreOptions _options;

    /// <summary>Creates an evidence store with the default server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="options">Optional bounded schema-1 persistence settings.</param>
    /// <param name="durabilityBarrier">An optional trusted filesystem durability adapter.</param>
    /// <param name="authorityTransaction">The optional shared authority transaction.</param>
    public GovernedLoopEffectAuthorityEvidenceStore(
        WorkspacePaths paths,
        GovernedLoopEffectAuthorityEvidenceStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), options, durabilityBarrier, authorityTransaction)
    {
    }

    /// <summary>Creates an evidence store over an explicit server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="trustProvider">The server-owned trust provider.</param>
    /// <param name="options">Optional bounded schema-1 persistence settings.</param>
    /// <param name="durabilityBarrier">An optional trusted filesystem durability adapter.</param>
    /// <param name="authorityTransaction">The optional shared authority transaction.</param>
    public GovernedLoopEffectAuthorityEvidenceStore(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        GovernedLoopEffectAuthorityEvidenceStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new GovernedLoopEffectAuthorityEvidenceStoreOptions());
        if (trustProvider.MaximumAuthenticationTagUtf8Bytes < 1
            || trustProvider.MaximumAuthenticationTagUtf8Bytes > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The trust provider must declare a positive bounded authentication-tag size.");
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        _paths = new GovernedLoopEffectAuthorityEvidenceStorePaths(paths);
        _pathGuard = new CapabilityCatalogPathGuard(
            paths.RootPath,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver);
        _trustProvider = trustProvider;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAuthorityEvidenceStoreResult> AppendAsync(
        GovernedLoopEffectAuthorityDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
        }

        var callbackEntered = false;
        GovernedLoopEffectAuthorityEvidenceStoreResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await AppendCoreAsync(decision, token, cancellationToken);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && callbackResult is null)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? Result(callbackEntered
                ? GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous
                : GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopEffectAuthorityEvidenceStoreResult> AppendCoreAsync(
        GovernedLoopEffectAuthorityDecision decision,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var decisionMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireForAppendAsync(cancellationToken);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
            GovernedLoopEffectAuthorityEvidenceDocument current;
            if (loaded.Disposition == GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Pending)
            {
                if (FindDecision(loaded.Pending!, decision.EffectOperationId) is not null)
                {
                    decisionMayHaveCommitted = true;
                }

                trust = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken);
                current = loaded.Pending!;
            }
            else
            {
                if (loaded.Disposition == GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered)
                {
                    return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous);
                }

                if (loaded.Document is null)
                {
                    return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
                }

                current = loaded.Document;
            }

            var existing = FindDecision(current, decision.EffectOperationId);
            if (existing is not null)
            {
                return Result(
                    SameDecision(existing, decision)
                        ? GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent
                        : GovernedLoopEffectAuthorityEvidenceStoreStatus.Conflict,
                    existing.ContentHash);
            }

            if (current.Decisions.Count >= _options.MaxDecisions)
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            var candidate = CreateCandidate(current, decision, workspaceIdentity);
            if (!ValidateDocument(candidate, workspaceIdentity) || !IsDirectSuccessor(current, candidate))
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            var currentDigest = ComputeContentDigest(current).Value;
            trust ??= await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken);
            if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            GovernedLoopEffectAuthorityEvidenceSerializedDocument serializedCandidate;
            try
            {
                serializedCandidate = await SerializeAsync(workspaceIdentity, candidate, cancellationToken);
            }
            catch (GovernedLoopEffectAuthorityEvidenceStoreLimitException)
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            var proof = await SerializeAsync(workspaceIdentity, current, cancellationToken);
            await session.WriteTextAtomicallyAsync(_paths.ProofPath, proof.Json, cancellationToken);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, cancellationToken);
            decisionMayHaveCommitted = true;
            await session.WriteTextAtomicallyAsync(_paths.PrimaryPath, serializedCandidate.Json, cancellationToken);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, cancellationToken);
            var advanced = await _trustProvider.AdvanceAsync(
                workspaceIdentity,
                trust.CurrentGeneration,
                trust.CurrentContentDigest,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                cancellationToken);
            RequireExactTrustSuccessor(
                advanced,
                workspaceIdentity,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                trust.CurrentGeneration,
                trust.CurrentContentDigest);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, cancellationToken);
            return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, decision.ContentHash);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !decisionMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return Result(decisionMayHaveCommitted
                ? GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous
                : GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAuthorityUsageStoreResult> ReserveAsync(
        GovernedLoopEffectAuthorityUsageRequest request,
        CancellationToken cancellationToken = default)
        => await ExecuteUsageAsync(request, null, GovernedLoopEffectAuthorityUsageOperation.ReserveTarget, cancellationToken);

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAuthorityUsageStoreResult> BeginCompletionAsync(
        GovernedLoopEffectAuthorityCompletionUsageRequest request,
        CancellationToken cancellationToken = default)
        => await ExecuteUsageAsync(null, request, GovernedLoopEffectAuthorityUsageOperation.BeginCompletion, cancellationToken);

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAuthorityUsageStoreResult> CompleteCompletionAsync(
        GovernedLoopEffectAuthorityCompletionUsageRequest request,
        CancellationToken cancellationToken = default)
        => await ExecuteUsageAsync(null, request, GovernedLoopEffectAuthorityUsageOperation.CompleteCompletion, cancellationToken);

    private async Task<GovernedLoopEffectAuthorityUsageStoreResult> ExecuteUsageAsync(
        GovernedLoopEffectAuthorityUsageRequest? usageRequest,
        GovernedLoopEffectAuthorityCompletionUsageRequest? completionRequest,
        GovernedLoopEffectAuthorityUsageOperation operation,
        CancellationToken cancellationToken)
    {
        var valid = operation == GovernedLoopEffectAuthorityUsageOperation.ReserveTarget
            ? GovernedLoopEffectAuthorityUsageRequestValidator.IsValid(usageRequest) && completionRequest is null
            : usageRequest is null && GovernedLoopEffectAuthorityCompletionUsageRequestValidator.IsValid(completionRequest);
        if (!valid)
        {
            return UsageResult(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
        }

        var callbackEntered = false;
        GovernedLoopEffectAuthorityUsageStoreResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await ReserveCoreAsync(usageRequest, completionRequest, operation, token, cancellationToken);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && callbackResult is null)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? UsageResult(callbackEntered
                ? GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous
                : GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopEffectAuthorityUsageStoreResult> ReserveCoreAsync(
        GovernedLoopEffectAuthorityUsageRequest? usageRequest,
        GovernedLoopEffectAuthorityCompletionUsageRequest? completionRequest,
        GovernedLoopEffectAuthorityUsageOperation operation,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var usageMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireForAppendAsync(cancellationToken);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
            GovernedLoopEffectAuthorityEvidenceDocument current;
            if (loaded.Disposition == GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Pending)
            {
                usageMayHaveCommitted = UsageMutationReflected(loaded.Document!, loaded.Pending!, usageRequest, completionRequest, operation);
                trust = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken);
                current = loaded.Pending!;
            }
            else
            {
                if (loaded.Disposition == GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered)
                {
                    return UsageResult(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous);
                }

                if (loaded.Document is null)
                {
                    return UsageResult(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
                }

                current = loaded.Document;
            }

            var status = EvaluateUsage(current, usageRequest, completionRequest, operation);
            if (status is not (GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved
                or GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending
                or GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted))
            {
                return UsageResult(status);
            }

            if (status == GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved
                    && current.TargetReservations.Count >= _options.MaxTargetReservations
                || status is GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending
                        or GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted
                    && current.CompletionClaims.Count >= _options.MaxCompletionClaims)
            {
                return UsageResult(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
            }

            var candidate = status == GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved
                ? CreateTargetCandidate(current, usageRequest!, workspaceIdentity)
                : CreateCompletionCandidate(current, completionRequest!, status, workspaceIdentity);
            if (!ValidateDocument(candidate, workspaceIdentity) || !IsDirectSuccessor(current, candidate))
            {
                return UsageResult(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
            }

            var currentDigest = ComputeContentDigest(current).Value;
            trust ??= await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken);
            if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
            {
                return UsageResult(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
            }

            GovernedLoopEffectAuthorityEvidenceSerializedDocument serializedCandidate;
            try
            {
                serializedCandidate = await SerializeAsync(workspaceIdentity, candidate, cancellationToken);
            }
            catch (GovernedLoopEffectAuthorityEvidenceStoreLimitException)
            {
                return UsageResult(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
            }

            var proof = await SerializeAsync(workspaceIdentity, current, cancellationToken);
            await session.WriteTextAtomicallyAsync(_paths.ProofPath, proof.Json, cancellationToken);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, cancellationToken);
            usageMayHaveCommitted = true;
            await session.WriteTextAtomicallyAsync(_paths.PrimaryPath, serializedCandidate.Json, cancellationToken);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, cancellationToken);
            var advanced = await _trustProvider.AdvanceAsync(
                workspaceIdentity,
                trust.CurrentGeneration,
                trust.CurrentContentDigest,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                cancellationToken);
            RequireExactTrustSuccessor(
                advanced,
                workspaceIdentity,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                trust.CurrentGeneration,
                trust.CurrentContentDigest);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, cancellationToken);
            return UsageResult(status);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !usageMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return UsageResult(usageMayHaveCommitted
                ? GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous
                : GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
        }
    }

    private static GovernedLoopEffectAuthorityUsageStoreStatus EvaluateUsage(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityUsageRequest? usageRequest,
        GovernedLoopEffectAuthorityCompletionUsageRequest? completionRequest,
        GovernedLoopEffectAuthorityUsageOperation operation)
    {
        if (operation == GovernedLoopEffectAuthorityUsageOperation.ReserveTarget)
        {
            return EvaluateTargetUsage(current, usageRequest!);
        }

        return EvaluateCompletionUsage(current, completionRequest!, operation);
    }

    private static GovernedLoopEffectAuthorityUsageStoreStatus EvaluateTargetUsage(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityUsageRequest request)
    {
        var completionClaims = current.CompletionClaims.Where(claim => SameGrant(claim.Grant, request.Grant)).ToArray();
        if (completionClaims.Any(claim => claim.Status == GovernedLoopEffectAuthorityCompletionClaimStatus.Completed))
        {
            return GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted;
        }

        if (completionClaims.Any(claim => claim.Status == GovernedLoopEffectAuthorityCompletionClaimStatus.Pending))
        {
            return GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous;
        }

        if (request.TargetFingerprint is null)
        {
            return GovernedLoopEffectAuthorityUsageStoreStatus.Allowed;
        }

        var operation = current.TargetReservations.SingleOrDefault(reservation =>
            string.Equals(reservation.EffectOperationId, request.EffectOperationId, StringComparison.Ordinal));
        if (operation is not null)
        {
            return SameReservationRequest(operation, request)
                ? GovernedLoopEffectAuthorityUsageStoreStatus.TargetAlreadyReserved
                : GovernedLoopEffectAuthorityUsageStoreStatus.Conflict;
        }

        var grantRunReservations = current.TargetReservations.Where(reservation =>
            SameGrant(reservation.Grant, request.Grant)
            && string.Equals(reservation.RunId, request.RunId, StringComparison.Ordinal)).ToArray();
        if (grantRunReservations.Any(reservation =>
                !string.Equals(reservation.AdmissionReceiptHash, request.AdmissionReceiptHash, StringComparison.Ordinal)
                || reservation.CompletionConstraint != request.CompletionConstraint))
        {
            return GovernedLoopEffectAuthorityUsageStoreStatus.Conflict;
        }

        if (grantRunReservations.Any(reservation =>
                string.Equals(reservation.TargetFingerprint, request.TargetFingerprint, StringComparison.Ordinal)))
        {
            return GovernedLoopEffectAuthorityUsageStoreStatus.TargetAlreadyReserved;
        }

        return grantRunReservations.Length >= request.MaxTargetCount
            ? GovernedLoopEffectAuthorityUsageStoreStatus.TargetLimitExceeded
            : GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved;
    }

    private static GovernedLoopEffectAuthorityUsageStoreStatus EvaluateCompletionUsage(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityCompletionUsageRequest request,
        GovernedLoopEffectAuthorityUsageOperation operation)
    {
        var claims = current.CompletionClaims.Where(claim => SameGrant(claim.Grant, request.Grant)).ToArray();
        if (claims.Length == 0)
        {
            return operation == GovernedLoopEffectAuthorityUsageOperation.BeginCompletion
                ? GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending
                : GovernedLoopEffectAuthorityUsageStoreStatus.Conflict;
        }

        var latest = claims[^1];
        var exact = SameCompletionIdentity(latest, request);
        if (operation == GovernedLoopEffectAuthorityUsageOperation.BeginCompletion)
        {
            if (latest.Status == GovernedLoopEffectAuthorityCompletionClaimStatus.Completed)
            {
                return exact
                    ? GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted
                    : GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted;
            }

            return exact
                ? GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyPending
                : GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous;
        }

        if (latest.Status == GovernedLoopEffectAuthorityCompletionClaimStatus.Completed)
        {
            return exact
                ? GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted
                : GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted;
        }

        return exact
            ? GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted
            : GovernedLoopEffectAuthorityUsageStoreStatus.Conflict;
    }

    private static bool UsageMutationReflected(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityEvidenceDocument pending,
        GovernedLoopEffectAuthorityUsageRequest? usageRequest,
        GovernedLoopEffectAuthorityCompletionUsageRequest? completionRequest,
        GovernedLoopEffectAuthorityUsageOperation operation)
    {
        if (operation == GovernedLoopEffectAuthorityUsageOperation.ReserveTarget)
        {
            return pending.TargetReservations.Count == current.TargetReservations.Count + 1
                && SameReservationRequest(pending.TargetReservations[^1], usageRequest!);
        }

        var expectedStatus = operation == GovernedLoopEffectAuthorityUsageOperation.BeginCompletion
            ? GovernedLoopEffectAuthorityCompletionClaimStatus.Pending
            : GovernedLoopEffectAuthorityCompletionClaimStatus.Completed;
        return pending.CompletionClaims.Count == current.CompletionClaims.Count + 1
            && pending.CompletionClaims[^1].Status == expectedStatus
            && SameCompletionIdentity(pending.CompletionClaims[^1], completionRequest!);
    }

    private static bool SameReservationRequest(
        GovernedLoopEffectAuthorityTargetReservation reservation,
        GovernedLoopEffectAuthorityUsageRequest request)
        => SameGrant(reservation.Grant, request.Grant)
            && reservation.CompletionConstraint == request.CompletionConstraint
            && string.Equals(reservation.AdmissionReceiptHash, request.AdmissionReceiptHash, StringComparison.Ordinal)
            && string.Equals(reservation.RunId, request.RunId, StringComparison.Ordinal)
            && reservation.ExecutionGeneration == request.ExecutionGeneration
            && string.Equals(reservation.NodeId, request.NodeId, StringComparison.Ordinal)
            && reservation.NodeAttempt == request.NodeAttempt
            && string.Equals(reservation.EffectOperationId, request.EffectOperationId, StringComparison.Ordinal)
            && reservation.BoundaryKind == request.BoundaryKind
            && reservation.MaxTargetCount == request.MaxTargetCount
            && string.Equals(reservation.TargetFingerprint, request.TargetFingerprint, StringComparison.Ordinal);

    private static bool SameCompletionIdentity(
        GovernedLoopEffectAuthorityCompletionClaim claim,
        GovernedLoopEffectAuthorityCompletionUsageRequest request)
        => SameGrant(claim.Grant, request.Grant)
            && string.Equals(claim.AdmissionReceiptHash, request.AdmissionReceiptHash, StringComparison.Ordinal)
            && string.Equals(claim.RunId, request.RunId, StringComparison.Ordinal)
            && string.Equals(claim.CompletionOperationId, request.CompletionOperationId, StringComparison.Ordinal);

    private static bool SameCompletionIdentity(
        GovernedLoopEffectAuthorityCompletionClaim left,
        GovernedLoopEffectAuthorityCompletionClaim right)
        => SameGrant(left.Grant, right.Grant)
            && string.Equals(left.AdmissionReceiptHash, right.AdmissionReceiptHash, StringComparison.Ordinal)
            && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && string.Equals(left.CompletionOperationId, right.CompletionOperationId, StringComparison.Ordinal);

    private static bool SameGrant(AuthorityGrantReference left, AuthorityGrantReference right)
        => Equals(left, right);

    private static string TargetScopeIdentity(GovernedLoopEffectAuthorityTargetReservation reservation)
        => string.Join(
            '\u001f',
            reservation.Grant.GrantId.Value,
            reservation.Grant.Revision.Value,
            reservation.Grant.ContentHash,
            reservation.AdmissionReceiptHash,
            reservation.RunId,
            reservation.TargetFingerprint);

    private async Task<GovernedLoopEffectAuthorityEvidenceStoreLoadResult> LoadAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        CapabilityCatalogTrustState? trust,
        CancellationToken cancellationToken)
    {
        var primaryExists = session.FileExists(_paths.PrimaryPath);
        var proofExists = session.FileExists(_paths.ProofPath);
        var empty = EmptyDocument(workspaceIdentity);
        if (trust is null)
        {
            return primaryExists || proofExists
                ? new(null, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Unavailable)
                : new(empty, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Current);
        }

        if (!string.Equals(trust.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal))
        {
            return new(null, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Unavailable);
        }

        var primary = primaryExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.PrimaryPath, cancellationToken)
            : null;
        var proof = proofExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.ProofPath, cancellationToken)
            : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new(primary, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Current);
        }

        var currentBase = proof is not null && MatchesCurrent(proof, trust)
            ? proof
            : !primaryExists && !proofExists && MatchesCurrent(empty, trust)
                ? empty
                : null;
        if (primary is not null
            && currentBase is not null
            && IsAuthenticatedDirectSuccessor(currentBase, primary, trust))
        {
            return new(currentBase, primary, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Pending);
        }

        if (!primaryExists && currentBase is not null)
        {
            return new(currentBase, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Current);
        }

        if (currentBase is not null)
        {
            return new(currentBase, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered);
        }

        if (proof is not null && MatchesPrevious(proof, trust))
        {
            return new(proof, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered);
        }

        if (primary is not null && MatchesPrevious(primary, trust))
        {
            return new(primary, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered);
        }

        return new(null, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Unavailable);
    }

    private async Task<GovernedLoopEffectAuthorityEvidenceDocument?> TryReadAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await session.TryReadAllBytesBoundAsync(path, _options.MaxArtifactUtf8Bytes, cancellationToken);
            if (bytes is null || HasUtf8Bom(bytes))
            {
                return null;
            }

            var text = _strictUtf8.GetString(bytes);
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            if (!GovernedLoopEffectAuthorityEvidenceStoreJson.IsStrictBoundedDocument(
                    json.RootElement,
                    _options.MaxDecisions,
                    _options.MaxTargetReservations,
                    _options.MaxCompletionClaims))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<GovernedLoopEffectAuthorityEvidenceDocument>(text, _jsonOptions);
            if (document is null
                || !ValidateDocument(document, workspaceIdentity)
                || !string.Equals(CanonicalJson(document), text, StringComparison.Ordinal)
                || string.IsNullOrEmpty(document.AuthenticationTag)
                || _strictUtf8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
                || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var digest, out _)
                || !digest!.FixedTimeEquals(ComputeContentDigest(document))
                || !await _trustProvider.VerifyArtifactAsync(
                    workspaceIdentity,
                    document.Generation,
                    document.ContentDigest,
                    document.AuthenticationTag,
                    cancellationToken))
            {
                return null;
            }

            return document;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<CapabilityCatalogTrustState> FinalizePendingAsync(
        string workspaceIdentity,
        CapabilityCatalogTrustState trust,
        GovernedLoopEffectAuthorityEvidenceStoreLoadResult loaded,
        CancellationToken cancellationToken)
    {
        var pending = loaded.Pending ?? throw new InvalidOperationException("A pending effect-authority successor is required.");
        var advanced = await _trustProvider.AdvanceAsync(
            workspaceIdentity,
            trust.CurrentGeneration,
            trust.CurrentContentDigest,
            pending.Generation,
            pending.ContentDigest,
            cancellationToken);
        RequireExactTrustSuccessor(
            advanced,
            workspaceIdentity,
            pending.Generation,
            pending.ContentDigest,
            trust.CurrentGeneration,
            trust.CurrentContentDigest);
        await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, cancellationToken);
        return advanced;
    }

    private static GovernedLoopEffectAuthorityEvidenceDocument CreateCandidate(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityDecision decision,
        string workspaceIdentity)
        => new(
            GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion,
            workspaceIdentity,
            checked(current.Generation + 1),
            current.Decisions.Append(decision).ToArray(),
            current.TargetReservations,
            current.CompletionClaims,
            string.Empty,
            string.Empty);

    private static GovernedLoopEffectAuthorityEvidenceDocument CreateTargetCandidate(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityUsageRequest request,
        string workspaceIdentity)
        => new(
            GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion,
            workspaceIdentity,
            checked(current.Generation + 1),
            current.Decisions,
            current.TargetReservations.Append(new GovernedLoopEffectAuthorityTargetReservation(
                GovernedLoopEffectAuthorityTargetReservation.CurrentSchemaVersion,
                request.Grant,
                request.CompletionConstraint,
                request.AdmissionReceiptHash,
                request.RunId,
                request.ExecutionGeneration,
                request.NodeId,
                request.NodeAttempt,
                request.EffectOperationId,
                request.BoundaryKind,
                request.MaxTargetCount,
                request.TargetFingerprint!,
                request.EvaluatedAtUtc)).ToArray(),
            current.CompletionClaims,
            string.Empty,
            string.Empty);

    private static GovernedLoopEffectAuthorityEvidenceDocument CreateCompletionCandidate(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityCompletionUsageRequest request,
        GovernedLoopEffectAuthorityUsageStoreStatus status,
        string workspaceIdentity)
        => new(
            GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion,
            workspaceIdentity,
            checked(current.Generation + 1),
            current.Decisions,
            current.TargetReservations,
            current.CompletionClaims.Append(new GovernedLoopEffectAuthorityCompletionClaim(
                GovernedLoopEffectAuthorityCompletionClaim.CurrentSchemaVersion,
                request.Grant,
                request.AdmissionReceiptHash,
                request.RunId,
                request.ExecutionGeneration,
                request.CompletionOperationId,
                status == GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending
                    ? GovernedLoopEffectAuthorityCompletionClaimStatus.Pending
                    : GovernedLoopEffectAuthorityCompletionClaimStatus.Completed,
                request.EvaluatedAtUtc)).ToArray(),
            string.Empty,
            string.Empty);

    private bool ValidateDocument(GovernedLoopEffectAuthorityEvidenceDocument document, string workspaceIdentity)
    {
        var mutationCount = document.Decisions?.Count + document.TargetReservations?.Count + document.CompletionClaims?.Count;
        if (document.SchemaVersion != GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion
            || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || document.Generation < 0
            || document.Decisions is null
            || document.TargetReservations is null
            || document.CompletionClaims is null
            || mutationCount != document.Generation
            || document.Decisions.Count > _options.MaxDecisions
            || document.TargetReservations.Count > _options.MaxTargetReservations
            || document.CompletionClaims.Count > _options.MaxCompletionClaims)
        {
            return false;
        }

        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in document.Decisions.Take(_options.MaxDecisions + 1))
        {
            if (!GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid
                || !operations.Add(decision.EffectOperationId))
            {
                return false;
            }
        }

        var reservationOperations = new HashSet<string>(StringComparer.Ordinal);
        var reservationTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reservation in document.TargetReservations.Take(_options.MaxTargetReservations + 1))
        {
            var request = new GovernedLoopEffectAuthorityUsageRequest(
                GovernedLoopEffectAuthorityUsageRequest.CurrentSchemaVersion,
                reservation.Grant,
                reservation.CompletionConstraint,
                reservation.AdmissionReceiptHash,
                reservation.RunId,
                reservation.ExecutionGeneration,
                reservation.NodeId,
                reservation.NodeAttempt,
                reservation.EffectOperationId,
                reservation.BoundaryKind,
                reservation.MaxTargetCount,
                reservation.TargetFingerprint,
                reservation.ReservedAtUtc);
            var targetIdentity = TargetScopeIdentity(reservation);
            if (reservation.SchemaVersion != GovernedLoopEffectAuthorityTargetReservation.CurrentSchemaVersion
                || !GovernedLoopEffectAuthorityUsageRequestValidator.IsValid(request)
                || !reservationOperations.Add(reservation.EffectOperationId)
                || !reservationTargets.Add(targetIdentity))
            {
                return false;
            }
        }

        var claimsByGrant = new Dictionary<AuthorityGrantReference, List<GovernedLoopEffectAuthorityCompletionClaim>>();
        foreach (var claim in document.CompletionClaims.Take(_options.MaxCompletionClaims + 1))
        {
            var request = new GovernedLoopEffectAuthorityCompletionUsageRequest(
                GovernedLoopEffectAuthorityCompletionUsageRequest.CurrentSchemaVersion,
                claim.Grant,
                claim.AdmissionReceiptHash,
                claim.RunId,
                claim.ExecutionGeneration,
                claim.CompletionOperationId,
                claim.RecordedAtUtc);
            if (claim.SchemaVersion != GovernedLoopEffectAuthorityCompletionClaim.CurrentSchemaVersion
                || !GovernedLoopEffectAuthorityCompletionUsageRequestValidator.IsValid(request)
                || !Enum.IsDefined(claim.Status))
            {
                return false;
            }

            if (!claimsByGrant.TryGetValue(claim.Grant, out var claims))
            {
                claims = [];
                claimsByGrant.Add(claim.Grant, claims);
            }

            claims.Add(claim);
        }

        foreach (var claims in claimsByGrant.Values)
        {
            if (claims.Count is < 1 or > 2
                || claims[0].Status != GovernedLoopEffectAuthorityCompletionClaimStatus.Pending
                || claims.Count == 2
                    && (claims[1].Status != GovernedLoopEffectAuthorityCompletionClaimStatus.Completed
                        || !SameCompletionIdentity(claims[0], claims[1])
                        || claims[1].RecordedAtUtc < claims[0].RecordedAtUtc))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDirectSuccessor(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityEvidenceDocument candidate)
    {
        var decisionDelta = candidate.Decisions.Count - current.Decisions.Count;
        var targetDelta = candidate.TargetReservations.Count - current.TargetReservations.Count;
        var completionDelta = candidate.CompletionClaims.Count - current.CompletionClaims.Count;
        return current.Generation < long.MaxValue
            && candidate.Generation == current.Generation + 1
            && candidate.SchemaVersion == current.SchemaVersion
            && string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            && decisionDelta is 0 or 1
            && targetDelta is 0 or 1
            && completionDelta is 0 or 1
            && decisionDelta + targetDelta + completionDelta == 1
            && candidate.Decisions.Take(current.Decisions.Count).Zip(current.Decisions).All(pair => SameDecision(pair.First, pair.Second))
            && candidate.TargetReservations.Take(current.TargetReservations.Count).SequenceEqual(current.TargetReservations)
            && candidate.CompletionClaims.Take(current.CompletionClaims.Count).SequenceEqual(current.CompletionClaims);
    }

    private static bool IsAuthenticatedDirectSuccessor(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityEvidenceDocument pending,
        CapabilityCatalogTrustState trust)
        => MatchesCurrent(current, trust)
            && pending.Generation == trust.CurrentGeneration + 1
            && IsDirectSuccessor(current, pending);

    private async Task<GovernedLoopEffectAuthorityEvidenceSerializedDocument> SerializeAsync(
        string workspaceIdentity,
        GovernedLoopEffectAuthorityEvidenceDocument document,
        CancellationToken cancellationToken)
    {
        var digest = ComputeContentDigest(document).Value;
        var authenticationTag = await _trustProvider.AuthenticateArtifactAsync(
            workspaceIdentity,
            document.Generation,
            digest,
            cancellationToken);
        if (string.IsNullOrEmpty(authenticationTag)
            || _strictUtf8.GetByteCount(authenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes)
        {
            throw new IOException("The trust provider returned an authentication tag outside its declared bound.");
        }

        var authenticated = document with { ContentDigest = digest, AuthenticationTag = authenticationTag };
        var json = CanonicalJson(authenticated);
        if (_strictUtf8.GetByteCount(json) > _options.MaxArtifactUtf8Bytes)
        {
            throw new GovernedLoopEffectAuthorityEvidenceStoreLimitException();
        }

        return new(json, digest, authenticationTag);
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(GovernedLoopEffectAuthorityEvidenceDocument document)
    {
        var content = JsonSerializer.Serialize(
            document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty },
            _hashOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
    }

    private static GovernedLoopEffectAuthorityEvidenceDocument EmptyDocument(string workspaceIdentity)
    {
        var empty = new GovernedLoopEffectAuthorityEvidenceDocument(
            GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion,
            workspaceIdentity,
            0,
            [],
            [],
            [],
            string.Empty,
            string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private async Task<CapabilityCatalogPathSession> AcquireForAppendAsync(CancellationToken cancellationToken)
        => await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.LockPath, createRoot: false, cancellationToken)
            ?? throw new IOException("The effect-authority evidence workspace is unavailable.");

    private static string WorkspaceIdentity(CapabilityCatalogPathSession session)
        => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(
            "embodysense-governed-loop-effect-authority-evidence-v1\n" + session.PhysicalIdentityMaterial);

    private async ValueTask ObserveAsync(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (_options.DurableBoundaryObserver is { } observer)
        {
            await observer(boundary, cancellationToken);
        }
    }

    private static GovernedLoopEffectAuthorityEvidenceStoreOptions ValidateOptions(
        GovernedLoopEffectAuthorityEvidenceStoreOptions options)
    {
        if (options.MaxDecisions is < 1 or > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumDecisions
            || options.MaxTargetReservations is < 1 or > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumTargetReservations
            || options.MaxCompletionClaims is < 1 or > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumCompletionClaims
            || options.MaxArtifactUtf8Bytes is < 1 or > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Effect-authority evidence-store options must remain within schema-1 bounds.");
        }

        return options;
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
        };
        options.Converters.Add(new GovernedLoopRevisionReferenceJsonConverter());
        options.Converters.Add(new AuthorityGrantIdJsonConverter());
        options.Converters.Add(new AuthorityGrantRevisionJsonConverter());
        options.Converters.Add(new AuthorityProfileIdJsonConverter());
        options.Converters.Add(new AuthorityProfileRevisionJsonConverter());
        options.Converters.Add(new AuthorityProfileHashJsonConverter());
        options.Converters.Add(new CapabilityDataClassJsonConverter());
        return options;
    }

    private static string CanonicalJson(GovernedLoopEffectAuthorityEvidenceDocument document)
        => JsonSerializer.Serialize(document, _jsonOptions) + "\n";

    private static GovernedLoopEffectAuthorityDecision? FindDecision(
        GovernedLoopEffectAuthorityEvidenceDocument document,
        string effectOperationId)
        => document.Decisions.SingleOrDefault(decision => SameIdentity(decision, effectOperationId));

    private static bool SameIdentity(
        GovernedLoopEffectAuthorityDecision left,
        GovernedLoopEffectAuthorityDecision right)
        => SameIdentity(left, right.EffectOperationId);

    private static bool SameIdentity(GovernedLoopEffectAuthorityDecision decision, string effectOperationId)
        => string.Equals(decision.EffectOperationId, effectOperationId, StringComparison.Ordinal);

    private static bool SameDecision(
        GovernedLoopEffectAuthorityDecision left,
        GovernedLoopEffectAuthorityDecision right)
        => FixedTimeHashEquals(left.ContentHash, right.ContentHash);

    private static bool MatchesCurrent(
        GovernedLoopEffectAuthorityEvidenceDocument document,
        CapabilityCatalogTrustState trust)
        => string.Equals(document.WorkspaceIdentity, trust.WorkspaceIdentity, StringComparison.Ordinal)
            && document.Generation == trust.CurrentGeneration
            && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);

    private static bool MatchesPrevious(
        GovernedLoopEffectAuthorityEvidenceDocument document,
        CapabilityCatalogTrustState trust)
        => string.Equals(document.WorkspaceIdentity, trust.WorkspaceIdentity, StringComparison.Ordinal)
            && document.Generation == trust.PreviousGeneration
            && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);

    private static void RequireExactTrustSuccessor(
        CapabilityCatalogTrustState? advanced,
        string workspaceIdentity,
        long candidateGeneration,
        string candidateContentDigest,
        long previousGeneration,
        string previousContentDigest)
    {
        if (advanced is null
            || !string.Equals(advanced.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || advanced.CurrentGeneration != candidateGeneration
            || !string.Equals(advanced.CurrentContentDigest, candidateContentDigest, StringComparison.Ordinal)
            || advanced.PreviousGeneration != previousGeneration
            || !string.Equals(advanced.PreviousContentDigest, previousContentDigest, StringComparison.Ordinal))
        {
            throw new IOException("The effect-authority trust provider did not return the exact committed successor state.");
        }
    }

    private static bool FixedTimeHashEquals(string? left, string? right)
    {
        return left is { Length: GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters }
            && right is { Length: GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters }
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes)
        => bytes.Count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static GovernedLoopEffectAuthorityEvidenceStoreResult Result(
        GovernedLoopEffectAuthorityEvidenceStoreStatus status,
        string? contentHash = null)
        => new(status, contentHash);

    private static GovernedLoopEffectAuthorityUsageStoreResult UsageResult(
        GovernedLoopEffectAuthorityUsageStoreStatus status)
        => new(status);

    private static bool IsAvailabilityFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or OverflowException
            or DecoderFallbackException
            or CryptographicException
            or NotSupportedException
            or InvalidOperationException;
}
