using EmbodySense.Core.Application.Loops.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Persists idempotent pause, resume, and cancel operations across their reserved and completed states.
/// </summary>
public interface ICustomLoopControlOperationStore
{
    /// <summary>
    /// Atomically reserves a control operation or returns the existing request-bound operation.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The reservation status and persisted operation.</returns>
    Task<CustomLoopControlOperationStoreResult> BeginAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a control operation by its idempotency identifier.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The operation, or <see langword="null"/> when it is unknown.</returns>
    Task<CustomLoopControlOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records the terminal control result for a previously reserved operation.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The completion status and persisted operation.</returns>
    Task<CustomLoopControlOperationStoreResult> CompleteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default);
}
