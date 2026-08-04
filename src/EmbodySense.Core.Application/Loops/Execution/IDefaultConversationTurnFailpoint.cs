using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Provides an injectable process-loss seam immediately after each durable turn boundary.
/// </summary>
public interface IDefaultConversationTurnFailpoint
{
    /// <summary>
    /// Runs after a named boundary commits and before the next operation starts.
    /// </summary>
    Task AfterBoundaryAsync(DefaultConversationTurnBoundary boundary, DefaultConversationTurnRecord record, CancellationToken cancellationToken = default);
}
