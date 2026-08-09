namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Serializes cross-domain capability authority observations and mutations for one physical workspace.</summary>
public interface ICapabilityAuthorityTransaction
{
    /// <summary>Executes one bounded operation under the workspace capability-authority fence.</summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="operation">The bounded operation to execute. Nested calls on the same workspace are reentrant.</param>
    /// <param name="cancellationToken">The cancellation token used while acquiring and executing the operation.</param>
    /// <returns>The operation result.</returns>
    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default);

    /// <summary>Acquires a retained fence only when validation succeeds while holding the same workspace authority boundary.</summary>
    /// <param name="validator">The final authority validation performed after the fence is acquired.</param>
    /// <param name="cancellationToken">The cancellation token used while acquiring and validating the fence.</param>
    /// <returns>A retained fence when validation succeeds; otherwise <see langword="null"/>.</returns>
    Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default);
}
