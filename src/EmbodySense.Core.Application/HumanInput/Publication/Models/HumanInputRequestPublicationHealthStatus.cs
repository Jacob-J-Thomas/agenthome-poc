namespace EmbodySense.Core.Application.HumanInput.Publication.Models;

/// <summary>Defines closed canonical Human Input request-ledger health dispositions.</summary>
public enum HumanInputRequestPublicationHealthStatus
{
    /// <summary>No supported disposition was established.</summary>
    Unknown = 0,

    /// <summary>The canonical request ledger established one valid current state.</summary>
    Ready = 1,

    /// <summary>The canonical request ledger could not be read safely.</summary>
    Unavailable = 2,

    /// <summary>The canonical request ledger returned malformed, ambiguous, or contradictory evidence.</summary>
    Corrupt = 3,
}
