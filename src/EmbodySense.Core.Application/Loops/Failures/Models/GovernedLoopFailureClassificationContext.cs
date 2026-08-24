using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Failures.Models;

/// <summary>Binds classification to one exact immutable run-node attempt.</summary>
public sealed record GovernedLoopFailureClassificationContext(
    string FailureEvidenceId,
    string WorkspaceId,
    string RunId,
    GovernedLoopRevisionReference Revision,
    long ExecutionGeneration,
    int ActivationOrdinal,
    int VisitOrdinal,
    string NodeId,
    int Attempt,
    GovernedLoopFailureEvidenceReference ClassificationBoundaryEvidence);
