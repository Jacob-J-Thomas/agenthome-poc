namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects the one canonical publication disposition for a durable conversation-publication operation.
/// </summary>
/// <param name="OperationId">The stable idempotency identity shared by every correlated publication phase.</param>
/// <param name="Disposition">The authoritative disposition: <c>Pending</c>, <c>Published</c>, <c>AlreadyPublished</c>, <c>OmittedNoInvokingConversation</c>, <c>DefinitelyFailed</c>, <c>Uncertain</c>, <c>DuplicateTerminalOutcomes</c>, or <c>ConflictingTerminalOutcomes</c>.</param>
/// <param name="Detail">A bounded, public explanation of the disposition that does not expose provider-private reasoning.</param>
/// <param name="IsDefinite">Whether the disposition proves the publication effect did or did not happen.</param>
/// <param name="HasIntegrityWarning">Whether the correlated terminal evidence is malformed and requires integrity review.</param>
/// <param name="EventSequences">The ordered durable event sequences contributing to this operation.</param>
public sealed record LoopRunConversationPublicationDispositionSnapshot(
    string OperationId,
    string Disposition,
    string Detail,
    bool IsDefinite,
    bool HasIntegrityWarning,
    IReadOnlyList<long> EventSequences);
