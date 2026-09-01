using System.Text.Json;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Describes one surface-owned Human Input response intent with opaque JSON data for Startup validation.</summary>
/// <param name="OperationId">The caller-owned workspace-global idempotency identity.</param>
/// <param name="Kind">The exact response operation token.</param>
/// <param name="RequestId">The route-bound request identity.</param>
/// <param name="ExpectedLifecycleVersion">The optimistic lifecycle version observed by the surface.</param>
/// <param name="ExpectedLifecycleStatus">The optimistic lifecycle status token observed by the surface.</param>
/// <param name="ExpectedRequest">The detached immutable request reference observed by the surface.</param>
/// <param name="ResponseId">The proposed response identity.</param>
/// <param name="Value">The untrusted typed response JSON.</param>
/// <param name="Explanation">The optional bounded untrusted explanation.</param>
public sealed record HumanInputSurfaceResponseOperationInput(
    string OperationId,
    string Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleStatus,
    HumanInputSurfaceRequestReference? ExpectedRequest,
    string ResponseId,
    JsonElement Value,
    string? Explanation);
