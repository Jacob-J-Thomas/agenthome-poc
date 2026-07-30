using EmbodySense.Core.Application.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Runs serialized turns through the default interactive conversation loop.
/// </summary>
public interface IDefaultConversationLoopRunner
{
    /// <summary>
    /// Executes one complete conversation turn.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The completed, cancelled, or failed turn result.</returns>
    Task<DefaultConversationLoopTurnResult> RunTurnAsync(DefaultConversationLoopTurnRequest request);
}
