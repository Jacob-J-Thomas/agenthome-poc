namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopRunIdentityGenerator
{
    string NewRunId();

    string NewEventId();
}
