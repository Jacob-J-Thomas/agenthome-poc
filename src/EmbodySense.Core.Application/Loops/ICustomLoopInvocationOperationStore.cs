namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopInvocationOperationStore
{
    Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);
}
