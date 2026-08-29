using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

internal sealed class HumanInputResponseContinuationFixedResponseStore : IHumanInputResponseLifecycleStore
{
    internal HumanInputResponseContinuationFixedResponseStore(
        HumanInputResponseLifecycleStoreSnapshot? snapshot,
        HumanInputResponseLifecycleStoreReadStatus status = HumanInputResponseLifecycleStoreReadStatus.NotFound)
    {
        Snapshot = snapshot;
        Status = status;
    }

    internal HumanInputResponseLifecycleStoreSnapshot? Snapshot { get; set; }

    internal HumanInputResponseLifecycleStoreReadStatus Status { get; set; }

    internal int ReadCount { get; private set; }

    internal Exception? ReadException { get; set; }

    public Task<HumanInputResponseLifecycleStoreReadResult> ReadAsync(HumanInputRequestReference request, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        if (ReadException is not null)
        {
            throw ReadException;
        }
        return Task.FromResult(new HumanInputResponseLifecycleStoreReadResult(
            Snapshot is null ? Status : HumanInputResponseLifecycleStoreReadStatus.Ready,
            Snapshot is null ? 0 : 1,
            Snapshot,
            null));
    }

    public Task<HumanInputResponseLifecycleStoreReadResult> ReadForMutationAsync(string requestId, string operationId, string commandHash, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<HumanInputResponseLifecycleStoreCommitResult> CommitAsync(HumanInputResponseLifecycleStoreMutation mutation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
