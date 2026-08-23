using EmbodySense.Core.Common.Credentials.Leases;

namespace EmbodySense.Core.Common.Credentials.Models;

internal sealed record EvidenceDto(int SchemaVersion, string EvidenceId, string ReferenceId, string BindingHash, string ProofId, string RunId, ScopeDto UsedScope, string UsedAtUtc, string Outcome, bool RedactionApplied, CredentialLeaseUseEvidence? Lease);
