using EmbodySense.Core.Application.Loops.Diagnostics;

namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record CustomLoopRunCanonicalPublicationResult(bool IsCommitted, CustomLoopRunPersistenceDiagnostic? Diagnostic, Exception? Cause = null)
{
    public static CustomLoopRunCanonicalPublicationResult Committed() => new(true, null);

    public static CustomLoopRunCanonicalPublicationResult Unknown(CustomLoopRunPersistenceDiagnostic diagnostic, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(cause);
        return new CustomLoopRunCanonicalPublicationResult(false, diagnostic, cause);
    }
}
