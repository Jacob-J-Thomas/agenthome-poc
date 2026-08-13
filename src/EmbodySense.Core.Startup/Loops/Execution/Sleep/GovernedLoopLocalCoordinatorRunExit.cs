using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

internal sealed record GovernedLoopLocalCoordinatorRunExit(
    bool IsFatal,
    GovernedLoopCoordinatorFailureKind FailureKind,
    string EvidenceReference)
{
    internal static GovernedLoopLocalCoordinatorRunExit Stopped { get; } = new(false, default, string.Empty);

    internal static GovernedLoopLocalCoordinatorRunExit Fatal(
        GovernedLoopCoordinatorFailureKind kind,
        string evidenceReference)
        => new(true, kind, evidenceReference);
}
