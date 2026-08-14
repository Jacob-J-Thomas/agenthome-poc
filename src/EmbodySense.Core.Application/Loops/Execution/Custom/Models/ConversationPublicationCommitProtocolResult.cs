namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Returns the exact callback result and non-persisted failure observed for one publication boundary invocation.</summary>
/// <typeparam name="T">The publisher-owned append result type.</typeparam>
/// <param name="Status">The protocol disposition.</param>
/// <param name="Value">The exact callback result, when one completed successfully.</param>
/// <param name="Failure">The callback or boundary failure, when one was observed.</param>
/// <param name="CallbackInvocationCount">The observed callback invocation count.</param>
public sealed record ConversationPublicationCommitProtocolResult<T>(
    ConversationPublicationCommitProtocolStatus Status,
    T? Value,
    Exception? Failure,
    int CallbackInvocationCount)
    where T : class;
