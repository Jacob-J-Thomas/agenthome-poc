using System.Text.Json;

namespace EmbodySense.Core.Clients.CodexAppServer;

internal interface ICodexAppServerRequestHandler
{
    Task<CodexAppServerRequestHandlingResult> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}
