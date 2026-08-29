using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed record HumanInputContinuationRecoveryContext(
    CustomLoopRunRecord AdmittedRun,
    CustomLoopRunRecord RunningRun,
    CustomLoopRunRecord Run,
    GovernedLoopHumanInputWaitingCheckpoint Checkpoint,
    GovernedLoopSequentialAdapterBinding Binding,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact);
