using EmbodySense.Core.Application.Loops.Diagnostics;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class CustomLoopRunCanonicalPublicationUnknownException : IOException
{
    public CustomLoopRunCanonicalPublicationUnknownException(CustomLoopRunCanonicalPublicationResult publication)
        : base("Canonical run publication durability could not be proved after atomic rename.", publication.Cause)
    {
        ArgumentNullException.ThrowIfNull(publication);
        Data[CustomLoopRunPersistenceDiagnostic.ExceptionDataKey] = publication.Diagnostic ?? throw new ArgumentException("Unknown canonical publication requires a diagnostic.", nameof(publication));
    }
}
