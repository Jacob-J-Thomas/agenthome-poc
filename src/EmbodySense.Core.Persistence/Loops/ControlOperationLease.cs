using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Owns the exclusive file handle and generation identity for one pending control operation.
/// </summary>
/// <param name="operationId">The operation ID.</param>
/// <param name="ownerGenerationId">The owner generation ID.</param>
/// <param name="ownership">The ownership.</param>
internal sealed class ControlOperationLease(string operationId, string ownerGenerationId, FileStream ownership) : ICustomLoopControlOperationLease
{
    private int _disposed;

    /// <summary>
    /// Gets the operation ID.
    /// </summary>
    /// <value>The operation ID.</value>
    public string OperationId { get; } = operationId;

    /// <summary>
    /// Gets the owner generation ID.
    /// </summary>
    /// <value>The owner generation ID.</value>
    public string OwnerGenerationId { get; } = ownerGenerationId;

    /// <summary>
    /// Idempotently closes the ownership handle.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ownership.Dispose();
        }
    }
}
