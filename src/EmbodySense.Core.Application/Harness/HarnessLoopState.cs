namespace EmbodySense.Core.Application.Harness;

public sealed class HarnessLoopState
{
    public bool ExitRequested { get; private set; }

    public bool ModelTurnStarted { get; private set; }

    public void RequestExit()
    {
        ExitRequested = true;
    }

    public void MarkModelTurnStarted()
    {
        ModelTurnStarted = true;
    }

    public void ResetModelTurn()
    {
        ModelTurnStarted = false;
    }
}
