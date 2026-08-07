using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Contains immutable, value-free evidence of one registry or credential-use operation identity.</summary>
public sealed record CredentialRegistryOperationEvidence(CredentialContractId OperationId, CredentialContractHash RequestHash, int Kind, long Revision, CredentialReferenceId ReferenceId);
