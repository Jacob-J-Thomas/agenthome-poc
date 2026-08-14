using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Returns one atomic governed-loop admission store commit disposition.</summary>
/// <param name="Status">The closed commit disposition.</param>
/// <param name="StoreGeneration">The observed workspace-global store generation.</param>
/// <param name="Outcome">The exact committed, already committed, conflicting, or otherwise proved terminal outcome.</param>
public sealed record GovernedLoopAdmissionStoreCommitResult(
    GovernedLoopAdmissionStoreCommitStatus Status,
    long StoreGeneration,
    GovernedLoopAdmissionTerminalOutcome? Outcome);
