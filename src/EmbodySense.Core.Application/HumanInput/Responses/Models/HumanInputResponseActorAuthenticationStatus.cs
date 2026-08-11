namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Identifies one server-owned caller-authentication disposition.</summary>
public enum HumanInputResponseActorAuthenticationStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The current caller was authenticated to one exact actor.</summary>
    Authenticated = 1,
    /// <summary>The current caller was not authenticated.</summary>
    Denied = 2,
    /// <summary>Authentication could not establish a safe result.</summary>
    Unavailable = 3,
}
