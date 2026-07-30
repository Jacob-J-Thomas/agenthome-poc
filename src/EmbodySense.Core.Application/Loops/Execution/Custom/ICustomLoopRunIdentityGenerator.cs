namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Generates canonical identifiers for custom-loop runs and trace events.
/// </summary>
public interface ICustomLoopRunIdentityGenerator
{
    /// <summary>
    /// Creates a new run identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    string NewRunId();

    /// <summary>
    /// Creates a new trace-event identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    string NewEventId();
}
