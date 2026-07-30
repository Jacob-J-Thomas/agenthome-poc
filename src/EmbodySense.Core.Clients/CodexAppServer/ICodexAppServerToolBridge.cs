using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Clients.CodexAppServer;

/// <summary>
/// Maps governed EmbodySense tool commands to app-server dynamic-tool specifications and brokered call results.
/// </summary>
internal interface ICodexAppServerToolBridge
{
    /// <summary>
    /// Creates the protocol tool specifications for the currently permitted command set.
    /// </summary>
    /// <returns>A deterministic JSON array of dynamic-tool declarations.</returns>
    JsonArray CreateToolSpecs();

    /// <summary>
    /// Validates and dispatches one app-server dynamic-tool call through the governed tool broker.
    /// </summary>
    /// <param name="parameters">The exact app-server dynamic-tool parameters.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the JSON object.</returns>
    Task<JsonObject> HandleToolCallAsync(JsonElement parameters, CancellationToken cancellationToken);
}
