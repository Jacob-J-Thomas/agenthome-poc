namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Binds a model-provider credential to one exact admitted profile, or explicitly marks it inapplicable.</summary>
public sealed record CredentialLeaseProfileScope(
    CredentialLeaseProfileApplicability Applicability,
    string? ProfileId,
    string? ProfileHash);
