using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop recovery result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Run">The run.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopRecoveryResult(CustomLoopRecoveryStatus Status, CustomLoopRunRecord Run, string Detail);
