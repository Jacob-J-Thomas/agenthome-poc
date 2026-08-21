using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one exact provider-attempt ledger history without private adapter configuration.</summary>
public sealed record LoopRunModelUsageAttemptSnapshot(
    string NodeId,
    int PlanOrdinal,
    int ActivationOrdinal,
    int VisitOrdinal,
    string AttemptOperationId,
    int AttemptNumber,
    string ProfilePinHash,
    string BudgetPolicyHash,
    string Phase,
    long Generation,
    string ReservationEntryHash,
    string LatestEntryHash,
    GovernedModelUsageCeiling Reservation,
    LlmInferenceUsageEvidence? Usage,
    GovernedModelUsageVector? Used,
    GovernedModelUsageVector? Released,
    bool UsageUnknown,
    bool ReservationOutstanding);
