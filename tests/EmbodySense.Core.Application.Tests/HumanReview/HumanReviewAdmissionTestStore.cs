using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewAdmissionTestStore(HumanReviewAdmissionSharedState state) : ICustomLoopRunStore
{
    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord candidate, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        CustomLoopRunRecord? result;
        Task? initialReadGate = null;
        lock (state.Gate)
        {
            result = state.Run;
            if (state.GateInitialReads)
            {
                state.InitialReadCount++;
                if (state.InitialReadCount == 2)
                {
                    state.GateInitialReads = false;
                    state.InitialReadsReady.TrySetResult();
                }
                else
                {
                    initialReadGate = state.InitialReadsReady.Task;
                }
            }
        }

        if (initialReadGate is not null)
        {
            await initialReadGate.WaitAsync(cancellationToken);
        }

        return result;
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord candidate, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        lock (state.Gate)
        {
            if (state.Run is null)
            {
                return Task.FromResult(CustomLoopRunStoreResult.NotFound());
            }

            if (state.Run.LifecycleVersion != expectedLifecycleVersion)
            {
                return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(state.Run, expectedLifecycleVersion));
            }

            var validation = CustomLoopRunValidator.ValidateUpdate(state.Run, candidate);
            if (!validation.IsValid)
            {
                throw new FormatException(string.Join(Environment.NewLine, validation.Errors));
            }

            state.Run = candidate;
            state.UpdateCount++;
            return Task.FromResult(CustomLoopRunStoreResult.Updated(candidate));
        }
    }
}
