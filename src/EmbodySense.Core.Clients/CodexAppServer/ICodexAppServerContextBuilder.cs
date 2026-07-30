using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

/// <summary>
/// Maps an admitted inference request into the trusted developer-instruction and turn-input text sent to Codex app-server.
/// </summary>
internal interface ICodexAppServerContextBuilder
{
    /// <summary>
    /// Creates the trusted developer-instruction payload for a new app-server thread.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The deterministic developer-instruction text.</returns>
    string CreateDeveloperInstructions(LlmInferenceRequest request);

    /// <summary>
    /// Creates the user-visible turn input for one app-server turn.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The serialized turn-input text.</returns>
    string CreateTurnInput(LlmInferenceRequest request);
}
