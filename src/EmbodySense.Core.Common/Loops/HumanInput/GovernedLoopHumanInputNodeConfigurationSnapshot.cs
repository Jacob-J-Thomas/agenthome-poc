using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.HumanInput;

internal static class GovernedLoopHumanInputNodeConfigurationSnapshot
{
    internal static GovernedLoopHumanInputNodeConfiguration? Copy(GovernedLoopHumanInputNodeConfiguration? value)
        => value is null
            ? null
            : new GovernedLoopHumanInputNodeConfiguration(
                value.SchemaVersion,
                value.RequestSchemaReference,
                value.Purpose,
                value.Prompt,
                Copy(value.ResponseSchema),
                value.PrivacyClass,
                Copy(value.EligibleRespondents),
                Copy(value.ResponsePolicy),
                value.TimeoutPolicyReference,
                value.FailurePolicyReference);

    private static HumanInputResponseSchema? Copy(HumanInputResponseSchema? value)
        => value is null
            ? null
            : new HumanInputResponseSchema(value.Kind, value.MaxTextCharacters, Copy(value.Choices), Copy(value.StructuredFields), Copy(value.ReferencePolicy));

    private static HumanInputChoice[]? Copy(HumanInputChoice[]? values)
        => values?.Take(HumanInputLimits.MaxChoices + 1).Select(value => value is null ? null! : new HumanInputChoice(value.ChoiceId, value.DisplayText)).ToArray();

    private static HumanInputStructuredFieldSchema[]? Copy(HumanInputStructuredFieldSchema[]? values)
        => values?.Take(HumanInputLimits.MaxStructuredFields + 1).Select(value => value is null
            ? null!
            : new HumanInputStructuredFieldSchema(value.FieldId, value.Kind, value.Required, value.MaxTextCharacters, Copy(value.Choices))).ToArray();

    private static HumanInputReferencePolicy? Copy(HumanInputReferencePolicy? value)
        => value is null ? null : new HumanInputReferencePolicy(value.Kind, value.MaxReferenceCharacters);

    private static IReadOnlyList<HumanInputEligibleRespondent?>? Copy(IReadOnlyList<HumanInputEligibleRespondent?>? values)
        => values is null
            ? null
            : Array.AsReadOnly(values.Take(HumanInputLimits.MaxEligibleRespondents + 1).Select(value => value is null
                ? null
                : new HumanInputEligibleRespondent(value.RespondentId, value.RespondentRoleId, value.RoutingReference)).ToArray());

    private static HumanInputResponsePolicy? Copy(HumanInputResponsePolicy? value)
        => value is null
            ? null
            : new HumanInputResponsePolicy(
                value.Kind,
                value.RequiredResponseCount,
                value.OrderedRoleIds is { } roles ? roles.Take(HumanInputLimits.MaxResponsePolicyRoles + 1).ToImmutableArray() : null);
}
