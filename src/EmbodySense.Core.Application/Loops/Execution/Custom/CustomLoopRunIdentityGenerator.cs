namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopRunIdentityGenerator : ICustomLoopRunIdentityGenerator
{
    public string NewRunId() => CustomLoopGeneratedIdentifier.New("run");

    public string NewEventId() => CustomLoopGeneratedIdentifier.New("event");
}
