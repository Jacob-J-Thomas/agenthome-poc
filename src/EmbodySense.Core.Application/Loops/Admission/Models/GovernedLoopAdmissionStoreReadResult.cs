using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Returns one exact admission-operation lookup from the atomic store.</summary>
/// <param name="Status">The closed read disposition.</param>
/// <param name="StoreGeneration">The nonnegative workspace-global store generation when safely observed.</param>
/// <param name="Outcome">The immutable terminal outcome when the operation was found.</param>
public sealed record GovernedLoopAdmissionStoreReadResult(
    GovernedLoopAdmissionStoreReadStatus Status,
    long StoreGeneration,
    GovernedLoopAdmissionTerminalOutcome? Outcome);
