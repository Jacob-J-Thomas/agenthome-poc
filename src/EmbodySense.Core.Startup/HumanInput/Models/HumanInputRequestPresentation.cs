using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Projects the display-safe request contract needed to supply untrusted data without routing or authority details.</summary>
/// <param name="RequestVersionId">The exact immutable current request-version identifier.</param>
/// <param name="RequestHash">The exact canonical request-content hash.</param>
/// <param name="Purpose">The bounded display-safe data-collection purpose.</param>
/// <param name="Prompt">The bounded display-safe prompt.</param>
/// <param name="ResponseSchema">The typed untrusted data-entry schema.</param>
/// <param name="PrivacyClass">The required data-handling classification.</param>
/// <param name="Timing">The finite response window.</param>
/// <param name="ResponsePolicyKind">The policy family without respondent or selector role identities.</param>
/// <param name="RequiredResponseCount">The policy's bounded numeric threshold when applicable.</param>
public sealed record HumanInputRequestPresentation(
    string RequestVersionId,
    string RequestHash,
    string Purpose,
    string Prompt,
    HumanInputResponseSchema ResponseSchema,
    HumanInputPrivacyClass PrivacyClass,
    HumanInputTiming Timing,
    HumanInputResponsePolicyKind ResponsePolicyKind,
    int? RequiredResponseCount);
