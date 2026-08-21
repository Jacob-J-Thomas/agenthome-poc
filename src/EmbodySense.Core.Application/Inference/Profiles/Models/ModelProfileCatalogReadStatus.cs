namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies a bounded model-profile catalog read outcome.</summary>
public enum ModelProfileCatalogReadStatus
{
    /// <summary>A current bounded page is available.</summary>
    Available = 1,
    /// <summary>The caller request is invalid.</summary>
    Invalid = 2,
    /// <summary>Complete trusted catalog evidence is unavailable.</summary>
    Unavailable = 3,
    /// <summary>The source exceeded a bounded contract.</summary>
    LimitExceeded = 4
}
