namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Describes a bounded authenticated actor posture.</summary>
public enum CredentialActorAuthentication
{
    /// <summary>The actor could not be authenticated.</summary>
    Unauthenticated = 0,
    /// <summary>The actor is authenticated but is not a user-authority principal.</summary>
    Authenticated = 1,
    /// <summary>The actor is an authenticated user-authority principal.</summary>
    AuthenticatedUser = 2
}
