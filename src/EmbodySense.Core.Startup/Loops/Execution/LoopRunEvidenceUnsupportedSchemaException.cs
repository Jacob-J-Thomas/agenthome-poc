using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed class LoopRunEvidenceUnsupportedSchemaException : InvalidOperationException
{
    public LoopRunEvidenceUnsupportedSchemaException(UnsupportedCustomLoopRunDiscoveryIndexSchemaException innerException)
        : base(innerException?.Message ?? throw new ArgumentNullException(nameof(innerException)), innerException)
    {
    }
}
