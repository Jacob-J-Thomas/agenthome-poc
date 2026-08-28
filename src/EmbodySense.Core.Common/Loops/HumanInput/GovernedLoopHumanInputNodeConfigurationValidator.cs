using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.HumanInput;

/// <summary>Validates data-only Human Input graph configuration without creating requests, checkpoints, notifications, responses, or authority.</summary>
public static class GovernedLoopHumanInputNodeConfigurationValidator
{
    private static readonly string[] _authorityTerms = ["approve", "approval", "reject", "review", "authority", "grant"];

    /// <summary>Gets whether one configuration is complete, canonical, display-safe, and free of approval or authority semantics.</summary>
    /// <param name="configuration">The configuration to validate.</param>
    /// <returns><see langword="true"/> only when the complete schema-1 data-collection contract is valid.</returns>
    public static bool IsValid(GovernedLoopHumanInputNodeConfiguration? configuration)
    {
        if (configuration is null
            || configuration.SchemaVersion != GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion
            || !IsSafeReference(configuration.RequestSchemaReference)
            || !HumanInputPolicyReference.TryParse(configuration.TimeoutPolicyReference, out _)
            || !HumanInputPolicyReference.TryParse(configuration.FailurePolicyReference, out _)
            || !IsSafeText(configuration.Purpose, HumanInputLimits.MaxPurposeCharacters)
            || !IsSafeText(configuration.Prompt, HumanInputLimits.MaxPromptCharacters)
            || !HasCanonicalSafeRespondents(configuration.EligibleRespondents)
            || !HasSafeResponseSchema(configuration.ResponseSchema)
            || !HasSafeResponsePolicy(configuration.ResponsePolicy))
        {
            return false;
        }

        try
        {
            var request = new HumanInputRequest(
                HumanInputRequest.CurrentSchemaVersion,
                "graph-human-input-request",
                "graph-human-input-version",
                new HumanInputRequestBinding("workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "graph-1", "revision-1", "node-1", "run-1", "checkpoint-1"),
                configuration.Purpose!,
                configuration.Prompt!,
                configuration.ResponseSchema!,
                configuration.PrivacyClass,
                configuration.EligibleRespondents!.Cast<HumanInputEligibleRespondent>().ToArray(),
                new HumanInputTiming(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2000, 1, 1, 0, 1, 0, TimeSpan.Zero)),
                configuration.ResponsePolicy!,
                new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, "node-1", "checkpoint-1"),
                string.Empty);
            return HumanInputValidator.ValidateRequest(HumanInputRequestHash.Apply(request)).IsValid;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Gets whether a node retains the complete closed Human Input graph contract for its exact response schema.</summary>
    /// <param name="node">The node to validate.</param>
    /// <param name="schemas">The canonical graph schemas indexed by identifier.</param>
    /// <returns><see langword="true"/> only when the node is data-only and its sole response port exactly binds the configured schema reference.</returns>
    public static bool HasExactNodeSemantics(
        GovernedLoopNodeDefinition? node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        if (node is null
            || !GovernedLoopHumanInputVocabulary.IsSupported(node.Descriptor)
            || !IsValid(node.HumanInputConfiguration)
            || node.AuthorityCeiling is null
            || node.AuthorityCeiling.CapabilityIds.Count != 0
            || node.Parameters is null
            || node.Parameters.Count != 0
            || node.ModelRoutingPolicy is not null
            || node.AuthoredInputDataClasses is not null
            || node.RetryPolicy is not null
            || node.Ports is null
            || node.Ports.Count != 1)
        {
            return false;
        }

        var configuration = node.HumanInputConfiguration!;
        var port = node.Ports[0];
        return port is not null
            && string.Equals(port.Id, GovernedLoopHumanInputVocabulary.ResponsePortId, StringComparison.Ordinal)
            && port.Direction == GovernedLoopPortDirection.Output
            && port.BindingKind == GovernedLoopBindingKind.Data
            && port.Required
            && string.Equals(port.ValueSchemaId, configuration.RequestSchemaReference, StringComparison.Ordinal)
            && schemas.TryGetValue(configuration.RequestSchemaReference!, out var schema)
            && TryGetResponseValueKind(configuration.ResponseSchema, out var responseKind)
            && !schema.Nullable
            && schema.Kind == responseKind;
    }

    private static bool HasCanonicalSafeRespondents(IReadOnlyList<HumanInputEligibleRespondent?>? respondents)
    {
        if (respondents is null || respondents.Count is < 1 or > HumanInputLimits.MaxEligibleRespondents)
        {
            return false;
        }

        var copied = respondents.ToArray();
        if (copied.Any(value => value is null)
            || copied.Any(value => !IsSafeIdentifier(value!.RespondentId)
                || !IsSafeIdentifier(value.RespondentRoleId)
                || !IsSafeText(value.RoutingReference, HumanInputLimits.MaxRoutingReferenceCharacters)))
        {
            return false;
        }

        return copied.SequenceEqual(copied.OrderBy(value => value!.RespondentId, StringComparer.Ordinal)
            .ThenBy(value => value!.RespondentRoleId, StringComparer.Ordinal)
            .ThenBy(value => value!.RoutingReference, StringComparer.Ordinal));
    }

    private static bool HasSafeResponseSchema(HumanInputResponseSchema? schema)
    {
        if (schema is null)
        {
            return false;
        }

        return (schema.Choices is null || schema.Choices.All(value => value is not null
                && IsSafeIdentifier(value.ChoiceId)
                && IsSafeText(value.DisplayText, HumanInputLimits.MaxChoiceDisplayCharacters)))
            && (schema.StructuredFields is null || schema.StructuredFields.All(value => value is not null
                && IsSafeIdentifier(value.FieldId)
                && (value.Choices is null || value.Choices.All(choice => choice is not null
                    && IsSafeIdentifier(choice.ChoiceId)
                    && IsSafeText(choice.DisplayText, HumanInputLimits.MaxChoiceDisplayCharacters)))));
    }

    private static bool HasSafeResponsePolicy(HumanInputResponsePolicy? policy)
        => policy is not null
            && (policy.OrderedRoleIds is not { } roles
                || !roles.IsDefault
                && roles.All(IsSafeIdentifier));

    private static bool IsSafeReference(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value)
            && IsSafeText(value, HumanInputLimits.MaxIdentifierCharacters);

    private static bool IsSafeIdentifier(string? value)
        => HumanInputIdentifier.IsValid(value)
            && IsSafeText(value, HumanInputLimits.MaxIdentifierCharacters);

    private static bool IsSafeText(string? value, int maximum)
        => HumanReviewSafeText.IsValid(value, maximum, required: true)
            && !_authorityTerms.Any(term => value!.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetResponseValueKind(HumanInputResponseSchema? schema, out GovernedLoopValueKind kind)
    {
        kind = schema?.Kind switch
        {
            HumanInputResponseKind.Text or HumanInputResponseKind.Choice or HumanInputResponseKind.Reference => GovernedLoopValueKind.Text,
            HumanInputResponseKind.Confirmation => GovernedLoopValueKind.Boolean,
            HumanInputResponseKind.Structured => GovernedLoopValueKind.Object,
            _ => GovernedLoopValueKind.Unknown,
        };
        return kind != GovernedLoopValueKind.Unknown;
    }
}
