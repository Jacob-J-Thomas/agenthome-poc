using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Coordinates one bounded selection, exact authority revalidation, durable intent, and governed dispatch.</summary>
/// <remarks>
/// This service never runs in the background. Caller cancellation is honored before intent. Once intent is durable, dispatch
/// proceeds independently of the caller token and every exception, cancellation, or lost response is recorded as
/// <see cref="TriggerDispatchOutcome.NeedsReview"/> rather than fabricated as a proved rejection.
/// </remarks>
public sealed class TriggerWorkerService
{
    private readonly ITriggerWorkerStatePort _state;
    private readonly ITriggerDispatchAuthorizer _authorizer;
    private readonly ITriggerWorkerDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a one-shot worker over composition-owned state, authority, and dispatch ports.</summary>
    public TriggerWorkerService(ITriggerWorkerStatePort state, ITriggerDispatchAuthorizer authorizer, ITriggerWorkerDispatcher dispatcher, TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs at most one eligible trigger entry through the durable dispatch boundary.</summary>
    /// <param name="request">The exact selection inputs.</param>
    /// <param name="cancellationToken">A token honored until durable intent is recorded.</param>
    /// <returns>The selection and final durable posture.</returns>
    public async Task<TriggerWorkerRunResult> RunOnceAsync(TriggerWorkerRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selection);
        var selected = await _state.SelectAsync(request.Selection, cancellationToken).ConfigureAwait(false);
        if (selected.Status != TriggerWorkerSelectionStatus.Acquired || selected.Entry?.WorkerLease is not { } lease || selected.Envelope is null)
        {
            return new TriggerWorkerRunResult(selected.Status, null, selected.Entry);
        }

        TriggerDispatchAuthorization authorization;
        try
        {
            authorization = await _authorizer.AuthorizeAsync(selected.Envelope, UtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var released = await _state.ReleaseAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, UtcNow(), CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, released.Status, released.Entry);
        }
        catch (Exception exception)
        {
            authorization = new TriggerDispatchAuthorization(TriggerDispatchAuthorizationStatus.Unavailable, new string('0', 64), $"Current dispatch evidence was unavailable: {exception.GetType().Name}.");
        }

        try
        {
            ValidateAuthorization(authorization);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            authorization = new TriggerDispatchAuthorization(TriggerDispatchAuthorizationStatus.Unavailable, new string('0', 64), $"Current dispatch evidence was malformed: {exception.GetType().Name}.");
        }
        var operationId = TriggerWorkerRequestHash.ComputeOperationId(selected.Entry.DeliveryId, lease.Generation);
        var requestHash = TriggerWorkerRequestHash.Compute(selected.Envelope, lease, authorization.EvidenceHash);
        var now = UtcNow();
        if (authorization.Status != TriggerDispatchAuthorizationStatus.Authorized)
        {
            var detail = Bound(authorization.Detail);
            var rejection = new TriggerDispatchEvidence(operationId, requestHash, authorization.EvidenceHash, now, TriggerDispatchOutcome.Rejected, now, detail);
            var rejected = await _state.RejectBeforeDispatchAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, rejection, CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, rejected.Status, rejected.Entry);
        }

        var intent = new TriggerDispatchEvidence(operationId, requestHash, authorization.EvidenceHash, now, TriggerDispatchOutcome.IntentRecorded, null, "Durable dispatch intent recorded after exact current-evidence revalidation.");
        TriggerWorkerMutationResult begun;
        try
        {
            begun = await _state.BeginDispatchAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var released = await _state.ReleaseAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, UtcNow(), CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, released.Status, released.Entry);
        }
        if (begun.Status is not (TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed) || begun.Entry is null)
        {
            return new TriggerWorkerRunResult(selected.Status, begun.Status, begun.Entry);
        }

        TriggerWorkerDispatchResult dispatch;
        try
        {
            dispatch = await _dispatcher.DispatchAsync(selected.Envelope, intent, CancellationToken.None).ConfigureAwait(false);
            ValidateDispatch(dispatch, selected.Envelope, intent);
        }
        catch (Exception exception)
        {
            dispatch = new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, $"Governed dispatch outcome is ambiguous: {exception.GetType().Name}.");
        }

        var completedAtUtc = UtcNow();
        var outcome = intent with { Outcome = dispatch.Outcome, OutcomeRecordedAtUtc = completedAtUtc, Detail = Bound(dispatch.Detail), GovernedInvocation = dispatch.GovernedInvocation };
        var completed = await _state.CompleteDispatchAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, begun.Entry.Revision, outcome, CancellationToken.None).ConfigureAwait(false);
        return new TriggerWorkerRunResult(selected.Status, completed.Status, completed.Entry);
    }

    private DateTimeOffset UtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private static void ValidateAuthorization(TriggerDispatchAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!Enum.IsDefined(authorization.Status) || !IsHash(authorization.EvidenceHash) || string.IsNullOrWhiteSpace(authorization.Detail))
        {
            throw new InvalidOperationException("The dispatch authorizer returned malformed current evidence.");
        }
    }

    private static void ValidateDispatch(TriggerWorkerDispatchResult dispatch, TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var requiresReceipt = dispatch.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal;
        if (dispatch.Outcome is not (TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal or TriggerDispatchOutcome.Rejected or TriggerDispatchOutcome.NeedsReview)
            || string.IsNullOrWhiteSpace(dispatch.Detail)
            || requiresReceipt != (dispatch.GovernedInvocation is not null)
            || dispatch.GovernedInvocation is { } governed && (!string.Equals(governed.OperationId, intent.OperationId, StringComparison.Ordinal)
                || !IsArtifactId(governed.RunId, TriggerWorkerLimits.MaxGovernedRunIdCharacters)
                || !IsHash(governed.AdmissionRequestHash)
                || !string.Equals(governed.LoopId, envelope.Loop.LoopId, StringComparison.Ordinal)
                || governed.DefinitionVersion != envelope.Loop.DefinitionVersion
                || !string.Equals(governed.DefinitionHash, envelope.Loop.ContentHash, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The governed dispatcher returned an unsupported outcome.");
        }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsArtifactId(string value, int maximumLength) => !string.IsNullOrEmpty(value) && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string Bound(string detail)
    {
        var normalized = string.IsNullOrWhiteSpace(detail) ? "No outcome detail was supplied." : detail.Trim();
        return normalized.Length <= TriggerWorkerLimits.MaxOutcomeDetailCharacters ? normalized : normalized[..TriggerWorkerLimits.MaxOutcomeDetailCharacters];
    }
}
