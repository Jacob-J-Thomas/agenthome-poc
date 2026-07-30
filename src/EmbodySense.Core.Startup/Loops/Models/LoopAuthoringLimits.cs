namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects the enforced custom-loop authoring, execution, governance, and evidence limits.
/// </summary>
/// <param name="MaxDefinitionsPerWorkspace">Maximum persisted custom definitions in one workspace.</param>
/// <param name="MinInferenceSteps">Minimum ordered inference steps in a valid custom definition.</param>
/// <param name="MaxInferenceSteps">Maximum ordered inference steps in a valid custom definition.</param>
/// <param name="MaxAdditionalIterations">Maximum complete iterations after the first.</param>
/// <param name="MaxModelAttemptsPerRun">Maximum provider attempts across one run.</param>
/// <param name="MaxGovernedToolRequestsPerAttempt">Maximum governed requests observed in one provider attempt.</param>
/// <param name="MaxGovernedToolRequestsPerRun">Maximum governed requests observed across one run.</param>
/// <param name="MaxNameCharacters">Maximum loop or step display-name length.</param>
/// <param name="MaxDescriptionCharacters">Maximum loop description length.</param>
/// <param name="MaxInstructionCharacters">Maximum inference or decision instruction length.</param>
/// <param name="MaxTriggerPromptCharacters">Maximum invocation or preset prompt length.</param>
/// <param name="MaxInvokingConversationCharacters">Maximum admitted conversation content length.</param>
/// <param name="MaxInvokingConversationEntries">Maximum admitted conversation message count.</param>
/// <param name="MaxGovernedToolTargetCharacters">Maximum governed tool target-path length.</param>
/// <param name="MaxGovernedToolArgumentCharacters">Maximum governed tool content or search-pattern length.</param>
/// <param name="MaxToolGovernanceDetailCharacters">Maximum retained governance-detail length.</param>
/// <param name="MaxCanonicalModelOutputCharacters">Maximum canonical model output length.</param>
/// <param name="MaxCanonicalToolResultCharacters">Maximum canonical tool-result length.</param>
/// <param name="MaxLifecycleControlEventsPerRun">Maximum retained pause, cancel, and resume events.</param>
/// <param name="MaxTraceEventsPerRun">Maximum append-only trace events in one run.</param>
/// <param name="MaxLifecycleControlDetailCharacters">Maximum lifecycle-control detail length.</param>
/// <param name="MaxRunTraceUtf8Bytes">Maximum UTF-8 size of one retained run trace.</param>
/// <param name="MaxRunExecutionMilliseconds">Maximum accumulated execution time before failure.</param>
public sealed record LoopAuthoringLimits(
    int MaxDefinitionsPerWorkspace,
    int MinInferenceSteps,
    int MaxInferenceSteps,
    int MaxAdditionalIterations,
    int MaxModelAttemptsPerRun,
    int MaxGovernedToolRequestsPerAttempt,
    int MaxGovernedToolRequestsPerRun,
    int MaxNameCharacters,
    int MaxDescriptionCharacters,
    int MaxInstructionCharacters,
    int MaxTriggerPromptCharacters,
    int MaxInvokingConversationCharacters,
    int MaxInvokingConversationEntries,
    int MaxGovernedToolTargetCharacters,
    int MaxGovernedToolArgumentCharacters,
    int MaxToolGovernanceDetailCharacters,
    int MaxCanonicalModelOutputCharacters,
    int MaxCanonicalToolResultCharacters,
    int MaxLifecycleControlEventsPerRun,
    int MaxTraceEventsPerRun,
    int MaxLifecycleControlDetailCharacters,
    int MaxRunTraceUtf8Bytes,
    long MaxRunExecutionMilliseconds);
