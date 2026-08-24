using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Common.Loops.Custom;

/// <summary>
/// Defines the supported custom loop limits.
/// </summary>
public static class CustomLoopLimits
{
    /// <summary>
    /// Maximum nodes in a canonical governed graph.
    /// </summary>
    public const int MaxGraphNodes = 128;
    /// <summary>
    /// Maximum control-flow edges in a canonical governed graph.
    /// </summary>
    public const int MaxGraphControlEdges = 512;
    /// <summary>
    /// Maximum typed bindings in a canonical governed graph.
    /// </summary>
    public const int MaxGraphBindings = 1_024;
    /// <summary>
    /// Maximum ports declared by one node.
    /// </summary>
    public const int MaxGraphPortsPerNode = 64;
    /// <summary>
    /// Maximum value schemas in a canonical governed graph.
    /// </summary>
    public const int MaxGraphValueSchemas = 128;
    /// <summary>
    /// Maximum declared loop outputs.
    /// </summary>
    public const int MaxGraphOutputs = 64;
    /// <summary>
    /// Maximum executable descriptor parameters on one node.
    /// </summary>
    public const int MaxGraphDescriptorParameters = 64;
    /// <summary>
    /// Maximum authority capabilities in one ceiling.
    /// </summary>
    public const int MaxGraphAuthorityCapabilities = 128;
    /// <summary>
    /// Maximum characters in a descriptor parameter value.
    /// </summary>
    public const int MaxGraphParameterValueCharacters = 8_192;
    /// <summary>
    /// Maximum absolute canvas coordinate accepted as display-only metadata.
    /// </summary>
    public const int MaxGraphCanvasCoordinate = 1_000_000;
    /// <summary>
    /// Maximum control-flow fan-out from one graph node.
    /// </summary>
    public const int MaxGraphControlFanOut = 16;
    /// <summary>
    /// Maximum depth of the acyclic control-flow graph after cycles are condensed.
    /// </summary>
    public const int MaxGraphControlDepth = 64;
    /// <summary>
    /// Maximum structured errors returned by graph normalization and validation.
    /// </summary>
    public const int MaxGraphValidationErrors = 128;
    /// <summary>
    /// Maximum characters in a graph validation error code.
    /// </summary>
    public const int MaxGraphValidationErrorCodeCharacters = 64;
    /// <summary>
    /// Maximum characters in a graph validation element path.
    /// </summary>
    public const int MaxGraphValidationErrorPathCharacters = 256;
    /// <summary>
    /// Maximum characters in a graph validation error message.
    /// </summary>
    public const int MaxGraphValidationErrorMessageCharacters = 512;
    /// <summary>
    /// Maximum explicit iterations permitted for one cyclic node descriptor.
    /// </summary>
    public const int MaxGraphCycleIterations = 10_000;
    /// <summary>
    /// Maximum explicit wall-clock budget permitted for one cyclic node descriptor.
    /// </summary>
    public const long MaxGraphCycleMilliseconds = 24L * 60 * 60 * 1_000;
    /// <summary>
    /// Maximum model or actuator attempts declared by one node descriptor.
    /// </summary>
    public const int MaxGraphNodeAttempts = MaxModelAttemptsPerRun;
    /// <summary>
    /// Maximum payload characters declared by one node descriptor.
    /// </summary>
    public const int MaxGraphNodePayloadCharacters = MaxLogicalProviderRequestCharacters;
    /// <summary>
    /// Maximum evidence items declared by one node descriptor.
    /// </summary>
    public const int MaxGraphNodeEvidenceItems = MaxGovernedToolRequestsPerRun;
    /// <summary>
    /// Maximum abstract resource units declared by one node descriptor.
    /// </summary>
    public const int MaxGraphNodeResourceUnits = 100_000;
    /// <summary>
    /// Maximum sequential dispatch and terminal evidence records retained by one graph activation.
    /// </summary>
    public const int MaxGraphSequentialEvidenceItemsPerActivation = 2;
    /// <summary>
    /// Maximum graph-wide attempt envelope admitted for one run.
    /// </summary>
    public const int MaxGraphAggregateAttempts = MaxModelAttemptsPerRun;
    /// <summary>
    /// Maximum graph-wide payload-character envelope admitted across all retained activations.
    /// </summary>
    public const int MaxGraphAggregatePayloadCharacters = MaxGraphNodePayloadCharacters * GovernedLoopExecutionLimits.MaxFrontierNodes;
    /// <summary>
    /// Maximum graph-wide sequential-evidence envelope admitted across all retained activations.
    /// </summary>
    public const int MaxGraphAggregateEvidenceItems = MaxGraphSequentialEvidenceItemsPerActivation * GovernedLoopExecutionLimits.MaxFrontierNodes;
    /// <summary>
    /// Maximum graph-wide abstract resource-unit envelope admitted for one run.
    /// </summary>
    public const int MaxGraphAggregateResourceUnits = MaxGraphNodeResourceUnits;
    /// <summary>
    /// Maximum UTF-8 bytes in one canonical typed graph value envelope.
    /// </summary>
    public const int MaxGraphTypedValueUtf8Bytes = 256 * 1024;
    /// <summary>
    /// Maximum decoded characters in one string or object-property name carried by a typed graph value.
    /// </summary>
    public const int MaxGraphTypedValueStringCharacters = 64 * 1024;
    /// <summary>
    /// Maximum decoded characters in one object-property name carried by a typed graph value.
    /// </summary>
    public const int MaxGraphTypedValuePropertyNameCharacters = 256;
    /// <summary>
    /// Maximum nested depth in one typed graph value.
    /// </summary>
    public const int MaxGraphTypedValueDepth = 32;
    /// <summary>
    /// Maximum JSON values in one typed graph value.
    /// </summary>
    public const int MaxGraphTypedValueElements = 4_096;
    /// <summary>
    /// Maximum entries in one typed graph array or object.
    /// </summary>
    public const int MaxGraphTypedValueCollectionEntries = 1_024;
    /// <summary>
    /// Maximum characters in one finite typed-value numeric token.
    /// </summary>
    public const int MaxGraphTypedValueNumberCharacters = 128;
    /// <summary>
    /// Maximum characters in the signed exponent portion of one finite typed-value number.
    /// </summary>
    public const int MaxGraphTypedValueExponentCharacters = 6;
    /// <summary>
    /// Maximum path/code observations retained by one deterministic validation node.
    /// </summary>
    public const int MaxGraphPureNodeObservations = 64;
    /// <summary>
    /// Maximum UTF-8 bytes in one canonical pure-node outcome artifact.
    /// </summary>
    public const int MaxGraphPureNodeOutcomeUtf8Bytes = 4 * 1024 * 1024;
    /// <summary>
    /// Maximum trace capacity reserved before pure-node execution for the complete outcome plus bounded event and registry metadata.
    /// </summary>
    public const int MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes = (((MaxGraphPureNodeOutcomeUtf8Bytes + 2) / 3) * 4) + (256 * 1024);
    /// <summary>
    /// Maximum definitions per workspace.
    /// </summary>
    public const int MaxDefinitionsPerWorkspace = 50;
    /// <summary>
    /// Minimum inference steps.
    /// </summary>
    public const int MinInferenceSteps = 1;
    /// <summary>
    /// Maximum inference steps.
    /// </summary>
    public const int MaxInferenceSteps = 5;
    /// <summary>
    /// Minimum additional iterations.
    /// </summary>
    public const int MinAdditionalIterations = 0;
    /// <summary>
    /// Maximum additional iterations.
    /// </summary>
    public const int MaxAdditionalIterations = 10;
    /// <summary>
    /// Maximum conversation publication effects per run.
    /// </summary>
    public const int MaxConversationPublicationEffectsPerRun = (MaxInferenceSteps + 1) * (MaxAdditionalIterations + 1);
    /// <summary>
    /// Maximum model attempts per run.
    /// </summary>
    public const int MaxModelAttemptsPerRun = 65;
    /// <summary>
    /// Maximum name characters.
    /// </summary>
    public const int MaxNameCharacters = 120;
    /// <summary>
    /// Maximum description characters.
    /// </summary>
    public const int MaxDescriptionCharacters = 2_000;
    /// <summary>
    /// Maximum instruction characters.
    /// </summary>
    public const int MaxInstructionCharacters = 12_000;
    /// <summary>
    /// Maximum preset prompt characters.
    /// </summary>
    public const int MaxPresetPromptCharacters = 24_000;
    /// <summary>
    /// Maximum invoking conversation characters.
    /// </summary>
    public const int MaxInvokingConversationCharacters = 24_000;
    /// <summary>
    /// Maximum invoking conversation entries.
    /// </summary>
    public const int MaxInvokingConversationEntries = 384;
    /// <summary>
    /// Number of lowercase hexadecimal characters in a SHA-256 digest.
    /// </summary>
    public const int Sha256HexCharacters = 64;
    /// <summary>
    /// Maximum artifact ID characters.
    /// </summary>
    public const int MaxArtifactIdCharacters = 120;
    /// <summary>
    /// Maximum mutation operation ID characters.
    /// </summary>
    public const int MaxMutationOperationIdCharacters = 120;
    /// <summary>
    /// Maximum run traces per workspace.
    /// </summary>
    public const int MaxRunTracesPerWorkspace = 250;
    /// <summary>
    /// Maximum run trace tombstones per workspace.
    /// </summary>
    public const int MaxRunTraceTombstonesPerWorkspace = 10_000;
    /// <summary>
    /// Maximum run trace deletion operations per workspace.
    /// </summary>
    public const int MaxRunTraceDeletionOperationsPerWorkspace = 20_000;
    /// <summary>
    /// Number of run trace deletion operations for tombstones reserved for integrity-preserving state.
    /// </summary>
    public const int ReservedRunTraceDeletionOperationsForTombstones = MaxRunTraceTombstonesPerWorkspace;
    /// <summary>
    /// Maximum invocation operation receipts per workspace.
    /// </summary>
    public const int MaxInvocationOperationReceiptsPerWorkspace = 10_000;
    /// <summary>
    /// Maximum invocation operation UTF-8 bytes.
    /// </summary>
    public const int MaxInvocationOperationUtf8Bytes = 512 * 1024;
    /// <summary>
    /// Maximum invocation operation workspace UTF-8 bytes.
    /// </summary>
    public const long MaxInvocationOperationWorkspaceUtf8Bytes = 128L * 1024 * 1024;
    /// <summary>
    /// Maximum invocation receipt retention operation UTF-8 bytes.
    /// </summary>
    public const int MaxInvocationReceiptRetentionOperationUtf8Bytes = 4 * 1024 * 1024;
    /// <summary>
    /// Maximum invocation validation errors.
    /// </summary>
    public const int MaxInvocationValidationErrors = 24;
    /// <summary>
    /// Maximum invocation validation error code characters.
    /// </summary>
    public const int MaxInvocationValidationErrorCodeCharacters = 64;
    /// <summary>
    /// Maximum invocation validation error field characters.
    /// </summary>
    public const int MaxInvocationValidationErrorFieldCharacters = 128;
    /// <summary>
    /// Maximum invocation validation error message characters.
    /// </summary>
    public const int MaxInvocationValidationErrorMessageCharacters = 512;
    /// <summary>
    /// Maximum recent runs page size.
    /// </summary>
    public const int MaxRecentRunsPageSize = 50;
    /// <summary>
    /// Maximum run page cursor characters.
    /// </summary>
    public const int MaxRunPageCursorCharacters = 1_024;
    /// <summary>
    /// Maximum run discovery index UTF-8 bytes.
    /// </summary>
    public const int MaxRunDiscoveryIndexUtf8Bytes = 16 * 1024 * 1024;
    /// <summary>
    /// Maximum run trace UTF-8 bytes.
    /// </summary>
    public const int MaxRunTraceUtf8Bytes = 16 * 1024 * 1024;
    /// <summary>
    /// Maximum run trace tombstone UTF-8 bytes.
    /// </summary>
    public const int MaxRunTraceTombstoneUtf8Bytes = 16 * 1024;
    /// <summary>
    /// Maximum run trace deletion operation UTF-8 bytes.
    /// </summary>
    public const int MaxRunTraceDeletionOperationUtf8Bytes = 32 * 1024;
    /// <summary>
    /// Maximum run trace workspace UTF-8 bytes.
    /// </summary>
    public const long MaxRunTraceWorkspaceUtf8Bytes = 1024L * 1024 * 1024;
    /// <summary>
    /// Maximum canonical model output characters.
    /// </summary>
    public const int MaxCanonicalModelOutputCharacters = 8_000;
    /// <summary>
    /// Maximum logical provider request characters.
    /// </summary>
    public const int MaxLogicalProviderRequestCharacters = 256_000;
    /// <summary>
    /// Maximum run detail characters.
    /// </summary>
    public const int MaxRunDetailCharacters = 64_000;
    /// <summary>
    /// Maximum trace reference characters.
    /// </summary>
    public const int MaxTraceReferenceCharacters = 512;
    /// <summary>
    /// Maximum run execution milliseconds.
    /// </summary>
    public const long MaxRunExecutionMilliseconds = 30 * 60 * 1_000;
    /// <summary>
    /// Maximum governed tool requests per run.
    /// </summary>
    public const int MaxGovernedToolRequestsPerRun = 30;
    /// <summary>
    /// Maximum governed tool requests per attempt.
    /// </summary>
    public const int MaxGovernedToolRequestsPerAttempt = 5;
    /// <summary>
    /// Maximum model visible governed tool requests per run.
    /// </summary>
    public const int MaxModelVisibleGovernedToolRequestsPerRun = MaxGovernedToolRequestsPerRun + 1;
    /// <summary>
    /// Maximum model visible governed tool requests per attempt.
    /// </summary>
    public const int MaxModelVisibleGovernedToolRequestsPerAttempt = MaxGovernedToolRequestsPerAttempt + 1;
    /// <summary>
    /// Maximum recorded governed tool requests per run.
    /// </summary>
    public const int MaxRecordedGovernedToolRequestsPerRun = MaxModelVisibleGovernedToolRequestsPerRun + 1;
    /// <summary>
    /// Maximum recorded governed tool requests per attempt.
    /// </summary>
    public const int MaxRecordedGovernedToolRequestsPerAttempt = MaxModelVisibleGovernedToolRequestsPerAttempt + 1;
    /// <summary>
    /// Maximum governed tool target characters.
    /// </summary>
    public const int MaxGovernedToolTargetCharacters = 1_024;
    /// <summary>
    /// Maximum governed tool argument characters.
    /// </summary>
    public const int MaxGovernedToolArgumentCharacters = 1_024;
    /// <summary>
    /// Maximum lifecycle control events per run.
    /// </summary>
    public const int MaxLifecycleControlEventsPerRun = 64;
    /// <summary>
    /// Number of terminal lifecycle changed events per run reserved for integrity-preserving state.
    /// </summary>
    public const int ReservedTerminalLifecycleChangedEventsPerRun = 1;
    /// <summary>
    /// Number of post terminal integrity warning events per run reserved for integrity-preserving state.
    /// </summary>
    public const int ReservedPostTerminalIntegrityWarningEventsPerRun = 1;
    /// <summary>
    /// Maximum nonterminal lifecycle control events per run.
    /// </summary>
    public const int MaxNonterminalLifecycleControlEventsPerRun = MaxLifecycleControlEventsPerRun - ReservedTerminalLifecycleChangedEventsPerRun - ReservedPostTerminalIntegrityWarningEventsPerRun;
    /// <summary>
    /// Maximum terminal lifecycle control events before integrity warning.
    /// </summary>
    public const int MaxTerminalLifecycleControlEventsBeforeIntegrityWarning = MaxLifecycleControlEventsPerRun - ReservedPostTerminalIntegrityWarningEventsPerRun;
    /// <summary>
    /// Maximum trace events per run.
    /// </summary>
    public const int MaxTraceEventsPerRun = 768;
    /// <summary>
    /// Maximum lifecycle control detail characters.
    /// </summary>
    public const int MaxLifecycleControlDetailCharacters = 1_024;
    /// <summary>
    /// Maximum append-only retry-state detail characters.
    /// </summary>
    /// <remarks>
    /// Retry-state events retain only bounded server-owned transition detail. This keeps each
    /// authenticated retry transition within its independently reserved trace footprint.
    /// </remarks>
    public const int MaxRetryStateDetailCharacters = MaxLifecycleControlDetailCharacters;
    /// <summary>
    /// Maximum attempt start evidence UTF-8 bytes.
    /// </summary>
    public const int MaxAttemptStartEvidenceUtf8Bytes = 45_000;
    /// <summary>
    /// Maximum first attempt start evidence UTF-8 bytes.
    /// </summary>
    public const int MaxFirstAttemptStartEvidenceUtf8Bytes = 265 * 1_024;
    /// <summary>
    /// Maximum first distinct node attempt start evidence UTF-8 bytes.
    /// </summary>
    public const int MaxFirstDistinctNodeAttemptStartEvidenceUtf8Bytes = 128 * 1_024;
    private const int MaxJsonEscapedUtf8BytesPerCharacter = 6;
    private const int MaxAttemptOutcomeMetadataUtf8Bytes = 32 * 1_024;
    // The observed and completed events both retain canonical output; six bytes covers the default JSON encoder's worst-case UTF-16 escape.
    /// <summary>
    /// Maximum attempt evidence reservation UTF-8 bytes.
    /// </summary>
    public const int MaxAttemptEvidenceReservationUtf8Bytes = (2 * MaxCanonicalModelOutputCharacters * MaxJsonEscapedUtf8BytesPerCharacter) + MaxAttemptOutcomeMetadataUtf8Bytes;
    /// <summary>
    /// Maximum governed tool request evidence UTF-8 bytes.
    /// </summary>
    public const int MaxGovernedToolRequestEvidenceUtf8Bytes = 18 * 1_024;
    /// <summary>
    /// Maximum governed tool governance evidence UTF-8 bytes.
    /// </summary>
    public const int MaxGovernedToolGovernanceEvidenceUtf8Bytes = 20 * 1_024;
    /// <summary>
    /// Maximum governed tool outcome evidence UTF-8 bytes.
    /// </summary>
    public const int MaxGovernedToolOutcomeEvidenceUtf8Bytes = 251 * 1_024;
    /// <summary>
    /// Maximum governed tool return evidence UTF-8 bytes.
    /// </summary>
    public const int MaxGovernedToolReturnEvidenceUtf8Bytes = 8 * 1_024;
    /// <summary>
    /// Maximum repeated governed tool request integrity evidence UTF-8 bytes.
    /// </summary>
    public const int MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes = MaxGovernedToolRequestEvidenceUtf8Bytes;
    /// <summary>
    /// Maximum governed tool evidence reservation UTF-8 bytes.
    /// </summary>
    public const int MaxGovernedToolEvidenceReservationUtf8Bytes = MaxGovernedToolRequestEvidenceUtf8Bytes + MaxGovernedToolGovernanceEvidenceUtf8Bytes + MaxGovernedToolOutcomeEvidenceUtf8Bytes + MaxGovernedToolReturnEvidenceUtf8Bytes;
    /// <summary>
    /// Maximum trace control reserve UTF-8 bytes.
    /// </summary>
    public const int MaxTraceControlReserveUtf8Bytes = 512 * 1_024;
    /// <summary>
    /// Maximum trace control event UTF-8 bytes.
    /// </summary>
    public const int MaxTraceControlEventUtf8Bytes = 8 * 1_024;
    /// <summary>
    /// Maximum append-only retry-state event UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Retry-state transitions are node evidence rather than lifecycle control events. The
    /// persistence store reserves this bounded footprint for every required successor without
    /// consuming the permanent lifecycle and terminalization reserve. The 12 KiB ceiling is a
    /// conservative schema-1 bound: 1,024 UTF-16 detail code units can encode to 6,144 bytes,
    /// sixteen identifier/workspace fields consume at most 1,920 ASCII bytes, ten SHA-256 values
    /// consume 640 ASCII bytes, and 2 KiB remains for the fixed canonical JSON structure, numeric
    /// values, timestamps, enums, and content-registry reference. Retry events cannot carry
    /// context, output, tool, failure, model, or sequential-evidence payloads. The public
    /// persistence maximum-detail test guards the real compact encoded delta against this ceiling.
    /// </remarks>
    public const int MaxRetryStateEventUtf8Bytes = 12 * 1_024;
    /// <summary>
    /// Maximum permanent terminal integrity reserve UTF-8 bytes.
    /// </summary>
    public const int MaxPermanentTerminalIntegrityReserveUtf8Bytes = (128 * 1_024) + MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes;
    /// <summary>
    /// Maximum tool governance detail characters.
    /// </summary>
    public const int MaxToolGovernanceDetailCharacters = 512;
    /// <summary>
    /// Maximum canonical tool result characters.
    /// </summary>
    public const int MaxCanonicalToolResultCharacters = ToolResultFormatter.MaxFormattedCharacters;

    /// <summary>
    /// Calculates the total provider-attempt ceiling for the configured loop shape.
    /// </summary>
    /// <param name="inferenceStepCount">The number of inference steps executed in every iteration.</param>
    /// <param name="maxAdditionalIterations">The maximum iterations accepted after the first.</param>
    /// <returns>The inference attempts across all possible iterations plus one exit-decision attempt for each possible repeat.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when either argument falls outside the corresponding configured minimum and maximum.</exception>
    /// <exception cref="OverflowException">Thrown when checked attempt arithmetic overflows.</exception>
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
