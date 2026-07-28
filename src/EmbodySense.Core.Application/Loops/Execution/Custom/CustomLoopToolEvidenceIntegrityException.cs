namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopToolEvidenceIntegrityException : Exception
{
    public CustomLoopToolEvidenceIntegrityException(string message) : base(message)
    {
    }

    public CustomLoopToolEvidenceIntegrityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
