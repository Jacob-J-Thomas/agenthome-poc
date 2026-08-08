using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Reconciles one exact browser-owned default-conversation request against durable turn and transcript evidence.
/// </summary>
/// <remarks>
/// The projection intentionally excludes transcript content, provider-private data, approval payloads, correlation
/// identities, and retained failure details. Incomplete turns are recovered through the existing crash-consistent
/// protocol before their disposition is projected, and provider work is never redispatched by this reader.
/// </remarks>
public sealed class DefaultConversationRequestReconciliationReader
{
    private readonly DefaultConversationTurnStore _turns;
    private readonly DefaultConversationTurnRecoveryService _recovery;

    /// <summary>
    /// Initializes a reconciliation reader for one workspace.
    /// </summary>
    /// <param name="workingDirectory">The workspace root whose durable request evidence is authoritative.</param>
    public DefaultConversationRequestReconciliationReader(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var paths = new WorkspacePaths(workingDirectory);
        _turns = new DefaultConversationTurnStore(paths);
        _recovery = new DefaultConversationTurnRecoveryService(
            _turns,
            new ConversationMemoryStore(paths),
            new LoopRunStore(paths),
            new FileConversationWorkspaceLease(paths));
    }

    /// <summary>
    /// Classifies one exact request after reconciling any incomplete durable checkpoint.
    /// </summary>
    /// <param name="requestId">The canonical browser-owned request identity.</param>
    /// <param name="message">The exact canonical user message retained by the browser.</param>
    /// <param name="cancellationToken">The token used to cancel durable reads and recovery.</param>
    /// <returns>A bounded disposition that tells the browser whether the identity must remain reserved.</returns>
    /// <exception cref="ArgumentException">The request identity or message is blank, noncanonical, or oversized.</exception>
    /// <exception cref="FormatException">Persisted turn, transcript, or run evidence is corrupt or unsupported.</exception>
    public async Task<DefaultConversationRequestReconciliationSnapshot> ReadAsync(
        string requestId,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var canonicalRequestId = requestId.Trim();
        var canonicalMessage = message.Trim();
        if (!string.Equals(requestId, canonicalRequestId, StringComparison.Ordinal) || canonicalRequestId.Length > 256)
        {
            throw new ArgumentException("The default-conversation request identity was noncanonical or oversized.", nameof(requestId));
        }

        if (!string.Equals(message, canonicalMessage, StringComparison.Ordinal))
        {
            throw new ArgumentException("The default-conversation message was noncanonical.", nameof(message));
        }

        var turnId = DefaultConversationTurnProtocol.CreateTurnId(canonicalRequestId);
        var record = await _turns.LoadAsync(turnId, cancellationToken);
        if (record is null)
        {
            return Snapshot("not-found", retrySameRequest: true, releaseRequestIdentity: false);
        }

        if (!string.Equals(record.RequestId, canonicalRequestId, StringComparison.Ordinal)
            || !string.Equals(record.UserMessage.Content, canonicalMessage, StringComparison.Ordinal))
        {
            return Snapshot("conflict", retrySameRequest: false, releaseRequestIdentity: false);
        }

        if (record.Checkpoint < DefaultConversationTurnCheckpoint.Terminal)
        {
            await _recovery.RecoverAsync(cancellationToken);
            record = await _turns.LoadAsync(turnId, cancellationToken)
                ?? throw new InvalidOperationException("The durable default-conversation request disappeared during reconciliation.");
        }

        if (record.Checkpoint < DefaultConversationTurnCheckpoint.Terminal)
        {
            return Snapshot("pending", retrySameRequest: true, releaseRequestIdentity: false);
        }

        if (record.Checkpoint == DefaultConversationTurnCheckpoint.ReviewResolved)
        {
            return Snapshot("rejected", retrySameRequest: false, releaseRequestIdentity: true);
        }

        return record.Run.Status switch
        {
            LoopRunStatus.Completed when record.AssistantMessage is not null => Snapshot("completed", retrySameRequest: false, releaseRequestIdentity: true),
            LoopRunStatus.Failed or LoopRunStatus.Cancelled => Snapshot("rejected", retrySameRequest: false, releaseRequestIdentity: true),
            LoopRunStatus.NeedsReview => Snapshot("needs-review", retrySameRequest: false, releaseRequestIdentity: false),
            _ => Snapshot("conflict", retrySameRequest: false, releaseRequestIdentity: false)
        };
    }

    private static DefaultConversationRequestReconciliationSnapshot Snapshot(string status, bool retrySameRequest, bool releaseRequestIdentity)
    {
        return new DefaultConversationRequestReconciliationSnapshot(status, retrySameRequest, releaseRequestIdentity);
    }
}
