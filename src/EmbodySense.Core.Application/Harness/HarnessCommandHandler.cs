using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;

namespace EmbodySense.Core.Application.Harness;

public sealed class HarnessCommandHandler
{
    private readonly HarnessCommandService _commandService;
    private readonly IHarnessClient _client;

    public HarnessCommandHandler(
        IHarnessClient client,
        IConversationMemoryStore? conversationMemoryStore = null,
        IReadOnlyList<LlmMessage>? startupMessages = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _commandService = new HarnessCommandService(conversationMemoryStore, startupMessages);
    }

    public async Task<bool> TryHandleAsync(
        string input,
        AgentHarnessSession session,
        HarnessLoopState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);

        var result = await _commandService.TryHandleAsync(input, session, state, cancellationToken: cancellationToken);
        if (!result.Handled)
        {
            return false;
        }

        await WriteResultAsync(result, session, state, cancellationToken);
        return true;
    }

    private async Task WriteResultAsync(
        HarnessCommandResult result,
        AgentHarnessSession session,
        HarnessLoopState state,
        CancellationToken cancellationToken)
    {
        WriteResult(result);
        if (!result.AwaitingInput)
        {
            return;
        }

        var answer = _client.ReadLine() ?? "";
        var answerResult = await _commandService.TryHandleAsync(answer, session, state, cancellationToken: cancellationToken);
        WriteResult(answerResult);
    }

    private void WriteResult(HarnessCommandResult result)
    {
        if (!string.IsNullOrEmpty(result.Output))
        {
            _client.WriteLine(result.Output);
        }

        if (!string.IsNullOrEmpty(result.Prompt))
        {
            _client.Write(result.Prompt);
        }
    }
}
