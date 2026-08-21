namespace EmbodySense.Core.Clients.CommandActions.Models;

/// <summary>Identifies the only conclusive outcomes of a pre-execution isolation launch request.</summary>
public enum CommandActionIsolatedLaunchStatus
{
    /// <summary>The complete isolated process tree was created.</summary>
    Started = 1,
    /// <summary>The adapter affirmatively proved that no child code was created.</summary>
    RejectedBeforeStart = 2,
}
