namespace EmbodySense.Core.Application.Loops.Authoring;

public sealed class CustomLoopDefinitionIdentityGenerator : ICustomLoopDefinitionIdentityGenerator
{
    public string NewLoopId() => CustomLoopGeneratedIdentifier.New("loop");

    public string NewInferenceStepId() => CustomLoopGeneratedIdentifier.New("step");
}
