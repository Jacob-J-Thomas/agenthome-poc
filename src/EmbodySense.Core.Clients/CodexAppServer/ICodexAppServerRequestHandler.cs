using System.Text.Json;
using EmbodySense.Core.Clients.CodexAppServer.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

internal interface ICodexAppServerRequestHandler
{
    Task<CodexAppServerRequestHandlingResult> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}
