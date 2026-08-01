namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Requests callback-only use under an exact binding, requested scope, and independently verified proof.</summary>
public sealed record CredentialUseRequest(
    CredentialCapabilityBinding Binding,
    CredentialContractHash BindingHash,
    CredentialScope RequestedScope,
    CredentialAuthorityProof AuthorityProof,
    DateTimeOffset RequestedAtUtc);
