namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record HumanInputNodeConfigurationJson(
    int SchemaVersion,
    string? RequestSchemaReference,
    string? Purpose,
    string? Prompt,
    HumanInputResponseSchemaJson? ResponseSchema,
    string? PrivacyClass,
    HumanInputEligibleRespondentJson?[]? EligibleRespondents,
    HumanInputResponsePolicyJson? ResponsePolicy,
    string? TimeoutPolicyReference,
    string? FailurePolicyReference);
