namespace EmbodySense.Core.Application.Loops.Authoring;

/// <summary>
/// Represents a custom loop definition identity generator.
/// </summary>
public sealed class CustomLoopDefinitionIdentityGenerator : ICustomLoopDefinitionIdentityGenerator
{
    /// <summary>
    /// Creates a new loop identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    public string NewLoopId() => CustomLoopGeneratedIdentifier.New("loop");

    /// <summary>
    /// Creates a new inference-step identifier.
    /// </summary>
    /// <returns>The canonical unique identifier.</returns>
    public string NewInferenceStepId() => CustomLoopGeneratedIdentifier.New("step");
}
