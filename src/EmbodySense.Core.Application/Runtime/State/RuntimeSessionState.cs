namespace EmbodySense.Core.Application.Runtime.State;

/// <summary>
/// Tracks surface-local exit, active-turn, and diagnostic-verbosity flags.
/// </summary>
public sealed class RuntimeSessionState
{
    /// <summary>
    /// Gets a value indicating whether the exit requested condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the exit requested condition holds; otherwise, <see langword="false"/>.</value>
    public bool ExitRequested { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the model turn started condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the model turn started condition holds; otherwise, <see langword="false"/>.</value>
    public bool ModelTurnStarted { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the verbose condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the verbose condition holds; otherwise, <see langword="false"/>.</value>
    public bool Verbose { get; private set; }

    /// <summary>
    /// Marks the session for exit after the current command boundary.
    /// </summary>
    public void RequestExit()
    {
        ExitRequested = true;
    }

    /// <summary>
    /// Marks that provider work for the current input has begun.
    /// </summary>
    public void MarkModelTurnStarted()
    {
        ModelTurnStarted = true;
    }

    /// <summary>
    /// Clears the active model-turn marker.
    /// </summary>
    public void ResetModelTurn()
    {
        ModelTurnStarted = false;
    }

    /// <summary>
    /// Enables or disables verbose context diagnostics.
    /// </summary>
    /// <param name="enabled">The enabled.</param>
    public void SetVerbose(bool enabled)
    {
        Verbose = enabled;
    }
}
