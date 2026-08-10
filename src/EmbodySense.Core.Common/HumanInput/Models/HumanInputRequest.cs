namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines a schema-1 bounded human-input request. It carries untrusted data collection requirements and no approval, consent, credential, or authority semantics.
/// </summary>
/// <param name="SchemaVersion">The request schema version.</param>
/// <param name="RequestId">The stable request ID.</param>
/// <param name="RequestVersionId">The stable immutable request-version ID.</param>
/// <param name="Binding">The exact workspace, loop graph and revision, node, run, and checkpoint binding.</param>
/// <param name="Purpose">The bounded canonical data-collection purpose.</param>
/// <param name="Prompt">The bounded canonical prompt, treated as display data.</param>
/// <param name="ResponseSchema">The required typed response schema.</param>
/// <param name="PrivacyClass">The required handling classification.</param>
/// <param name="EligibleRespondents">The explicitly routed eligible respondents.</param>
/// <param name="Timing">The finite response window.</param>
/// <param name="ResponsePolicy">The explicit data-selection policy.</param>
/// <param name="ContinuationBinding">The non-ambient future data-visibility binding.</param>
/// <param name="RequestHash">The canonical SHA-256 hash of all behavior-affecting request fields.</param>
public sealed partial record HumanInputRequest(int SchemaVersion, string RequestId, string RequestVersionId, HumanInputRequestBinding Binding, string Purpose, string Prompt, HumanInputResponseSchema ResponseSchema, HumanInputPrivacyClass PrivacyClass, HumanInputEligibleRespondent[] EligibleRespondents, HumanInputTiming Timing, HumanInputResponsePolicy ResponsePolicy, HumanInputContinuationBinding ContinuationBinding, string RequestHash)
{
    /// <summary>Schema version required by this contract.</summary>
    public const int CurrentSchemaVersion = 1;
}
