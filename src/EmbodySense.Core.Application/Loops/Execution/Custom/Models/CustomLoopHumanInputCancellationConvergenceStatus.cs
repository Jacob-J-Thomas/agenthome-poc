namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Identifies the closed outcome of one Human Input-aware custom-loop cancellation reconciliation attempt.</summary>
public enum CustomLoopHumanInputCancellationConvergenceStatus
{
    /// <summary>No supported reconciliation outcome was established.</summary>
    Unknown = 0,

    /// <summary>No Human Input checkpoint was retained for the run.</summary>
    NotApplicable = 1,

    /// <summary>Every retained checkpoint has authoritative non-actionable terminal proof.</summary>
    Converged = 2,

    /// <summary>Current evidence is safe but requires a later retry after a concurrent or incomplete operation resolves.</summary>
    Pending = 3,

    /// <summary>Available evidence preserves a different winner or is unsafe to reconcile automatically.</summary>
    Blocked = 4,

    /// <summary>The run changed during the bounded reconciliation attempt.</summary>
    Conflict = 5,

    /// <summary>A required canonical store or authority dependency was unavailable.</summary>
    Unavailable = 6,

    /// <summary>Canonical receipt, run, request, or checkpoint evidence was malformed or divergent.</summary>
    Corrupt = 7,
}
