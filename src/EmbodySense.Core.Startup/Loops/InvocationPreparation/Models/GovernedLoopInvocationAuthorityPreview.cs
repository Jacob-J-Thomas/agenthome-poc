using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Projects a non-persisted, server-derived least-authority confirmation preview.</summary>
/// <param name="SemanticHash">The exact stable server-computed authority-input digest that confirmation must echo.</param>
/// <param name="Publication">The exact current publication covered by the preview.</param>
/// <param name="AsOfUtc">The trusted server time at which the preview was evaluated.</param>
/// <param name="ExpiresAtUtc">The server-owned expiry projection, when a current time boundary exists.</param>
/// <remarks>The projection deliberately omits raw role policy, profile, capability, workspace, and authority payloads.</remarks>
public sealed record GovernedLoopInvocationAuthorityPreview(
    string SemanticHash,
    GovernedLoopRevisionPublicationPin Publication,
    DateTimeOffset AsOfUtc,
    DateTimeOffset? ExpiresAtUtc);
