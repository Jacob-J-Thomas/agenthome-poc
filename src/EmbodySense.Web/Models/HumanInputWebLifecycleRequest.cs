namespace EmbodySense.Web.Models;

/// <summary>Supplies bounded optimistic lifecycle terms and an optional opaque Startup candidate selector.</summary>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact lifecycle version observed by the client.</param>
/// <param name="ExpectedLifecycleStatus">The exact lifecycle status token observed by the client.</param>
/// <param name="ExpectedRequest">The exact immutable request reference observed by the client.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
/// <param name="CandidateKey">The opaque Startup-issued key for supersede, when applicable.</param>
public sealed record HumanInputWebLifecycleRequest(string OperationId, long ExpectedLifecycleVersion, string ExpectedLifecycleStatus, HumanInputWebRequestReference? ExpectedRequest, string Reason, string? CandidateKey = null);
