using EmbodySense.Core.Common.Inference.Models;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public enum CustomLoopRunStatus
{
    Unknown = 0,
    Admitted = 1,
    Running = 2,
    PauseRequested = 3,
    Paused = 4,
    CancelRequested = 5,
    Completed = 6,
    Failed = 7,
    Cancelled = 8,
    NeedsReview = 9
}

public enum CustomLoopRunEventKind
{
    Unknown = 0,
    Admitted = 1,
    LifecycleChanged = 2,
    IterationStarted = 3,
    NodeAttemptStarted = 4,
    NodeAttemptCompleted = 5,
    NodeOutcomeObserved = 6,
    NodeAttemptFailed = 7,
    ExitDecisionStarted = 8,
    ExitDecisionCompleted = 9,
    ConversationPublicationStarted = 10,
    ConversationPublished = 11,
    CheckpointCommitted = 12,
    IntegrityWarning = 13,
    AdmissionAuditCompleted = 14,
    ToolRequestReserved = 15,
    ToolGovernanceDecided = 16,
    ToolOutcomeObserved = 17,
    ToolIntegrityFailed = 18
}

public enum CustomLoopExitDecision
{
    Unknown = 0,
    Complete = 1,
    Repeat = 2,
    Invalid = 3
}

public enum CustomLoopContextSource
{
    Unknown = 0,
    HarnessGovernance = 1,
    RoleInstruction = 2,
    ContextualState = 3,
    RunMetadata = 4,
    NodeInstruction = 5,
    TriggerPrompt = 6,
    InvokingConversation = 7,
    EarlierRetainedOutput = 8,
    PreviousIterationResult = 9
}

public enum CustomLoopContextProvenance
{
    Unknown = 0,
    HarnessRuntime = 1,
    WorkspaceRoleFile = 2,
    WorkspaceContextFile = 3,
    ServerRunState = 4,
    AuthoredDefinition = 5,
    ManualInvocation = 6,
    LogicalConversation = 7,
    ModelOutput = 8
}

public enum CustomLoopContextTrustClass
{
    Unknown = 0,
    NonOverridableGovernance = 1,
    TrustedInstruction = 2,
    TrustedMetadata = 3,
    UntrustedData = 4
}

public sealed record CustomLoopConversationReference(
    string ConversationId,
    string CapturedVersion,
    DateTimeOffset CapturedAtUtc);

public sealed record CustomLoopModelSnapshot(
    string Provider,
    string? Model);

public sealed record CustomLoopExecutionClock(
    long AccumulatedRunningMilliseconds,
    DateTimeOffset? ActiveSinceUtc)
{
    public static CustomLoopExecutionClock NotStarted()
    {
        return new CustomLoopExecutionClock(0, null);
    }
}

public sealed record CustomLoopMessageSnapshot(
    LlmMessageRole Role,
    string Content);

public sealed record CustomLoopContextManifestSource(
    int Order,
    CustomLoopContextSource SourceType,
    string SourceId,
    string SourcePath,
    CustomLoopContextProvenance Provenance,
    CustomLoopContextTrustClass TrustClass,
    LlmMessageRole Role,
    string Content,
    string ContentHash,
    int OriginalCharacterCount,
    int UsedCharacterCount,
    bool Truncated,
    string? TruncationReason,
    string? OmissionReason,
    DateTimeOffset CapturedAtUtc)
{
    [JsonIgnore]
    public bool Included => OmissionReason is null;
}

public sealed record CustomLoopContextSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    CustomLoopContextManifestSource[] SourceManifest,
    string ManifestHash)
{
    public const int CurrentSchemaVersion = 1;

    [JsonIgnore]
    public CustomLoopMessageSnapshot[] DirectoryRoleMessages => (SourceManifest ?? [])
            .Where(source => source.Included && source.SourceType is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.ContextualState)
            .Select(source => new CustomLoopMessageSnapshot(source.Role, source.Content))
            .ToArray();

    [JsonIgnore]
    public CustomLoopMessageSnapshot[] InvokingConversationMessages => (SourceManifest ?? [])
            .Where(source => source.Included && source.SourceType == CustomLoopContextSource.InvokingConversation)
            .Select(source => new CustomLoopMessageSnapshot(source.Role, source.Content))
            .ToArray();

    public static CustomLoopContextSnapshot CreateEmpty(DateTimeOffset capturedAtUtc)
    {
        var snapshot = new CustomLoopContextSnapshot(
            CurrentSchemaVersion,
            capturedAtUtc,
            [
                OmittedWorkspaceSource(1, CustomLoopContextSource.RoleInstruction, "nearest-agents", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(2, CustomLoopContextSource.RoleInstruction, "agent", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(3, CustomLoopContextSource.RoleInstruction, "soul", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(4, CustomLoopContextSource.RoleInstruction, "personality", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(5, CustomLoopContextSource.ContextualState, "context", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc),
                OmittedWorkspaceSource(6, CustomLoopContextSource.ContextualState, "memory", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc),
                OmittedWorkspaceSource(7, CustomLoopContextSource.ContextualState, "models", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc)
            ],
            string.Empty);
        return CustomLoopContextSnapshotHash.Apply(snapshot);
    }

    private static CustomLoopContextManifestSource OmittedWorkspaceSource(
        int order,
        CustomLoopContextSource sourceType,
        string sourceId,
        CustomLoopContextProvenance provenance,
        CustomLoopContextTrustClass trustClass,
        LlmMessageRole role,
        DateTimeOffset capturedAtUtc)
    {
        var sourcePath = sourceId switch
        {
            "nearest-agents" => "unavailable/AGENTS.md",
            "agent" => "unavailable/.agent/AGENT.md",
            "soul" => "unavailable/.agent/SOUL.md",
            "personality" => "unavailable/.agent/PERSONALITY.md",
            "context" => "unavailable/.agent/CONTEXT.md",
            "memory" => "unavailable/.agent/MEMORY.md",
            "models" => "unavailable/.agent/models.json",
            _ => $"unavailable/{sourceId}"
        };
        return new CustomLoopContextManifestSource(order, sourceType, sourceId, sourcePath, provenance, trustClass, role, string.Empty, CustomLoopTraceContentHash.Compute(string.Empty), 0, 0, false, null, "Source was not present in this captured context.", capturedAtUtc);
    }
}

public sealed record CustomLoopContextBlock(
    CustomLoopContextSource Source,
    string SourceId,
    LlmMessageRole Role,
    bool Included,
    string? OmissionReason,
    string Content,
    string ContentHash,
    int CharacterCount,
    bool Truncated,
    string? SourceVersion = null);

public sealed record CustomLoopRetainedOutput(
    string StepId,
    int Iteration,
    string Content,
    string ContentHash);

public sealed record CustomLoopRunCheckpoint(
    int Iteration,
    int NextStepIndex,
    int AcceptedRepeatCount,
    bool PendingExitDecision,
    CustomLoopRetainedOutput[] EarlierRetainedOutputs,
    CustomLoopRetainedOutput? PreviousIterationResult,
    CustomLoopRetainedOutput? CurrentIterationResult,
    int ToolRequestsUsed,
    long LastCommittedSequence)
{
    public static CustomLoopRunCheckpoint Start()
    {
        return new CustomLoopRunCheckpoint(1, 0, 0, false, [], null, null, 0, 0);
    }
}

public sealed record CustomLoopRunEvent(
    long Sequence,
    string EventId,
    DateTimeOffset TimestampUtc,
    CustomLoopRunEventKind Kind,
    int? Iteration,
    string? StepId,
    int? Attempt,
    string Detail,
    CustomLoopContextBlock[] ContextBlocks,
    string? CanonicalOutput,
    int? OriginalOutputCharacterCount,
    bool? CanonicalOutputTruncated,
    bool? RetainedForLoopReasoning,
    bool? PublishedToInvokingConversation,
    string? ConversationPublicationId,
    string? Provider,
    string? Model,
    string? ProviderResponseId,
    CustomLoopExitDecision? ExitDecision,
    CustomLoopToolAuthoritySnapshot? ToolAuthority = null,
    CustomLoopToolTraceEvidence? ToolEvidence = null,
    int? TraceReservationUtf8Bytes = null);

public sealed record CustomLoopRunRecord(
    int SchemaVersion,
    string Id,
    string LoopId,
    int LifecycleVersion,
    CustomLoopRunStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Surface,
    CustomLoopModelSnapshot ModelSnapshot,
    string AdmissionOperationId,
    string AdmissionRequestHash,
    CustomLoopDefinition AdmittedDefinition,
    string TriggerPrompt,
    CustomLoopConversationReference? InvokingConversation,
    CustomLoopContextSnapshot ContextSnapshot,
    CustomLoopExecutionClock ExecutionClock,
    CustomLoopRunCheckpoint Checkpoint,
    CustomLoopRunEvent[] Events,
    string? FinalOutput,
    string? FailureCode,
    string? FailureDetail)
{
    public const int CurrentSchemaVersion = 2;

    [JsonIgnore]
    public bool IsTerminal => Status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
}

public sealed record CustomLoopRunSummary(
    string Id,
    string LoopId,
    string AdmissionOperationId,
    int DefinitionVersion,
    CustomLoopRunStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Iteration,
    int NextStepIndex,
    string? FailureCode,
    bool IsDeleted);
