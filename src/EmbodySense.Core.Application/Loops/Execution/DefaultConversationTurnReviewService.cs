using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Lists and explicitly resolves outcome-unknown default-conversation turns under workspace ownership.
/// </summary>
public sealed class DefaultConversationTurnReviewService
{
    private readonly IDefaultConversationTurnStore _turns;
    private readonly IQuarantinableInferenceClient _inferenceClient;
    private readonly IConversationWorkspaceLease _workspaceLease;

    /// <summary>
    /// Initializes the review service over durable turns, provider quarantine, and workspace ownership.
    /// </summary>
    public DefaultConversationTurnReviewService(
        IDefaultConversationTurnStore turns,
        IQuarantinableInferenceClient inferenceClient,
        IConversationWorkspaceLease workspaceLease)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(workspaceLease);

        _turns = turns;
        _inferenceClient = inferenceClient;
        _workspaceLease = workspaceLease;
    }

    /// <summary>
    /// Lists every unresolved needs-review turn in deterministic start order.
    /// </summary>
    public async Task<IReadOnlyList<DefaultConversationTurnRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _turns.ListNeedsReviewAsync(cancellationToken);
    }

    /// <summary>
    /// Quarantines live provider state and durably records explicit abandonment of an outcome-unknown attempt without redispatch or publication.
    /// </summary>
    public async Task<DefaultConversationTurnRecord?> ResolveAsync(string turnId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        using var lease = await _workspaceLease.AcquireAsync(cancellationToken);
        var current = await _turns.LoadAsync(turnId, cancellationToken);
        if (current is null || current.Checkpoint == DefaultConversationTurnCheckpoint.ReviewResolved)
        {
            return current;
        }

        if (current.Checkpoint != DefaultConversationTurnCheckpoint.Terminal || current.Run.Status != LoopRunStatus.NeedsReview)
        {
            throw new InvalidOperationException($"Default-conversation turn `{turnId}` is not an unresolved NeedsReview turn.");
        }

        if (!DefaultConversationTurnProtocol.CanAbandonReview(current))
        {
            throw new InvalidOperationException($"Default-conversation turn `{turnId}` is classified as {DefaultConversationTurnProtocol.GetReviewClassification(current)} and cannot be abandoned. {DefaultConversationTurnProtocol.GetReviewAction(current)}");
        }

        await _inferenceClient.QuarantineAsync(cancellationToken);
        var candidate = current.ResolveReview(DateTimeOffset.UtcNow);
        var update = await _turns.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken);
        if (update.Status is DefaultConversationTurnStoreStatus.Updated or DefaultConversationTurnStoreStatus.Replay && update.Record is not null)
        {
            return update.Record;
        }

        var latest = await _turns.LoadAsync(turnId, cancellationToken);
        if (latest?.Checkpoint == DefaultConversationTurnCheckpoint.ReviewResolved)
        {
            return latest;
        }

        throw new InvalidOperationException($"Default-conversation review resolution for `{turnId}` conflicted with another durable update.");
    }
}
