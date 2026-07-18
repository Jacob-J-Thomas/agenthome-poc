namespace EmbodySense.Core.Application.Loops.Authoring;

public sealed class CustomLoopIdentityGenerator : ICustomLoopIdentityGenerator
{
    public string NewLoopId() => $"loop-{Guid.NewGuid():N}";

    public string NewInferenceStepId() => $"step-{Guid.NewGuid():N}";
}
