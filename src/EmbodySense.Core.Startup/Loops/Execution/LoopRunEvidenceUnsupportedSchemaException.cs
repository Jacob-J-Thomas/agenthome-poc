namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed class LoopRunEvidenceUnsupportedSchemaException : InvalidOperationException
{
    public LoopRunEvidenceUnsupportedSchemaException(string message)
        : base(message ?? throw new ArgumentNullException(nameof(message)))
    {
    }

    internal LoopRunEvidenceUnsupportedSchemaException(Exception innerException)
        : base(innerException?.Message ?? throw new ArgumentNullException(nameof(innerException)), innerException)
    {
    }
}
