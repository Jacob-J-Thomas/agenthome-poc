using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage;

/// <summary>Atomically consumes first-bound-run authority around the exact idempotent success-exit persistence callback.</summary>
/// <remarks>
/// The caller must invoke this boundary only after every optional publication has succeeded and immediately before
/// persisting a successful terminal run state. Failed, cancelled, and paused runs never call this boundary. A pending
/// claim deliberately survives callback failure or process loss; only the exact same run and completion operation may
/// resume that idempotent callback, while effects and different runs fail closed.
/// </remarks>
public sealed class GovernedLoopFirstBoundRunCompletionBoundary
{
    private const string CompletionOperationDomain = "embodysense-first-bound-run-completion-v1";
    private static readonly TimeSpan _integrityFinalizeTimeout = TimeSpan.FromSeconds(30);
    private readonly IGovernedLoopEffectAuthorityUsageStore _usageStore;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a completion boundary over the same authority transaction used by effect dispatch.</summary>
    /// <param name="usageStore">The authenticated non-renewable authority-usage ledger.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority transaction.</param>
    /// <param name="timeProvider">The optional trusted UTC clock.</param>
    public GovernedLoopFirstBoundRunCompletionBoundary(
        IGovernedLoopEffectAuthorityUsageStore usageStore,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _usageStore = usageStore ?? throw new ArgumentNullException(nameof(usageStore));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs the exact terminal persistence callback under the durable first-bound-run completion claim.</summary>
    /// <param name="admissionReceipt">The complete immutable admission receipt containing the exact grant boundary.</param>
    /// <param name="executionBinding">The exact admitted run and current execution generation.</param>
    /// <param name="commitTerminalCompletion">The exact idempotent successful-terminal persistence callback.</param>
    /// <param name="cancellationToken">The token used while fencing and completing the operation.</param>
    /// <returns>The durable completion posture and whether the callback was invoked.</returns>
    public Task<GovernedLoopFirstBoundRunCompletionExecutionResult> ExecuteAsync(
        GovernedLoopAdmissionReceipt admissionReceipt,
        GovernedLoopExecutionBinding executionBinding,
        Func<CancellationToken, Task> commitTerminalCompletion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionReceipt);
        ArgumentNullException.ThrowIfNull(executionBinding);
        ArgumentNullException.ThrowIfNull(commitTerminalCompletion);

        if (!GovernedLoopAdmissionValidator.Validate(admissionReceipt).IsValid
            || !Equals(admissionReceipt.Evidence.Binding, executionBinding))
        {
            return Task.FromResult(Result(
                GovernedLoopFirstBoundRunCompletionDisposition.Rejected,
                GovernedLoopEffectAuthorityUsageStoreStatus.Conflict,
                false,
                "The completion coordinates did not match the immutable admitted run."));
        }

        return _authorityTransaction.ExecuteAsync(
            token => ExecuteUnderAuthorityAsync(admissionReceipt, executionBinding, commitTerminalCompletion, token),
            cancellationToken);
    }

    private async Task<GovernedLoopFirstBoundRunCompletionExecutionResult> ExecuteUnderAuthorityAsync(
        GovernedLoopAdmissionReceipt admissionReceipt,
        GovernedLoopExecutionBinding executionBinding,
        Func<CancellationToken, Task> commitTerminalCompletion,
        CancellationToken cancellationToken)
    {
        var completionConstraint = admissionReceipt.Evidence.GrantBoundary.CompletionConstraint;
        if (completionConstraint == AuthorityGrantCompletionConstraintKind.None)
        {
            await commitTerminalCompletion(cancellationToken).ConfigureAwait(false);
            return Result(
                GovernedLoopFirstBoundRunCompletionDisposition.Completed,
                GovernedLoopEffectAuthorityUsageStoreStatus.Allowed,
                true,
                "The grant has no completion constraint; the successful terminal continuation completed under the shared authority transaction.");
        }

        if (completionConstraint != AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion
            || !TryGetUtcNow(out var evaluatedAtUtc))
        {
            return Result(
                GovernedLoopFirstBoundRunCompletionDisposition.Rejected,
                GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable,
                false,
                "The completion constraint or trusted UTC time was unavailable; terminal completion was not invoked.");
        }

        var operationId = CreateCompletionOperationId(admissionReceipt.ContentHash, executionBinding.RunId);
        var beginRequest = CreateRequest(admissionReceipt, executionBinding, operationId, evaluatedAtUtc);
        GovernedLoopEffectAuthorityUsageStoreResult begin;
        try
        {
            begin = await _usageStore.BeginCompletionAsync(beginRequest, cancellationToken).ConfigureAwait(false)
                ?? new GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(
                GovernedLoopFirstBoundRunCompletionDisposition.Rejected,
                GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous,
                false,
                "The durable pending completion claim was ambiguous; terminal completion was not invoked.");
        }

        if (begin.Status == GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted)
        {
            return Result(
                GovernedLoopFirstBoundRunCompletionDisposition.AlreadyCompleted,
                begin.Status,
                false,
                "The exact run completion was already durable; the callback was not repeated, and the caller must reload and authenticate that exact completed run.");
        }

        if (begin.Status is not (GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending
            or GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyPending))
        {
            return Result(
                GovernedLoopFirstBoundRunCompletionDisposition.Rejected,
                begin.Status,
                false,
                "The grant could not admit this successful terminal continuation.");
        }

        await commitTerminalCompletion(cancellationToken).ConfigureAwait(false);

        if (!TryGetUtcNow(out evaluatedAtUtc))
        {
            return Result(
                GovernedLoopFirstBoundRunCompletionDisposition.NeedsReview,
                GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous,
                true,
                "The terminal continuation succeeded, but trusted UTC time was unavailable before completion evidence could advance.");
        }

        var completeRequest = CreateRequest(admissionReceipt, executionBinding, operationId, evaluatedAtUtc);
        GovernedLoopEffectAuthorityUsageStoreResult completed;
        using var integrityFinalize = new CancellationTokenSource(_integrityFinalizeTimeout);
        try
        {
            completed = await _usageStore.CompleteCompletionAsync(completeRequest, integrityFinalize.Token).ConfigureAwait(false)
                ?? new GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous);
        }
        catch (Exception)
        {
            return Result(
                GovernedLoopFirstBoundRunCompletionDisposition.NeedsReview,
                GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous,
                true,
                "The terminal continuation succeeded, but durable grant completion is ambiguous.");
        }

        var confirmed = completed.Status is GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted
            or GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted;
        return Result(
            confirmed
                ? GovernedLoopFirstBoundRunCompletionDisposition.Completed
                : GovernedLoopFirstBoundRunCompletionDisposition.NeedsReview,
            completed.Status,
            true,
            confirmed
                ? "The successful terminal continuation and durable first-bound-run completion are committed."
                : "The terminal continuation succeeded, but the durable completion ledger did not confirm the exact completion; retain the truthful terminal run with an integrity warning.");
    }

    private static GovernedLoopEffectAuthorityCompletionUsageRequest CreateRequest(
        GovernedLoopAdmissionReceipt admissionReceipt,
        GovernedLoopExecutionBinding executionBinding,
        string operationId,
        DateTimeOffset evaluatedAtUtc)
        => new(
            GovernedLoopEffectAuthorityCompletionUsageRequest.CurrentSchemaVersion,
            admissionReceipt.Intent.AuthorityGrant,
            admissionReceipt.ContentHash,
            executionBinding.RunId,
            executionBinding.ExecutionGeneration,
            operationId,
            evaluatedAtUtc);

    private bool TryGetUtcNow(out DateTimeOffset value)
    {
        value = default;
        try
        {
            var candidate = _timeProvider.GetUtcNow();
            if (candidate == default || candidate.Offset != TimeSpan.Zero)
            {
                return false;
            }

            value = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string CreateCompletionOperationId(string admissionReceiptHash, string runId)
    {
        var payload = Encoding.UTF8.GetBytes(CompletionOperationDomain + "\n" + admissionReceiptHash + "\n" + runId);
        return "run-completion-" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static GovernedLoopFirstBoundRunCompletionExecutionResult Result(
        GovernedLoopFirstBoundRunCompletionDisposition disposition,
        GovernedLoopEffectAuthorityUsageStoreStatus status,
        bool commitInvoked,
        string detail)
        => new(disposition, status, commitInvoked, detail);
}
