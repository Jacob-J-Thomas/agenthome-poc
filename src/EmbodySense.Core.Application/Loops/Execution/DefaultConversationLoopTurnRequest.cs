using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Runtime;

namespace EmbodySense.Core.Application.Loops.Execution;

public sealed record DefaultConversationLoopTurnRequest
{
    // TODO(default-loop-models): Move default-loop request/result/status contracts under a Models folder or collapse the Execution namespace if no sibling loop namespaces emerge.
    // Deferred to keep this cutover reviewable; revisit once the current harness-to-runtime rename is committed and folder moves can be staged cleanly.
    public DefaultConversationLoopTurnRequest(
        string input,
        RuntimeSurface surface,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Func<RuntimeDiagnosticMessage, CancellationToken, Task>? diagnosticHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(surface);

        Input = input;
        Surface = surface;
        ResponseChunkHandler = responseChunkHandler;
        DiagnosticHandler = diagnosticHandler;
        CancellationToken = cancellationToken;
    }

    public string Input { get; }

    // TODO(default-loop-surface): Wire Surface into run identity/audit metadata or remove it from this request contract.
    // Deferred because surface ids are only validated today; revisit when default-loop run records are persisted instead of inferred from host context.
    public RuntimeSurface Surface { get; }

    public Func<string, CancellationToken, Task>? ResponseChunkHandler { get; }

    public Func<RuntimeDiagnosticMessage, CancellationToken, Task>? DiagnosticHandler { get; }

    public CancellationToken CancellationToken { get; }

    public LlmMessage ToUserMessage()
    {
        return LlmMessage.User(Input);
    }
}
