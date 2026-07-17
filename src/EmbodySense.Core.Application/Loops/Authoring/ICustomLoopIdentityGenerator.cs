namespace EmbodySense.Core.Application.Loops.Authoring;

public interface ICustomLoopIdentityGenerator
{
    string NewLoopId();

    string NewInferenceStepId();
}

public sealed class CustomLoopIdentityGenerator : ICustomLoopIdentityGenerator
{
    public string NewLoopId() => $"loop-{Guid.NewGuid():N}";

    public string NewInferenceStepId() => $"step-{Guid.NewGuid():N}";
}
