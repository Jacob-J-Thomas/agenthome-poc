namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies current exact attempt-authority posture.</summary>
public enum ModelAttemptAuthorityStatus
{
    /// <summary>The exact frontier attempt remains authorized.</summary>
    Allowed = 1,
    /// <summary>Current authoritative evidence denies the attempt.</summary>
    Denied = 2,
    /// <summary>Current authority could not be proved.</summary>
    Unavailable = 3
}
