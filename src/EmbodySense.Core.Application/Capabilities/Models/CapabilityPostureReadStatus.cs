namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether a bounded capability posture query produced trustworthy current or recovered evidence.</summary>
public enum CapabilityPostureReadStatus
{
    /// <summary>Current proved posture is available.</summary>
    Available = 1,

    /// <summary>Only explicitly recovered read-only evidence is available.</summary>
    Recovered = 2,

    /// <summary>The requested identity is not present in the bound workspace.</summary>
    NotFound = 3,

    /// <summary>Required posture evidence could not be proved safely.</summary>
    Unavailable = 4,

    /// <summary>The query is outside the closed bounded contract.</summary>
    Invalid = 5
}
