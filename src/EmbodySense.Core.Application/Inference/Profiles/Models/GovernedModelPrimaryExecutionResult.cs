using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns primary-only execution and durable usage-state outcomes.</summary>
public sealed record GovernedModelPrimaryExecutionResult(
    GovernedModelAttemptAdmissionStatus AdmissionStatus,
    LlmInferenceResponse? Response,
    GovernedModelUsageTransitionStatus? DispatchStatus,
    GovernedModelUsageTransitionStatus? UsageStatus,
    GovernedModelUsageTransitionStatus? ReconciliationStatus,
    GovernedModelProfilePin? Primary = null,
    GovernedModelUsageLedgerEntry? ReservationEntry = null,
    GovernedModelUsageLedgerEntry? TerminalUsageEntry = null,
    bool ProviderDispatchMayHaveOccurred = false);
