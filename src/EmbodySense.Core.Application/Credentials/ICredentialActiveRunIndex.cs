using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Captures bounded active-run identities affected by an immediate restrictive credential posture.</summary>
public interface ICredentialActiveRunIndex
{
    /// <summary>Reads active runs matching the exact credential binding.</summary>
    Task<IReadOnlyList<string>> CaptureAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken);
}
