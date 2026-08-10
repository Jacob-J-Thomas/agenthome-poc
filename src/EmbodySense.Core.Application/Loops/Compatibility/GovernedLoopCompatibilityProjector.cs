using EmbodySense.Core.Application.Loops.Compatibility.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>
/// Regenerates bounded read-only compatibility views from validated legacy runtime evidence without accepting a caller-supplied canonical binding.
/// </summary>
public static class GovernedLoopCompatibilityProjector
{
    /// <summary>
    /// Projects a transitional default-conversation turn into unbound canonical payloads and explicit gaps.
    /// </summary>
    /// <param name="record">The legacy turn record, or <see langword="null"/> for an unsupported result.</param>
    /// <returns>A partial result for a valid current record, or an unsupported result when public validation fails.</returns>
    /// <remarks>This method never returns a complete result because the source has no exact canonical graph revision or execution binding.</remarks>
    public static GovernedLoopCompatibilityProjectionResult ProjectDefaultConversation(DefaultConversationTurnRecord? record)
    {
        if (record is null
            || !HasBoundedCollection(record.Transitions, GetMaximumDefaultConversationTransitions(), requireNonEmpty: true)
            || !HasBoundedCollection(record.BaseTranscript, GovernedLoopCompatibilityLimits.MaxDefaultTranscriptMessages)
            || !HasBoundedCollection(record.Run?.Metadata, GovernedLoopCompatibilityLimits.MaxDefaultRunMetadataEntries)
            || !HasBoundedCapabilityAdmission(record.CapabilityAdmission))
        {
            return new GovernedLoopCompatibilityUnsupportedResult(GovernedLoopCompatibilitySource.DefaultConversation, GovernedLoopCompatibilityGapCode.AdapterInputBoundsExceeded);
        }

        return DefaultConversationCompatibilityMapper.Project(record);
    }

    /// <summary>
    /// Projects a first-wave ordered custom-loop run into unbound canonical payloads and explicit gaps.
    /// </summary>
    /// <param name="run">The legacy custom-loop run, or <see langword="null"/> for an unsupported result.</param>
    /// <returns>A partial result for a valid current run, or an unsupported result when public validation fails.</returns>
    /// <remarks>This method never returns a complete result because the source has no exact canonical graph revision or durable graph frontier.</remarks>
    public static GovernedLoopCompatibilityProjectionResult ProjectCustomLoop(CustomLoopRunRecord? run)
    {
        if (!HasBoundedCustomCollections(run))
        {
            return new GovernedLoopCompatibilityUnsupportedResult(GovernedLoopCompatibilitySource.CustomLoop, GovernedLoopCompatibilityGapCode.AdapterInputBoundsExceeded);
        }

        return CustomLoopCompatibilityMapper.Project(run!);
    }

    private static bool HasBoundedCustomCollections(CustomLoopRunRecord? run)
    {
        if (run?.Events is not { Length: >= 1 } events
            || events.Length > CustomLoopLimits.MaxTraceEventsPerRun
            || run.ContextSnapshot?.SourceManifest is not { Length: <= GovernedLoopCompatibilityLimits.MaxCustomContextManifestSources }
            || run.Checkpoint?.EarlierRetainedOutputs is not { Length: <= GovernedLoopCompatibilityLimits.MaxCustomRetainedOutputs }
            || run.AdmittedDefinition?.InferenceSteps is not { Length: <= CustomLoopLimits.MaxInferenceSteps }
            || !HasBoundedAssignments(run.AdmittedDefinition?.ToolAssignments)
            || !HasBoundedCapabilityManifest(run.AdmittedDefinition?.CapabilityRequirements)
            || !HasBoundedCapabilityAdmission(run.CapabilityAdmission))
        {
            return false;
        }

        foreach (var item in events)
        {
            if (item?.ContextBlocks is not { Length: <= GovernedLoopCompatibilityLimits.MaxCustomContextBlocks }
                || !HasBoundedToolAuthority(item.ToolAuthority)
                || item.ToolEvidence is { Authority: null }
                || !HasBoundedToolAuthority(item.ToolEvidence?.Authority))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasBoundedToolAuthority(CustomLoopToolAuthoritySnapshot? authority)
    {
        return authority is null
            || HasBoundedAssignments(authority.AdmittedMaximum)
            && HasBoundedAssignments(authority.CurrentRoleCeiling)
            && HasBoundedAssignments(authority.ImplementedCatalog)
            && HasBoundedAssignments(authority.EffectiveAssignments);
    }

    private static bool HasBoundedAssignments(CustomLoopToolAssignment[]? assignments)
    {
        return assignments is not null && assignments.Length <= GovernedLoopCompatibilityLimits.MaxCustomToolAssignments;
    }

    private static bool HasBoundedCapabilityAdmission(CapabilityAdmissionSnapshot? snapshot)
    {
        return snapshot is not null
            && HasBoundedCapabilityManifest(snapshot.Requirements)
            && HasBoundedCollection(snapshot.Pins, CapabilityContractLimits.MaxCapabilityAdmissionPins)
            && HasBoundedCollection(snapshot.Evidence, CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries);
    }

    private static bool HasBoundedCapabilityManifest(CapabilityDependencyManifest? manifest)
    {
        return manifest is not null
            && HasBoundedCollection(manifest.Required, CapabilityContractLimits.MaxDependencyManifestDependencies)
            && HasBoundedCollection(manifest.Optional, CapabilityContractLimits.MaxDependencyManifestDependencies);
    }

    private static bool HasBoundedCollection<T>(IReadOnlyCollection<T>? values, int maximum, bool requireNonEmpty = false)
    {
        if (values is null)
        {
            return false;
        }

        try
        {
            var declaredCount = values.Count;
            if (declaredCount > maximum || requireNonEmpty && declaredCount == 0)
            {
                return false;
            }

            var enumerated = 0;
            foreach (var _ in values)
            {
                enumerated++;
                if (enumerated > maximum)
                {
                    return false;
                }
            }

            return enumerated == declaredCount;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static int GetMaximumDefaultConversationTransitions()
    {
        return Enum.GetValues<DefaultConversationTurnCheckpoint>()
            .Count(checkpoint => checkpoint != DefaultConversationTurnCheckpoint.Unknown);
    }
}
