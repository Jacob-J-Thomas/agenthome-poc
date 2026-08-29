using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

internal sealed record HumanInputResponseContinuationRunReadResult(
    HumanInputResponseContinuationRunReadStatus Status,
    CustomLoopRunRecord? Run = null);
