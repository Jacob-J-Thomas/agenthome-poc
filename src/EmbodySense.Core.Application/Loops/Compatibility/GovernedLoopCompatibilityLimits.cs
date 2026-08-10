using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>Defines finite bounds for regenerated legacy compatibility projections.</summary>
public static class GovernedLoopCompatibilityLimits
{
    /// <summary>Gets the maximum default-conversation transcript messages accepted by the read-only adapter.</summary>
    /// <remarks>This adapter-safety bound matches current Startup transcript ingestion; it does not redefine the legacy protocol.</remarks>
    public const int MaxDefaultTranscriptMessages = 200;

    /// <summary>Gets the maximum default-conversation run-metadata entries accepted by the read-only adapter.</summary>
    /// <remarks>This is an adapter-safety bound for projection work; it does not redefine the legacy protocol.</remarks>
    public const int MaxDefaultRunMetadataEntries = 128;

    /// <summary>Gets the maximum admitted custom-loop context-manifest sources accepted by the adapter.</summary>
    public const int MaxCustomContextManifestSources = 7 + CustomLoopLimits.MaxInvokingConversationEntries + 1;

    /// <summary>Gets the maximum earlier retained outputs possible within the admitted iteration and step ceilings.</summary>
    public const int MaxCustomRetainedOutputs = CustomLoopLimits.MaxInferenceSteps * (CustomLoopLimits.MaxAdditionalIterations + 1);

    /// <summary>Gets the maximum per-attempt custom-loop context blocks accepted by the adapter.</summary>
    public const int MaxCustomContextBlocks = 1 + 7 + 1 + 1 + 1 + CustomLoopLimits.MaxInvokingConversationEntries + MaxCustomRetainedOutputs + 1;

    /// <summary>Gets the number of implemented custom-loop tool assignment families accepted in one authority set.</summary>
    public const int MaxCustomToolAssignments = 3;

    /// <summary>Gets the maximum distinct static gaps in one result.</summary>
    public const int MaxGaps = 32;

    /// <summary>Gets the maximum effect observations regenerated from one source.</summary>
    public const int MaxEffectObservations = 512;

    /// <summary>Gets the maximum projection observations regenerated from one source.</summary>
    public const int MaxProjectionObservations = 1_024;
}
