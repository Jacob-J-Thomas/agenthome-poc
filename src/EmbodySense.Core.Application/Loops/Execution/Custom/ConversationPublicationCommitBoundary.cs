namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Fences one identity-bearing conversation append behind a caller-owned durable commit boundary.</summary>
/// <param name="commitAppend">The publisher-owned append callback, which must be invoked at most once.</param>
/// <param name="cancellationToken">The token used while entering and holding the boundary.</param>
public delegate Task ConversationPublicationCommitBoundary(
    Func<CancellationToken, Task> commitAppend,
    CancellationToken cancellationToken);
