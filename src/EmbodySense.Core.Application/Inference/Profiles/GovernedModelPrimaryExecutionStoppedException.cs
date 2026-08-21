using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Stops a canonical provider attempt with the exact structured model-admission disposition.</summary>
public sealed class GovernedModelPrimaryExecutionStoppedException : Exception
{
    /// <summary>Creates a bounded stopped-attempt error.</summary>
    public GovernedModelPrimaryExecutionStoppedException(GovernedModelAttemptAdmissionStatus status)
        : base($"Canonical model-profile execution stopped before a usable response with `{status}` posture.")
    {
        if (!Enum.IsDefined(status) || Convert.ToInt32(status, System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
    }

    /// <summary>Creates a stopped-attempt error carrying authenticated durable ledger posture.</summary>
    public GovernedModelPrimaryExecutionStoppedException(GovernedModelPrimaryExecutionResult result)
        : this(result?.AdmissionStatus ?? throw new ArgumentNullException(nameof(result)))
    {
        Primary = result.Primary;
        ReservationEntry = result.ReservationEntry;
        TerminalUsageEntry = result.TerminalUsageEntry;
        OutcomeMayExist = result.ProviderDispatchMayHaveOccurred;
    }

    /// <summary>Gets the exact current attempt disposition.</summary>
    public GovernedModelAttemptAdmissionStatus Status { get; }

    /// <summary>Gets the exact admitted primary when durable attempt evidence exists.</summary>
    public GovernedModelProfilePin? Primary { get; }

    /// <summary>Gets the exact durable reservation entry when one was authenticated.</summary>
    public GovernedModelUsageLedgerEntry? ReservationEntry { get; }

    /// <summary>Gets the authenticated current usage-ledger entry when the attempt advanced.</summary>
    public GovernedModelUsageLedgerEntry? TerminalUsageEntry { get; }

    /// <summary>Gets whether durable evidence proves provider dispatch may have occurred.</summary>
    public bool OutcomeMayExist { get; }
}
