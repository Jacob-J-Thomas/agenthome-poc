using System.Text.Json.Nodes;

namespace EmbodySense.Core.Clients.CodexAppServer.Models;

/// <summary>
/// Reports whether a server-initiated JSON-RPC request was recognized and the result to return.
/// </summary>
/// <param name="Handled">Whether the request handler owns the method.</param>
/// <param name="Result">The JSON-RPC result payload for a handled method, or <see langword="null"/> when unhandled.</param>
internal sealed record CodexAppServerRequestHandlingResult(bool Handled, JsonObject? Result);
