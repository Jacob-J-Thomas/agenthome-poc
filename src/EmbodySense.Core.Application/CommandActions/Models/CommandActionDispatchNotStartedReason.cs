namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Identifies one closed, non-sensitive reason that native command dispatch did not start.</summary>
public enum CommandActionDispatchNotStartedReason
{
    /// <summary>The native invocation contract was invalid.</summary>
    InvalidRequest = 1,

    /// <summary>The retained preparation evidence was missing, stale, or incoherent.</summary>
    PreparationUnavailable = 2,

    /// <summary>The exact executable artifact could not be resolved or retained.</summary>
    ArtifactUnavailable = 3,

    /// <summary>The declared cross-process concurrency slot was unavailable.</summary>
    ConcurrencyUnavailable = 4,

    /// <summary>Final lifecycle authority could not be retained through launch.</summary>
    LaunchAuthorityUnavailable = 5,
}
