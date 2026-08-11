namespace EmbodySense.Core.Application.Inference;

/// <summary>Fences the one irreversible provider transport write behind a caller-owned durable commit boundary.</summary>
/// <param name="commitTransportWrite">The provider-owned transport write callback, which must be invoked at most once.</param>
/// <param name="cancellationToken">The token used while entering and holding the boundary.</param>
public delegate Task InferenceProviderTransportCommitBoundary(
    Func<CancellationToken, Task> commitTransportWrite,
    CancellationToken cancellationToken);
