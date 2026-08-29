using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

internal sealed record HumanInputResponseContinuationWakeResolution(
    HumanInputResponseContinuationWakeResolutionStatus Status,
    CustomLoopRunRecord? Run = null,
    GovernedLoopSleepCheckpoint? Checkpoint = null);
