namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Retains only a safe target class and domain-separated fingerprint for one exact operation.</summary>
public sealed record CredentialLeaseTargetScope(
    string TargetClass,
    string TargetFingerprint,
    string OperationClass,
    string Purpose);
