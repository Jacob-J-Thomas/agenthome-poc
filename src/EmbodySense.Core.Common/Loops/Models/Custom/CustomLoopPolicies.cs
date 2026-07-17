using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

public enum CustomLoopTriggerPromptSource
{
    Unknown = 0,
    Invocation = 1,
    Preset = 2,
    None = 3
}

public enum CustomLoopContextPolicyMode
{
    Unknown = 0,
    Inherit = 1,
    Custom = 2
}

public enum CustomLoopToolAssignment
{
    Unknown = 0,
    List = 1,
    Read = 2,
    Search = 3
}

public sealed record CustomLoopTriggerPolicy(
    CustomLoopTriggerPromptSource PromptSource,
    string PresetPrompt,
    [property: JsonRequired] bool IncludeInvokingConversation);

public sealed record CustomLoopContextInputPolicy(
    [property: JsonRequired] bool IncludeRoleContext,
    [property: JsonRequired] bool IncludeTriggerPrompt,
    [property: JsonRequired] bool IncludeInvokingConversation,
    [property: JsonRequired] bool IncludeEarlierRetainedOutputs,
    [property: JsonRequired] bool IncludePreviousIterationResult);

public sealed record CustomLoopContextOutputPolicy(
    [property: JsonRequired] bool RetainForLoopReasoning,
    [property: JsonRequired] bool PublishToInvokingConversation);

public sealed record CustomLoopContextPolicy(
    CustomLoopContextInputPolicy ContextIn,
    CustomLoopContextOutputPolicy ContextOut);

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

public sealed record CustomLoopNodeContextPolicy(
    CustomLoopContextPolicyMode Mode,
    CustomLoopContextPolicy? CustomPolicy)
{
    public static CustomLoopNodeContextPolicy Inherit()
    {
        return new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Inherit, null);
    }

    public static CustomLoopNodeContextPolicy Override(CustomLoopContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Custom, policy);
    }
}

public sealed record CustomLoopInferenceStep(
    string Id,
    string Name,
    string Instruction,
    CustomLoopNodeContextPolicy ContextPolicy);

public sealed record CustomLoopExitPolicy(
    int MaxAdditionalIterations,
    string DecisionInstruction,
    CustomLoopNodeContextPolicy ContextPolicy);
