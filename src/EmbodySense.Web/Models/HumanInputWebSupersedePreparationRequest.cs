namespace EmbodySense.Web.Models;

/// <summary>Supplies one exact target and successor proposal for Startup-owned supersede preparation.</summary>
/// <param name="OperationId">The operation identity shared with the later commit.</param>
/// <param name="ExpectedLifecycleVersion">The exact lifecycle version observed by the client.</param>
/// <param name="ExpectedLifecycleStatus">The exact lifecycle status token observed by the client.</param>
/// <param name="ExpectedRequest">The exact immutable request reference observed by the client.</param>
/// <param name="Successor">The untrusted successor content.</param>
public sealed record HumanInputWebSupersedePreparationRequest(string OperationId, long ExpectedLifecycleVersion, string ExpectedLifecycleStatus, HumanInputWebRequestReference? ExpectedRequest, HumanInputWebSuccessorDraft? Successor);
