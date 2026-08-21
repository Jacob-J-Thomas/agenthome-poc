using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Common.Inference.Profiles;

/// <summary>Revalidates governed model contracts obtained from hostile persistence, adapter, or surface boundaries.</summary>
public static class GovernedModelContractValidator
{
    /// <summary>Validates a bounded provider response against the exact admitted model before evidence is derived.</summary>
    public static bool IsValidProviderResponse(LlmInferenceResponse? value, string? exactProviderId, string? exactModelId, LlmInferenceSurface exactSurface)
    {
        try
        {
            return value is not null
                && value.OutputText is not null
                && CapabilityTextRules.IsSafeNormalized(value.OutputText, GovernedModelContractLimits.MaxProviderOutputCharacters, allowEmpty: true)
                && value.Surface == exactSurface
                && string.Equals(value.ProviderId, exactProviderId, StringComparison.Ordinal)
                && string.Equals(value.Model, exactModelId, StringComparison.Ordinal)
                && IsProviderReference(value.ProviderResponseId)
                && IsValid(value.Usage);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates profile metadata, every nested value, and its canonical content hash.</summary>
    public static bool IsValid(GovernedModelProfileMetadata? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = GovernedModelProfileMetadata.Create(value.SchemaVersion, value.DescriptorIdentity, value.ProviderId, value.AdapterId, value.ModelId, value.AdapterContractVersion, value.ConfigurationRevision, value.ConfigurationHash, value.PublicPurpose, value.Modalities, value.Capabilities, value.ContextWindowTokens, value.MaximumOutputTokens, value.Privacy, value.UsageSupport, value.PermittedRoleIds, value.PermittedNodeTypeIds);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal)
                && IsValid(value.Privacy)
                && IsValid(value.UsageSupport);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates one exact capability-backed model-profile pin and its canonical content hash.</summary>
    public static bool IsValid(GovernedModelProfilePin? value)
    {
        try
        {
            if (value is null || !IsValid(value.Metadata))
            {
                return false;
            }

            return string.Equals(value.ContentHash, GovernedModelProfilePin.Create(value.Capability, value.Metadata, value.ProfileSourceRevisionHash, value.AdapterRegistryRevisionHash).ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates a privacy posture and canonical content hash.</summary>
    public static bool IsValid(GovernedModelPrivacyPosture? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = GovernedModelPrivacyPosture.Create(1, value.Locality, value.Egress, value.Destinations, value.AcceptedDataClasses, value.Regions, value.Retention, value.Training);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates a privacy requirement and canonical content hash.</summary>
    public static bool IsValid(GovernedModelPrivacyRequirement? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = GovernedModelPrivacyRequirement.Create(1, value.LocalOnly, value.MaximumEgress, value.AllowedDestinations, value.AllowedDataClasses, value.AllowedRegions, value.MaximumRetention, value.MaximumTraining);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates explicit usage support and canonical content hash.</summary>
    public static bool IsValid(GovernedModelUsageSupportPolicy? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = GovernedModelUsageSupportPolicy.Create(value.InputTokens, value.OutputTokens, value.CachedTokens, value.TotalTokens, value.MonetaryCost);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates complete provider-usage evidence and its canonical content hash.</summary>
    public static bool IsValid(LlmInferenceUsageEvidence? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = LlmInferenceUsageEvidence.Create(value.SchemaVersion, value.SourceId, value.SourceContractVersion, value.InputTokens, value.OutputTokens, value.CachedTokens, value.TotalTokens, value.MonetaryCost);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates completed-attempt profile, configuration, reservation, and reconciled usage evidence.</summary>
    public static bool IsValid(GovernedModelAttemptExecutionEvidence? value)
    {
        try
        {
            if (value is null || !IsValid(value.Usage))
            {
                return false;
            }

            var expected = GovernedModelAttemptExecutionEvidence.Create(
                GovernedModelContractLimits.CurrentSchemaVersion,
                value.ProfileId,
                value.ProfilePinHash,
                value.ConfigurationHash,
                value.ProviderId,
                value.AdapterId,
                value.ModelId,
                value.ResponseSurface,
                value.ReservationEntryHash,
                value.TerminalUsageEntryHash,
                value.TerminalUsagePhase,
                value.Usage,
                value.UsageUnknown);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates a complete nested provider-usage budget policy.</summary>
    public static bool IsValid(GovernedModelBudgetPolicy? value)
    {
        try
        {
            if (value is null || !IsValid(value.PerAttempt) || !IsValid(value.PerNodeSeries) || !IsValid(value.PerRun))
            {
                return false;
            }

            var expected = GovernedModelBudgetPolicy.Create(1, value.PerAttempt, value.PerNodeSeries, value.PerRun);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates every nested token and monetary limit in one usage ceiling.</summary>
    public static bool IsValid(GovernedModelUsageCeiling? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = GovernedModelUsageCeiling.Create(value.InputTokens, value.OutputTokens, value.CachedTokens, value.TotalTokens, value.MonetaryCost);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates one exact usage vector and canonical content hash.</summary>
    public static bool IsValid(GovernedModelUsageVector? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = GovernedModelUsageVector.Create(value.InputTokens, value.OutputTokens, value.CachedTokens, value.TotalTokens, value.Currency, value.CostMicros);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates one model-usage ledger identity and canonical content hash.</summary>
    public static bool IsValid(GovernedModelUsageLedgerIdentity? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = GovernedModelUsageLedgerIdentity.Create(1, value.WorkspaceId, value.RunId, value.GraphId, value.GraphRevisionId, value.GraphExecutableHash, value.ExecutionGeneration, value.AdmissionReceiptHash, value.RoutingAdmissionHash, value.AuthorityEvidenceHash, value.DataPostureEvidenceHash, value.NodeId, value.PlanOrdinal, value.ActivationOrdinal, value.VisitOrdinal, value.AttemptOperationId, value.AttemptNumber, value.ProfilePinHash, value.BudgetPolicyHash);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates an exact or bounded-inherit routing selector and canonical hash.</summary>
    public static bool IsValid(GovernedModelRoutingSelector? value)
    {
        try
        {
            if (value is null)
            {
                return false;
            }

            var expected = value.Kind switch
            {
                GovernedModelSelectorKind.Exact when value.ExactProfileId is not null && value.PermittedInheritedProfileIds.Count == 0 => GovernedModelRoutingSelector.Exact(value.ExactProfileId),
                GovernedModelSelectorKind.Inherit when value.ExactProfileId is null => GovernedModelRoutingSelector.Inherit(value.PermittedInheritedProfileIds),
                _ => null
            };
            return expected is not null && string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates common candidate requirements and nested content hashes.</summary>
    public static bool IsValid(GovernedModelProfileRequirements? value)
    {
        try
        {
            if (value is null || !IsValid(value.Privacy) || !IsValid(value.Budget))
            {
                return false;
            }

            var expected = GovernedModelProfileRequirements.Create(1, value.RequiredModalities, value.RequiredCapabilities, value.MinimumContextTokens, value.MinimumOutputTokens, value.Privacy, value.Budget);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates a typed routing policy and every nested content hash.</summary>
    public static bool IsValid(GovernedModelRoutingPolicy? value)
    {
        try
        {
            if (value is null || !IsValid(value.Selector) || !IsValid(value.Requirements))
            {
                return false;
            }

            var expected = GovernedModelRoutingPolicy.Create(1, value.Selector, value.FallbackProfileIds, value.Requirements);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates one routing admission entry and every retained primary/fallback pin.</summary>
    public static bool IsValid(GovernedModelRoutingAdmissionEntry? value)
    {
        try
        {
            if (value is null || !IsValid(value.Requirements) || !IsValid(value.Primary) || value.Fallbacks.Any(item => !IsValid(item)))
            {
                return false;
            }

            var expected = GovernedModelRoutingAdmissionEntry.Create(1, value.NodeId, value.NodeTypeId, value.PolicyHash, value.Requirements, value.HasAuthoredInputClassification, value.AuthoredInputDataClasses, value.Primary, value.Fallbacks);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates a complete routing admission snapshot and every nested entry.</summary>
    public static bool IsValid(GovernedModelRoutingAdmissionSnapshot? value)
    {
        try
        {
            if (value is null || value.Entries.Any(item => !IsValid(item)))
            {
                return false;
            }

            var expected = GovernedModelRoutingAdmissionSnapshot.Create(
                value.SchemaVersion,
                value.WorkspaceId,
                value.AdmissionOperationId,
                value.AdmissionIntentHash,
                value.ExecutionBindingReferenceHash,
                value.RunId,
                value.GraphId,
                value.GraphRevisionId,
                value.GraphExecutableHash,
                value.ExecutionGeneration,
                value.OwningRoleId,
                value.OwningRoleRevision,
                value.OwningRoleContentHash,
                value.CapabilityAdmissionReferenceHash,
                value.AuthorityAdmissionReferenceHash,
                value.CapabilityCatalogRevision,
                value.ResolvedDefaultProfileId,
                value.DefaultSourceRevisionHash,
                value.AdapterRegistryRevisionHash,
                value.EvaluatedAtUtc,
                value.Entries);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validates one append-only usage-ledger entry, nested evidence, and canonical hashes.</summary>
    public static bool IsValid(GovernedModelUsageLedgerEntry? value)
    {
        try
        {
            if (value is null
                || !IsValid(value.Identity)
                || value.Reservation is not null && !IsValid(value.Reservation)
                || value.Usage is not null && !IsValid(value.Usage)
                || value.Used is not null && !IsValid(value.Used)
                || value.Released is not null && !IsValid(value.Released))
            {
                return false;
            }

            var expected = GovernedModelUsageLedgerEntry.Create(1, value.Identity, value.Generation, value.Phase, value.Reservation, value.Usage, value.Used, value.Released, value.UsageUnknown, value.EvidenceHash, value.PreviousEntryHash, value.RecordedAtUtc);
            return string.Equals(value.ContentHash, expected.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProviderReference(string? value)
        => value is null
            || value is { Length: >= 1 and <= GovernedModelContractLimits.MaxIdentifierCharacters }
                && value.IsNormalized(System.Text.NormalizationForm.FormC)
                && value.All(character => character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-' or '_' or '.' or '/' or ':');
}
