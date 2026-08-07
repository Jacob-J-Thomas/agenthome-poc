namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies the narrow continuation visibility policy for response data.
/// </summary>
public enum HumanInputContinuationPolicyKind
{
    /// <summary>Unspecified and invalid.</summary>
    Unknown = 0,
    /// <summary>Data may be made available only to the exact bound node and checkpoint by a future lifecycle owner.</summary>
    BoundNodeAndCheckpointOnly = 1
}
