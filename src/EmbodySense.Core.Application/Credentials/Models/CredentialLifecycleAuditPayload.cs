namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Supplies the bounded value-free event fields atomically persisted with a lifecycle transition.</summary>
public sealed record CredentialLifecycleAuditPayload(string Action, string Outcome, string Detail);
