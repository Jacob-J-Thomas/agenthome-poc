namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Identifies a closed server-owned Human Input authority-boundary disposition.</summary>
public enum AgentRuntimeHumanInputAuthorityStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The trusted boundary established the requested terms or actor decision.</summary>
    Ready = 1,
    /// <summary>The known authenticated actor is not permitted for the exact operation.</summary>
    Denied = 2,
    /// <summary>The boundary could not establish safe current terms or identity.</summary>
    Unavailable = 3,
}
