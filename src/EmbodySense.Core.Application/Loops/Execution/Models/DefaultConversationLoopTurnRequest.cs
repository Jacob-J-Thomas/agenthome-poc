using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Runtime.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Models;

public sealed record DefaultConversationLoopTurnRequest
{
    public DefaultConversationLoopTurnRequest(
        string input,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Func<RuntimeDiagnosticMessage, CancellationToken, Task>? diagnosticHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        Input = input;
        ResponseChunkHandler = responseChunkHandler;
        DiagnosticHandler = diagnosticHandler;
        CancellationToken = cancellationToken;
    }

    public string Input { get; }

    public Func<string, CancellationToken, Task>? ResponseChunkHandler { get; }

    public Func<RuntimeDiagnosticMessage, CancellationToken, Task>? DiagnosticHandler { get; }

    public CancellationToken CancellationToken { get; }

    public LlmMessage ToUserMessage()
    {
        return LlmMessage.User(Input);
    }
}
