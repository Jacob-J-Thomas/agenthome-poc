using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Defines the closed schema-1 authoring contract for one data-only Human Input node.</summary>
/// <remarks>The configuration captures request intent only. It neither creates a request nor represents a checkpoint, response, approval, authority grant, notification, or continuation.</remarks>
/// <param name="SchemaVersion">The configuration schema version, which must be 1.</param>
/// <param name="RequestSchemaReference">The exact graph value-schema reference used by the node's response output.</param>
/// <param name="Purpose">The bounded display-safe data-collection purpose.</param>
/// <param name="Prompt">The bounded display-safe data-collection prompt.</param>
/// <param name="ResponseSchema">The exact untrusted typed response schema.</param>
/// <param name="PrivacyClass">The bounded handling classification for the untrusted exchange.</param>
/// <param name="EligibleRespondents">The exact canonically ordered eligible respondent and route declarations.</param>
/// <param name="ResponsePolicy">The data-only response selection policy.</param>
/// <param name="TimeoutPolicyReference">The exact opaque reference to the authored timeout policy.</param>
/// <param name="FailurePolicyReference">The exact opaque reference to the authored failure policy.</param>
public sealed record GovernedLoopHumanInputNodeConfiguration(
    int SchemaVersion,
    string? RequestSchemaReference,
    string? Purpose,
    string? Prompt,
    HumanInputResponseSchema? ResponseSchema,
    HumanInputPrivacyClass PrivacyClass,
    IReadOnlyList<HumanInputEligibleRespondent?>? EligibleRespondents,
    HumanInputResponsePolicy? ResponsePolicy,
    string? TimeoutPolicyReference,
    string? FailurePolicyReference)
{
    /// <summary>Gets the only supported Human Input graph configuration schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
