namespace EmbodySense.Core.Application.Runtime;

public sealed class RuntimeSessionState
{
    public bool ExitRequested { get; private set; }

    public bool ModelTurnStarted { get; private set; }

    public bool Verbose { get; private set; }

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

    public void SetVerbose(bool enabled)
    {
        Verbose = enabled;
    }
}
