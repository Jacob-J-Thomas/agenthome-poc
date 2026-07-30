namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Validates and normalizes custom loop generated identifiers.
/// </summary>
internal static class CustomLoopGeneratedIdentifier
{
    /// <summary>
    /// Creates a canonical prefixed identifier.
    /// </summary>
    /// <param name="prefix">The prefix.</param>
    /// <returns>The prefix followed by a collision-resistant lowercase suffix.</returns>
    internal static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
