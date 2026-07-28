using System.Text.Json.Nodes;

namespace EmbodySense.Core.Clients.CodexAppServer;

internal sealed record CodexAppServerRequestHandlingResult(bool Handled, JsonObject? Result);
