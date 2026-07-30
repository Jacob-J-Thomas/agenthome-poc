namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Proves bounded ownership of one durable lifecycle-control receipt.
/// </summary>
public interface ICustomLoopControlOperationLease : IDisposable
{
    /// <summary>
    /// Gets the operation ID.
    /// </summary>
    /// <value>The operation ID.</value>
    string OperationId { get; }

    /// <summary>
    /// Gets the owner generation ID.
    /// </summary>
    /// <value>The owner generation ID.</value>
    string OwnerGenerationId { get; }
}
