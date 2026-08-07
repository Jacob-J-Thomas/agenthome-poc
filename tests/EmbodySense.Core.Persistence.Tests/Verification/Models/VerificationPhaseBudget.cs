namespace EmbodySense.Core.Persistence.Tests.Verification.Models;

internal sealed record VerificationPhaseBudget(
    string Name,
    VerificationPhaseClassification Classification,
    TimeSpan ProposedBudget,
    TimeSpan DiagnosticBound,
    long? MaximumAllocatedBytes = null);
