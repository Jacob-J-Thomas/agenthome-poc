using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Application.Loops.TraceRetention;

namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopRunStore
{
    Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default);

    Task<bool> HasSufficientTraceCapacityForDispatchAsync(CustomLoopRunRecord candidate, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    Task<CustomLoopTraceQuota> GetTraceQuotaAsync(CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceQuota.Empty());

    Task<CustomLoopTraceInspection?> InspectTraceAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopTraceInspection?>(null);

    Task<CustomLoopTraceDeletionLookupResult> GetTraceDeletionOperationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceDeletionLookupResult.NotFound());

    Task<CustomLoopTraceDeletionStoreResult> DeleteTerminalTraceAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default) => Task.FromResult(new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.NotFound, null, CustomLoopTraceDeletionIntegrity.Unknown));

    Task<CustomLoopTraceDeletionAuditMarkStatus> MarkTraceDeletionOutcomeAsync(string operationId, CustomLoopTraceDeletionIntegrity integrity, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceDeletionAuditMarkStatus.NotFound);

    Task<CustomLoopRunStoreResult> AppendTerminalIntegrityWarningAsync(string runId, int expectedLifecycleVersion, CustomLoopRunEvent warning, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.NotFound());

    Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default);
}
