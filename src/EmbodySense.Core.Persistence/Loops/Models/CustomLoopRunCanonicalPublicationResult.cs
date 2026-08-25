using EmbodySense.Core.Application.Loops.Diagnostics;

namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record CustomLoopRunCanonicalPublicationResult(bool IsCommitted, CustomLoopRunPersistenceDiagnostic? Diagnostic, Exception? Cause = null);
