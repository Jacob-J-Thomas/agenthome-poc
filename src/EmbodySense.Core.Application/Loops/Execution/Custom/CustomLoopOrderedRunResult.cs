using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopOrderedRunResult(
    CustomLoopOrderedRunStatus Status,
    CustomLoopRunRecord? Run,
    string Detail);
