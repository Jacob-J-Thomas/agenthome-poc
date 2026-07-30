using EmbodySense.Core.Common.Loops.Models.Custom;
namespace EmbodySense.Core.Common.Loops.Custom;

/// <summary>
/// Represents a custom loop validation result.
/// </summary>
/// <param name="Errors">The errors.</param>
public sealed record CustomLoopValidationResult(IReadOnlyList<CustomLoopValidationError> Errors)
{
    /// <summary>
    /// Gets a value indicating whether validation produced no errors.
    /// </summary>
    /// <value><see langword="true"/> when <see cref="Errors"/> is empty; otherwise, <see langword="false"/>.</value>
    public bool IsValid => Errors.Count == 0;
}
