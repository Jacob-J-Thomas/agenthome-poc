namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies the command process network posture.</summary>
public enum CommandActionNetworkPolicy
{
    /// <summary>No network posture was supplied.</summary>
    Unknown = 0,
    /// <summary>Network access must be denied before child code executes.</summary>
    Denied = 1,
}
