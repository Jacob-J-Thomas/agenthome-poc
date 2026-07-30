using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class ControlOperationLease(string operationId, string ownerGenerationId, FileStream ownership) : ICustomLoopControlOperationLease
{
    private int _disposed;

    public string OperationId { get; } = operationId;

    public string OwnerGenerationId { get; } = ownerGenerationId;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ownership.Dispose();
        }
    }
}
