namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Returns a composition-owned current-state decision for one selected trigger.</summary>
/// <param name="Status">Authorized, Rejected, or Unavailable.</param>
/// <param name="EvidenceHash">The exact lowercase SHA-256 current-evidence proof binding.</param>
/// <param name="Detail">The bounded inspectable reason.</param>
public sealed record TriggerWorkerAuthorizationResponse(string Status, string EvidenceHash, string Detail);
