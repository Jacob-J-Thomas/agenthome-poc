namespace EmbodySense.Core.Persistence.Loops.Admission.Models;

internal sealed record GovernedLoopAdmissionStoreLoadResult(
    GovernedLoopAdmissionStoreDocument? Document,
    GovernedLoopAdmissionStoreDocument? Pending,
    GovernedLoopAdmissionStoreLoadDisposition Disposition);
