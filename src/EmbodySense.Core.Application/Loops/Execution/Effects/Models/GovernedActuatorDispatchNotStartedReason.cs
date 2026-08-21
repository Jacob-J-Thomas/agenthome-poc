namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Identifies one closed, non-sensitive reason that a governed actuator did not cross its dispatch boundary.</summary>
public enum GovernedActuatorDispatchNotStartedReason
{
    /// <summary>The adapter invocation contract was invalid.</summary>
    InvalidRequest = 1,

    /// <summary>Durable preparation evidence was missing, stale, or incoherent.</summary>
    PreparationUnavailable = 2,

    /// <summary>The exact server-owned implementation artifact was unavailable.</summary>
    ArtifactUnavailable = 3,

    /// <summary>The declared concurrency boundary was unavailable.</summary>
    ConcurrencyUnavailable = 4,

    /// <summary>Final launch authority was unavailable.</summary>
    LaunchAuthorityUnavailable = 5,
}
