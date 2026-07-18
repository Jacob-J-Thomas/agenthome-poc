namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopContextDefaults(
    CustomLoopContextPolicy Inference,
    CustomLoopContextPolicy Exit)
{
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
