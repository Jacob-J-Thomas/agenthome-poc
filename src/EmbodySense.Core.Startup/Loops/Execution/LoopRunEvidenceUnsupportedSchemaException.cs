namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed class LoopRunEvidenceUnsupportedSchemaException : InvalidOperationException
{
    internal LoopRunEvidenceUnsupportedSchemaException(Exception innerException)
        : base(innerException?.Message ?? throw new ArgumentNullException(nameof(innerException)), innerException)
    {
    }
}
