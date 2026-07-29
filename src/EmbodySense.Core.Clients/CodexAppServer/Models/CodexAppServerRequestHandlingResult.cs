using System.Text.Json.Nodes;

namespace EmbodySense.Core.Clients.CodexAppServer.Models;

internal sealed record CodexAppServerRequestHandlingResult(bool Handled, JsonObject? Result);
