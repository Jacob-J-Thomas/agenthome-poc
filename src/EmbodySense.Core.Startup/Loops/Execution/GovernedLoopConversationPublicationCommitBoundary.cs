using System.Runtime.ExceptionServices;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Projects canonical governed-loop effect authority into one conversation-publication commit boundary.</summary>
public sealed class GovernedLoopConversationPublicationCommitBoundary
{
    private readonly IGovernedLoopEffectAuthorityBoundary _effectAuthorityBoundary;
    private readonly GovernedLoopEffectAuthorityRequest _request;
    private int _executionStarted;

    /// <summary>Creates one single-use publication boundary over complete exact retained run evidence.</summary>
    /// <param name="effectAuthorityBoundary">The canonical durable effect-authority boundary.</param>
    /// <param name="admissionReceipt">The complete exact successful admission receipt retained by the run.</param>
    /// <param name="executionBinding">The exact run, revision, and execution generation.</param>
    /// <param name="graphArtifact">The exact immutable graph artifact retained by the run.</param>
    /// <param name="nodeId">The exact success-Exit node identity.</param>
    /// <param name="nodeAttempt">The exact positive node-attempt number.</param>
    /// <param name="publicationOperationId">The stable identity-bearing conversation publication operation.</param>
    public GovernedLoopConversationPublicationCommitBoundary(
        IGovernedLoopEffectAuthorityBoundary effectAuthorityBoundary,
        GovernedLoopAdmissionReceipt admissionReceipt,
        GovernedLoopExecutionBinding executionBinding,
        GovernedLoopGraphRevisionArtifact graphArtifact,
        string nodeId,
        int nodeAttempt,
        string publicationOperationId)
    {
        _effectAuthorityBoundary = effectAuthorityBoundary ?? throw new ArgumentNullException(nameof(effectAuthorityBoundary));
        _request = ConversationPublicationEffectAuthorityRequestFactory.Create(
            admissionReceipt,
            executionBinding,
            graphArtifact,
            nodeId,
            nodeAttempt,
            publicationOperationId);
    }

    /// <summary>Invokes the exact append once only after a newly appended durable direct decision.</summary>
    /// <param name="commitAppend">The publisher-owned identity-bearing append callback.</param>
    /// <param name="cancellationToken">The token used while entering and holding the authority boundary.</param>
    /// <returns>A task that completes only after the exact append callback completes.</returns>
    /// <exception cref="GovernedLoopEffectAuthorityStoppedException">Thrown when durable authority does not newly and directly admit the publication.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the effect boundary violates its callback or result protocol.</exception>
    public async Task CommitAsync(
        Func<CancellationToken, Task> commitAppend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitAppend);
        if (Interlocked.Exchange(ref _executionStarted, 1) != 0)
        {
            throw Protocol("A conversation-publication authority boundary may be executed only once.");
        }

        var marker = new object();
        var callbackSync = new object();
        var callbackCount = 0;
        var callbackCompleted = false;
        var callbackClosed = false;
        Exception? callbackFailure = null;
        InvalidOperationException? callbackViolation = null;
        using var callbackLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task<object> CommitUnderAuthorityAsync(CancellationToken token)
        {
            lock (callbackSync)
            {
                callbackCount++;
                if (callbackClosed || callbackCount != 1)
                {
                    callbackViolation ??= Protocol("The governed effect boundary invoked the conversation append more than once or after returning.");
                    throw callbackViolation;
                }
            }

            // This yield gives a boundary that captures or fires-and-forgets the callback a chance to
            // close before the identity-bearing append can start.
            await Task.Yield();
            lock (callbackSync)
            {
                if (callbackClosed)
                {
                    // https://github.com/Jacob-J-Thomas/agenthome-poc/issues/507 owns cancellation observability when closure wins before append lifetime creation.
                    callbackViolation ??= Protocol("The governed effect boundary did not await the conversation append while its authority boundary was active.");
                    throw callbackViolation;
                }
            }

            try
            {
                using var appendLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, callbackLifetime.Token);
                await commitAppend(appendLifetime.Token).ConfigureAwait(false);
                lock (callbackSync)
                {
                    callbackCompleted = true;
                }

                return marker;
            }
            catch (Exception exception)
            {
                lock (callbackSync)
                {
                    if (exception is not OperationCanceledException || !callbackClosed || callbackViolation is null)
                    {
                        callbackFailure ??= exception;
                    }
                }

                throw;
            }
        }

        GovernedLoopEffectAuthorityExecutionResult<object>? result = null;
        Exception? boundaryFailure = null;
        try
        {
            result = await _effectAuthorityBoundary
                .ExecuteAsync(_request, CommitUnderAuthorityAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            boundaryFailure = exception;
        }
        finally
        {
            lock (callbackSync)
            {
                callbackClosed = true;
                if (callbackCount == 1 && !callbackCompleted)
                {
                    callbackViolation ??= Protocol("The governed effect boundary returned before awaiting the conversation append to completion.");
                }
            }

            await callbackLifetime.CancelAsync().ConfigureAwait(false);
        }

        Exception? retainedCallbackFailure;
        InvalidOperationException? retainedCallbackViolation;
        int observedCallbackCount;
        bool observedCallbackCompletion;
        lock (callbackSync)
        {
            retainedCallbackFailure = callbackFailure;
            retainedCallbackViolation = callbackViolation;
            observedCallbackCount = callbackCount;
            observedCallbackCompletion = callbackCompleted;
        }

        if (retainedCallbackFailure is not null)
        {
            ExceptionDispatchInfo.Capture(retainedCallbackFailure).Throw();
        }

        if (retainedCallbackViolation is not null)
        {
            throw retainedCallbackViolation;
        }

        if (boundaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(boundaryFailure).Throw();
        }

        var outcome = ValidateResult(result, observedCallbackCount, observedCallbackCompletion, marker);
        if (outcome == PublicationAuthorityOutcome.Direct)
        {
            return;
        }

        throw new GovernedLoopEffectAuthorityStoppedException(
            CreateStoppedMessage(result!),
            result!.Status,
            result.EvidenceStatus,
            result.Decision);
    }

    private PublicationAuthorityOutcome ValidateResult(
        GovernedLoopEffectAuthorityExecutionResult<object>? result,
        int callbackCount,
        bool callbackCompleted,
        object marker)
    {
        if (result is null || !Enum.IsDefined(result.Status) || !Enum.IsDefined(result.EvidenceStatus))
        {
            throw Protocol("The governed effect boundary returned a malformed conversation-publication result.");
        }

        if (callbackCount is < 0 or > 1
            || result.CommitInvoked != (callbackCount == 1)
            || (!result.CommitInvoked && result.Result is not null))
        {
            throw Protocol("The governed effect boundary returned a result inconsistent with its conversation-append invocation count.");
        }

        if (callbackCount == 1 && !callbackCompleted)
        {
            throw Protocol("The governed effect boundary returned before the conversation append completed.");
        }

        if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.Decided)
        {
            if (result.Decision is null
                || result.EvidenceStatus is not (GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                    or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)
                || !GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(result.Decision, _request))
            {
                throw Protocol("A decided governed effect did not carry exact durable authority evidence for this conversation publication.");
            }

            if (result.Decision.Disposition == GovernedLoopEffectAuthorityDisposition.Direct
                && result.EvidenceStatus == GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended)
            {
                if (callbackCount != 1
                    || !result.CommitInvoked
                    || !callbackCompleted
                    || !ReferenceEquals(result.Result, marker))
                {
                    throw Protocol("Direct governed publication authority did not return the same completed single-use append marker.");
                }

                return PublicationAuthorityOutcome.Direct;
            }

            if (callbackCount != 0 || result.CommitInvoked || result.Result is not null)
            {
                throw Protocol("A stopped or replayed governed publication decision invoked the conversation append.");
            }

            return PublicationAuthorityOutcome.Stopped;
        }

        if (result.Status is GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest
            or GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable)
        {
            if (result.Decision is not null
                || result.EvidenceStatus != GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown
                || callbackCount != 0
                || result.CommitInvoked
                || result.Result is not null)
            {
                throw Protocol("An unavailable or invalid governed publication result carried contradictory authority evidence.");
            }

            return PublicationAuthorityOutcome.Stopped;
        }

        if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected)
        {
            if (result.EvidenceStatus == GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown
                || result.Decision is null
                || !GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(result.Decision, _request)
                || result.Decision.Disposition == GovernedLoopEffectAuthorityDisposition.Direct
                || callbackCount != 0
                || result.CommitInvoked
                || result.Result is not null)
            {
                throw Protocol("An evidence-rejected governed publication result crossed or contradicted the conversation append boundary.");
            }

            return PublicationAuthorityOutcome.Stopped;
        }

        throw Protocol("The governed effect boundary returned an unsupported conversation-publication execution status.");
    }

    private static string CreateStoppedMessage(GovernedLoopEffectAuthorityExecutionResult<object> result)
    {
        var disposition = result.Decision?.Disposition.ToString() ?? "none";
        return $"Conversation publication stopped at governed effect authority ({result.Status}/{result.EvidenceStatus}/{disposition}).";
    }

    private static InvalidOperationException Protocol(string message) => new(message);

}
