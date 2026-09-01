using System.Text.Json;

namespace EmbodySense.Web.Models;

/// <summary>Supplies bounded optimistic response terms and one untrusted typed response JSON value.</summary>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact lifecycle version observed by the client.</param>
/// <param name="ExpectedLifecycleStatus">The exact lifecycle status token observed by the client.</param>
/// <param name="ExpectedRequest">The exact immutable request reference observed by the client.</param>
/// <param name="ResponseId">The optional proposed response identity.</param>
/// <param name="Value">The untrusted response value JSON.</param>
/// <param name="Explanation">The optional bounded untrusted explanation.</param>
public sealed record HumanInputWebResponseRequest(string OperationId, long ExpectedLifecycleVersion, string ExpectedLifecycleStatus, HumanInputWebRequestReference? ExpectedRequest, string? ResponseId, JsonElement Value, string? Explanation);
