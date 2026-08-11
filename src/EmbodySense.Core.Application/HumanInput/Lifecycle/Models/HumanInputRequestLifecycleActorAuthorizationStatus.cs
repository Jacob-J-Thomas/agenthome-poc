namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Defines closed server-owned actor-authorization outcomes for Human Input lifecycle operations.</summary>
public enum HumanInputRequestLifecycleActorAuthorizationStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The authenticated actor may perform the exact command.</summary>
    Authorized = 1,
    /// <summary>The authenticated actor is known but may not perform the command.</summary>
    Denied = 2,
    /// <summary>Current actor authority could not be established.</summary>
    Unavailable = 3,
}
