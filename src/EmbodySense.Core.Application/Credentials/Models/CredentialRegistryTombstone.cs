using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Contains immutable, value-free evidence that a reference cannot be re-registered.</summary>
public sealed record CredentialRegistryTombstone(CredentialReferenceId ReferenceId, long Revision, CredentialContractId OperationId, DateTimeOffset TombstonedAtUtc, CredentialContractHash ReferenceHash);
