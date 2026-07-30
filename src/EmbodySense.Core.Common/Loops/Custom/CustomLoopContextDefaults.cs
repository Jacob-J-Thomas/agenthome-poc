using EmbodySense.Core.Common.Loops.Models.Custom;
namespace EmbodySense.Core.Common.Loops.Custom;

/// <summary>
/// Defines the resolved default context policy for custom-loop inference and exit nodes.
/// </summary>
/// <param name="Inference">The inference.</param>
/// <param name="Exit">The exit.</param>
public sealed record CustomLoopContextDefaults(
    CustomLoopContextPolicy Inference,
    CustomLoopContextPolicy Exit)
{
    /// <summary>
    /// Creates the current first-wave context defaults.
    /// </summary>
    /// <returns>Defaults that expose role and trigger context to both node kinds, retain inference output for loop reasoning, and publish only exit output to the invoking conversation.</returns>
    public static CustomLoopContextDefaults CreatePrototypeDefaults()
    {
        var sharedInput = new CustomLoopContextInputPolicy(
            IncludeRoleContext: true,
            IncludeTriggerPrompt: true,
            IncludeInvokingConversation: false,
            IncludeEarlierRetainedOutputs: true,
            IncludePreviousIterationResult: true);

        return new CustomLoopContextDefaults(
            new CustomLoopContextPolicy(sharedInput, new CustomLoopContextOutputPolicy(RetainForLoopReasoning: true, PublishToInvokingConversation: false)),
            new CustomLoopContextPolicy(sharedInput, new CustomLoopContextOutputPolicy(RetainForLoopReasoning: false, PublishToInvokingConversation: true)));
    }
}
