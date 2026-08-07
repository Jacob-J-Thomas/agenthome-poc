using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Runtime.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Represents a default conversation loop turn request.
/// </summary>
public sealed record DefaultConversationLoopTurnRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultConversationLoopTurnRequest"/> type.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="responseChunkHandler">The response chunk handler.</param>
    /// <param name="diagnosticHandler">The diagnostic handler.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="requestId">An optional caller-owned idempotency identity; omitted values receive a fresh identity.</param>
    public DefaultConversationLoopTurnRequest(
        string input,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Func<RuntimeDiagnosticMessage, CancellationToken, Task>? diagnosticHandler = null,
        CancellationToken cancellationToken = default,
        string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        Input = input;
        RequestId = string.IsNullOrWhiteSpace(requestId) ? "request-" + Guid.NewGuid().ToString("N") : requestId.Trim();
        if (RequestId.Length > 256)
        {
            throw new ArgumentException("Request id cannot exceed 256 characters.", nameof(requestId));
        }

        ResponseChunkHandler = responseChunkHandler;
        DiagnosticHandler = diagnosticHandler;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the input.
    /// </summary>
    /// <value>The input.</value>
    public string Input { get; }

    /// <summary>
    /// Gets the caller-owned idempotency identity.
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// Gets the response chunk handler func.
    /// </summary>
    /// <value>The response chunk handler func.</value>
    public Func<string, CancellationToken, Task>? ResponseChunkHandler { get; }

    /// <summary>
    /// Gets the diagnostic handler func.
    /// </summary>
    /// <value>The diagnostic handler func.</value>
    public Func<RuntimeDiagnosticMessage, CancellationToken, Task>? DiagnosticHandler { get; }

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    /// <value>The cancellation token.</value>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Converts the supplied value to user message.
    /// </summary>
    /// <returns>The LLM message.</returns>
    public LlmMessage ToUserMessage()
    {
        return LlmMessage.User(Input);
    }
}
