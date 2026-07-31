using System.Text.Json;
using EmbodySense.Core.Clients.CodexAppServer.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

/// <summary>
/// Handles native app-server requests that arrive while an EmbodySense request is awaiting a response.
/// </summary>
internal interface ICodexAppServerRequestHandler
{
    /// <summary>
    /// Handles or declines one server-initiated protocol request and produces its JSON-RPC result.
    /// </summary>
    /// <param name="method">The exact app-server method name.</param>
    /// <param name="parameters">The method parameters supplied by app-server.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The handling disposition and response payload; unrecognized notifications are not handled.</returns>
    Task<CodexAppServerRequestHandlingResult> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}
