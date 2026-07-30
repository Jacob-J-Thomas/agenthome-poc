namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop invocation operation store result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Operation">The operation.</param>
public sealed record CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus Status, CustomLoopInvocationOperation? Operation);
