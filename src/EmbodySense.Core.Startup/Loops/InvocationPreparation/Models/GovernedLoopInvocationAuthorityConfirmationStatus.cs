namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Identifies the bounded durable confirmation outcome.</summary>
public enum GovernedLoopInvocationAuthorityConfirmationStatus
{
    /// <summary>The status is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The exact profile and grant were created or safely replayed.</summary>
    Confirmed = 1,
    /// <summary>The supplied confirmation shape is invalid.</summary>
    Invalid = 2,
    /// <summary>The selected graph revision or preview evidence is stale or changed.</summary>
    Stale = 3,
    /// <summary>Current policy does not permit the requested least-authority grant.</summary>
    Ineligible = 4,
    /// <summary>The operation identity is already bound to a different durable intent.</summary>
    Conflict = 5,
    /// <summary>Current evidence or durable outcome cannot safely be established.</summary>
    Unavailable = 6,
}
