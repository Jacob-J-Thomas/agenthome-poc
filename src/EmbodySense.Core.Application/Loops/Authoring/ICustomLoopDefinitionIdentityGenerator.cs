namespace EmbodySense.Core.Application.Loops.Authoring;

public interface ICustomLoopDefinitionIdentityGenerator
{
    string NewLoopId();

    string NewInferenceStepId();
}
