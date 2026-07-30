namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Represents a custom loop tool evidence integrity exception.
/// </summary>
public sealed class CustomLoopToolEvidenceIntegrityException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopToolEvidenceIntegrityException"/> type.
    /// </summary>
    /// <param name="message">The message.</param>
    public CustomLoopToolEvidenceIntegrityException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopToolEvidenceIntegrityException"/> type.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public CustomLoopToolEvidenceIntegrityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
