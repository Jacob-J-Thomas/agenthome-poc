using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop ordered run result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Run">The run.</param>
/// <param name="Detail">The detail.</param>
/// <param name="ProviderWasInvoked">The provider was invoked.</param>
public sealed record CustomLoopOrderedRunResult(
    CustomLoopOrderedRunStatus Status,
    CustomLoopRunRecord? Run,
    string Detail,
    bool ProviderWasInvoked = false);
