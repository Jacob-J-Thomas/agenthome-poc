using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop control result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Run">The run.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopControlResult(CustomLoopControlStatus Status, CustomLoopRunRecord? Run, string OperationId, string Detail);
