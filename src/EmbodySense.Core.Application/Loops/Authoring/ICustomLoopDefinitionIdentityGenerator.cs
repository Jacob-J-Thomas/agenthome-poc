namespace EmbodySense.Core.Application.Loops.Authoring;

/// <summary>
/// Generates canonical identifiers for custom-loop definitions and steps.
/// </summary>
public interface ICustomLoopDefinitionIdentityGenerator
{
    /// <summary>
    /// Creates a new loop identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    string NewLoopId();

    /// <summary>
    /// Creates a new inference-step identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    string NewInferenceStepId();
}
