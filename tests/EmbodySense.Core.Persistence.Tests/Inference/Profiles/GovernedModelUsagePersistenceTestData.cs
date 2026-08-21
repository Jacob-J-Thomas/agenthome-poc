using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Tests.Inference.Profiles;

internal static class GovernedModelUsagePersistenceTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);

    internal static GovernedModelUsageLedgerIdentity Identity(
        WorkspacePaths paths,
        GovernedModelBudgetPolicy policy,
        string operationId = "attempt-one",
        string nodeId = "inference-one",
        int planOrdinal = 0,
        int activationOrdinal = 0,
        int visitOrdinal = 1,
        int attemptNumber = 1,
        string runId = "run-one",
        char admissionReceiptHash = 'b',
        char routingAdmissionHash = 'c',
        char authorityEvidenceHash = '8',
        char dataPostureEvidenceHash = '9')
        => GovernedModelUsageLedgerIdentity.Create(
            1,
            CapabilityWorkspaceScopeId.Create(paths.RootPath),
            runId,
            "graph-one",
            "revision-one",
            Hash('a'),
            1,
            Hash(admissionReceiptHash),
            Hash(routingAdmissionHash),
            Hash(authorityEvidenceHash),
            Hash(dataPostureEvidenceHash),
            nodeId,
            planOrdinal,
            activationOrdinal,
            visitOrdinal,
            operationId,
            attemptNumber,
            Hash('d'),
            policy.ContentHash);

    internal static GovernedModelBudgetPolicy InputBudget(long? perAttempt, long? perNode, long? perRun)
        => GovernedModelBudgetPolicy.Create(1, Ceiling(perAttempt), Ceiling(perNode), Ceiling(perRun));

    internal static GovernedModelBudgetPolicy UnboundedBudget()
        => GovernedModelBudgetPolicy.Create(1, Ceiling(null), Ceiling(null), Ceiling(null));

    internal static GovernedModelBudgetPolicy MonetaryBudget(string currency, long? perAttempt, long? perNode, long? perRun)
        => GovernedModelBudgetPolicy.Create(1, MonetaryCeiling(currency, perAttempt), MonetaryCeiling(currency, perNode), MonetaryCeiling(currency, perRun));

    internal static GovernedModelUsageLedgerEntry Dispatch(GovernedModelUsageLedgerEntry reservation, bool started, char evidence = 'e')
        => GovernedModelUsageLedgerEntry.Create(
            1,
            reservation.Identity,
            2,
            started ? GovernedModelUsageLedgerPhase.DispatchBoundaryReached : GovernedModelUsageLedgerPhase.DispatchProvedNotStarted,
            reservation.Reservation,
            null,
            null,
            null,
            started,
            Hash(evidence),
            reservation.ContentHash,
            Now.AddSeconds(1));

    internal static LlmInferenceUsageEvidence PartialUsage(long inputTokens)
        => LlmInferenceUsageEvidence.Create(
            1,
            "provider-test",
            "v1",
            GovernedModelUsageMeasurement.Authoritative(inputTokens),
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelMonetaryUsageMeasurement.Unavailable);

    internal static LlmInferenceUsageEvidence MonetaryUsage(string currency, long costMicros)
        => LlmInferenceUsageEvidence.Create(
            1,
            "provider-test",
            "v1",
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelMonetaryUsageMeasurement.Authoritative(currency, costMicros));

    internal static LlmInferenceUsageEvidence CompleteUsage(long inputTokens)
        => LlmInferenceUsageEvidence.Create(
            1,
            "provider-test",
            "v1",
            GovernedModelUsageMeasurement.Authoritative(inputTokens),
            GovernedModelUsageMeasurement.Authoritative(0),
            GovernedModelUsageMeasurement.Authoritative(0),
            GovernedModelUsageMeasurement.Authoritative(inputTokens),
            GovernedModelMonetaryUsageMeasurement.Authoritative("USD", 0));

    internal static GovernedModelUsageCeiling Ceiling(long? inputTokens)
        => GovernedModelUsageCeiling.Create(
            inputTokens is null ? GovernedModelUsageLimit.Unbounded : GovernedModelUsageLimit.Bounded(inputTokens.Value),
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Unbounded);

    private static GovernedModelUsageCeiling MonetaryCeiling(string currency, long? costMicros)
        => GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            costMicros is null ? GovernedModelMonetaryLimit.Unbounded : GovernedModelMonetaryLimit.Bounded(currency, costMicros.Value));

    internal static string Hash(char value) => new(value, 64);
}
