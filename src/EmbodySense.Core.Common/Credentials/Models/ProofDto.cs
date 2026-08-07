namespace EmbodySense.Core.Common.Credentials.Models;

internal sealed record ProofDto(int SchemaVersion, string ProofId, string ReferenceId, string BindingHash, ScopeDto GrantedScope, string ActorId, string RunId, long AuthorityRevision, string IssuedAtUtc, string ExpiresAtUtc, string IssuerId, string Authenticator);
