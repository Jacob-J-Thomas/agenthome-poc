using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

internal sealed record HumanInputResponseContinuationRunUpdateResult(
    HumanInputResponseContinuationRunUpdateStatus Status,
    CustomLoopRunRecord? Run = null);
