using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

internal sealed record GovernedLoopLocalCoordinatorAcquisitionPreparation(
    bool Succeeded,
    GovernedLoopCoordinatorAcquisitionRequest? Request,
    GovernedLoopCoordinatorRepairAcquisitionRequest? RepairAcquisition,
    GovernedLoopLocalCoordinatorStartResult? Blocked);
