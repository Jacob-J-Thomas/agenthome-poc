namespace EmbodySense.Core.Application.Inference;

/// <summary>
/// Exposes provider conversation-state reset when the runtime transcript projection changes.
/// </summary>
public interface IResettableInferenceClient
{
    /// <summary>
    /// Discards any provider-side conversation state represented by prior requests.
    /// </summary>
    void ResetConversation();
}
