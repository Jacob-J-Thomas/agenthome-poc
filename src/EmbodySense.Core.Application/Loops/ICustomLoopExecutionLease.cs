namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Proves in-process ownership of one custom-loop invocation operation.
/// </summary>
public interface ICustomLoopExecutionLease : IDisposable
{
    /// <summary>
    /// Gets the operation ID.
    /// </summary>
    /// <value>The operation ID.</value>
    string OperationId { get; }
}
