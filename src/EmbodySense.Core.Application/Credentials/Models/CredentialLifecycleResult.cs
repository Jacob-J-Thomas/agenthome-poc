using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Returns one bounded value-free lifecycle outcome and immediately affected active-run identities.</summary>
public sealed record CredentialLifecycleResult(CredentialLifecycleResultStatus Status, CredentialContractId OperationId, CredentialLifecycleOperationKind Kind, CredentialReferenceId ReferenceId, long? RegistryRevision, CredentialProviderHealthStatus Health, IReadOnlyList<string> AffectedActiveRuns, CredentialFailure? Failure, string Detail)
{
    /// <summary>Gets a defensive immutable active-run identity snapshot.</summary>
    public IReadOnlyList<string> AffectedActiveRuns { get; } = Array.AsReadOnly((AffectedActiveRuns ?? []).ToArray());
}
