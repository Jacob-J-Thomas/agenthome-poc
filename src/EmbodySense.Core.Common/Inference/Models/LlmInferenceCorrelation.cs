using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Inference.Models;

/// <summary>
/// Carries one stable provider-attempt correlation chain through inference, app-server, tools, and audit.
/// </summary>
/// <param name="ProviderAttemptId">The stable provider-attempt identity.</param>
/// <param name="ProviderCorrelationId">The stable provider correlation identity.</param>
/// <param name="ToolAuditCorrelation">Optional governed-tool audit attribution for this attempt.</param>
public sealed record LlmInferenceCorrelation(string ProviderAttemptId, string ProviderCorrelationId, ToolAuditCorrelation? ToolAuditCorrelation = null);
