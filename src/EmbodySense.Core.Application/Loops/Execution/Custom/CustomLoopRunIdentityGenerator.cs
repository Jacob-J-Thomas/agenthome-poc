namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Represents a custom loop run identity generator.
/// </summary>
public sealed class CustomLoopRunIdentityGenerator : ICustomLoopRunIdentityGenerator
{
    /// <summary>
    /// Creates a new run identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    public string NewRunId() => CustomLoopGeneratedIdentifier.New("run");

    /// <summary>
    /// Creates a new trace-event identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    public string NewEventId() => CustomLoopGeneratedIdentifier.New("event");
}
