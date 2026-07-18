using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopControlResult(CustomLoopControlStatus Status, CustomLoopRunRecord? Run, string OperationId, string Detail);
