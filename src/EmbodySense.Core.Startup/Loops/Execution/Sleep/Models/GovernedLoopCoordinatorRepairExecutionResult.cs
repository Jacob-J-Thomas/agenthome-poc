using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Reports one Startup repair submission and its canonical background-host startup outcome.</summary>
public sealed record GovernedLoopCoordinatorRepairExecutionResult(
    GovernedLoopCoordinatorRepairExecutionStatus Status,
    GovernedLoopCoordinatorRepairSubmitResult Submission,
    AgentRuntimeGovernedLoopBackgroundStartResult? Start);
