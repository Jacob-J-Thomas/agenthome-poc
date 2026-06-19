using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Inference.Interfaces;

internal interface ICodexAppServerToolBridge
{
    JsonArray CreateToolSpecs();

    Task<JsonObject> HandleToolCallAsync(JsonElement parameters, CancellationToken cancellationToken);
}
