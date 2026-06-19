using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Inference.Interfaces;

internal interface ICodexAppServerRequestHandler
{
    Task<CodexAppServerRequestHandlingResult> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}

internal sealed record CodexAppServerRequestHandlingResult(bool Handled, JsonObject? Result);
