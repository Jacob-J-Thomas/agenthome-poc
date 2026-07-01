using EmbodySense.Core.Application.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

public interface IDefaultConversationLoopRunner
{
    Task<DefaultConversationLoopTurnResult> RunTurnAsync(DefaultConversationLoopTurnRequest request);
}
