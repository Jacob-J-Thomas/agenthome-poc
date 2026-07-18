using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopRecoveryResult(CustomLoopRecoveryStatus Status, CustomLoopRunRecord Run, string Detail);
