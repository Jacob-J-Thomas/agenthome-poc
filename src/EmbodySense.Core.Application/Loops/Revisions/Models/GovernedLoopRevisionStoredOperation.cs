using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Binds one workspace-global operation identifier to the graph and exact terminal evidence that first consumed it.</summary>
/// <param name="GraphId">The graph identifier bound to the operation.</param>
/// <param name="Evidence">The exact durable operation evidence.</param>
public sealed record GovernedLoopRevisionStoredOperation(
    string GraphId,
    GovernedLoopRevisionOperationEvidence Evidence);
