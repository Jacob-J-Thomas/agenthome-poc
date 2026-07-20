using EmbodySense.Core.Common.Governance.Tools;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

public static class CustomLoopLimits
{
    public const int MaxDefinitionsPerWorkspace = 50;
    public const int MinInferenceSteps = 1;
    public const int MaxInferenceSteps = 5;
    public const int MinAdditionalIterations = 0;
    public const int MaxAdditionalIterations = 10;
    public const int MaxConversationPublicationEffectsPerRun = (MaxInferenceSteps + 1) * (MaxAdditionalIterations + 1);
    public const int MaxModelAttemptsPerRun = 65;
    public const int MaxNameCharacters = 120;
    public const int MaxDescriptionCharacters = 2_000;
    public const int MaxInstructionCharacters = 12_000;
    public const int MaxPresetPromptCharacters = 24_000;
    public const int MaxInvokingConversationCharacters = 24_000;
    public const int MaxInvokingConversationEntries = 384;
    public const int Sha256HexCharacters = 64;
    public const int MaxArtifactIdCharacters = 120;
    public const int MaxMutationOperationIdCharacters = 120;
    public const int MaxRunTracesPerWorkspace = 250;
    public const int MaxRunTraceTombstonesPerWorkspace = 10_000;
    public const int MaxInvocationOperationReceiptsPerWorkspace = 10_000;
    public const int MaxInvocationOperationUtf8Bytes = 512 * 1024;
    public const long MaxInvocationOperationWorkspaceUtf8Bytes = 128L * 1024 * 1024;
    public const int MaxRecentRunsPageSize = 50;
    public const int MaxRunTraceUtf8Bytes = 16 * 1024 * 1024;
    public const int MaxRunTraceTombstoneUtf8Bytes = 16 * 1024;
    public const int MaxRunTraceDeletionOperationUtf8Bytes = 32 * 1024;
    public const long MaxRunTraceWorkspaceUtf8Bytes = 1024L * 1024 * 1024;
    public const int MaxCanonicalModelOutputCharacters = 8_000;
    public const int MaxLogicalProviderRequestCharacters = 256_000;
    public const int MaxRunDetailCharacters = 64_000;
    public const int MaxTraceReferenceCharacters = 512;
    public const long MaxRunExecutionMilliseconds = 30 * 60 * 1_000;
    public const int MaxGovernedToolRequestsPerRun = 30;
    public const int MaxGovernedToolRequestsPerAttempt = 5;
    public const int MaxRecordedGovernedToolRequestsPerRun = MaxGovernedToolRequestsPerRun + 1;
    public const int MaxRecordedGovernedToolRequestsPerAttempt = MaxGovernedToolRequestsPerAttempt + 1;
    public const int MaxGovernedToolTargetCharacters = 1_024;
    public const int MaxGovernedToolArgumentCharacters = 1_024;
    public const int MaxLifecycleControlEventsPerRun = 64;
    public const int ReservedTerminalLifecycleChangedEventsPerRun = 1;
    public const int ReservedPostTerminalIntegrityWarningEventsPerRun = 1;
    public const int MaxNonterminalLifecycleControlEventsPerRun = MaxLifecycleControlEventsPerRun - ReservedTerminalLifecycleChangedEventsPerRun - ReservedPostTerminalIntegrityWarningEventsPerRun;
    public const int MaxTerminalLifecycleControlEventsBeforeIntegrityWarning = MaxLifecycleControlEventsPerRun - ReservedPostTerminalIntegrityWarningEventsPerRun;
    public const int MaxTraceEventsPerRun = 768;
    public const int MaxLifecycleControlDetailCharacters = 1_024;
    public const int MaxAttemptStartEvidenceUtf8Bytes = 45_000;
    public const int MaxFirstAttemptStartEvidenceUtf8Bytes = 265 * 1_024;
    public const int MaxFirstDistinctNodeAttemptStartEvidenceUtf8Bytes = 128 * 1_024;
    private const int MaxJsonEscapedUtf8BytesPerCharacter = 6;
    private const int MaxAttemptOutcomeMetadataUtf8Bytes = 32 * 1_024;
    // The observed and completed events both retain canonical output; six bytes covers the default JSON encoder's worst-case UTF-16 escape.
    public const int MaxAttemptEvidenceReservationUtf8Bytes = (2 * MaxCanonicalModelOutputCharacters * MaxJsonEscapedUtf8BytesPerCharacter) + MaxAttemptOutcomeMetadataUtf8Bytes;
    public const int MaxGovernedToolRequestEvidenceUtf8Bytes = 18 * 1_024;
    public const int MaxGovernedToolGovernanceEvidenceUtf8Bytes = 14 * 1_024;
    public const int MaxGovernedToolOutcomeEvidenceUtf8Bytes = 251 * 1_024;
    public const int MaxGovernedToolReturnEvidenceUtf8Bytes = 1 * 1_024;
    public const int MaxGovernedToolEvidenceReservationUtf8Bytes = MaxGovernedToolRequestEvidenceUtf8Bytes + MaxGovernedToolGovernanceEvidenceUtf8Bytes + MaxGovernedToolOutcomeEvidenceUtf8Bytes + MaxGovernedToolReturnEvidenceUtf8Bytes;
    public const int MaxTraceControlReserveUtf8Bytes = 512 * 1_024;
    public const int MaxTraceControlEventUtf8Bytes = 8 * 1_024;
    public const int MaxPermanentTerminalIntegrityReserveUtf8Bytes = 128 * 1_024;
    public const int MaxToolGovernanceDetailCharacters = 512;
    public const int MaxCanonicalToolResultCharacters = ToolResultFormatter.MaxFormattedCharacters;

    public static int GetMaximumModelAttempts(int inferenceStepCount, int maxAdditionalIterations)
    {
        if (inferenceStepCount < MinInferenceSteps || inferenceStepCount > MaxInferenceSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(inferenceStepCount));
        }

        if (maxAdditionalIterations < MinAdditionalIterations || maxAdditionalIterations > MaxAdditionalIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAdditionalIterations));
        }

        var totalIterations = checked(maxAdditionalIterations + 1);
        var inferenceAttempts = checked(inferenceStepCount * totalIterations);
        var exitAttempts = maxAdditionalIterations;
        return checked(inferenceAttempts + exitAttempts);
    }
}
