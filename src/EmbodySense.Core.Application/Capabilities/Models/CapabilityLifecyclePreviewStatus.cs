namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether a deterministic lifecycle preview was produced.</summary>
public enum CapabilityLifecyclePreviewStatus
{
    /// <summary>The preview is ready and may be applied if it has no required blockers.</summary>
    Ready = 1,
    /// <summary>The exact preview operation was replayed.</summary>
    Replayed = 2,
    /// <summary>The operation identity is bound to different intent.</summary>
    Conflict = 3,
    /// <summary>The target capability is unknown.</summary>
    NotFound = 4,
    /// <summary>The request violates the closed lifecycle contract.</summary>
    Invalid = 5,
    /// <summary>Complete dependency or proved lifecycle state is unavailable.</summary>
    Unavailable = 6
}
