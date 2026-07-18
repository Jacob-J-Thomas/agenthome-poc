namespace EmbodySense.Core.Application.Loops.Authoring;

public interface ICustomLoopIdentityGenerator
{
    string NewLoopId();

    string NewInferenceStepId();
}
