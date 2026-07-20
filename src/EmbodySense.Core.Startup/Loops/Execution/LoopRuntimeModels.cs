using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunInvocationInput(
    string LoopId,
    int ExpectedDefinitionVersion,
    string ExpectedDefinitionHash,
    string OperationId,
    string? InvocationPrompt);

public sealed record LoopRunInvocationResponse(
    string AdmissionStatus,
    string? ExecutionStatus,
    bool WasDispatched,
    LoopRunSnapshot? Run,
    IReadOnlyList<LoopValidationError> ValidationErrors,
    string Detail);

public sealed record LoopRunControlInput(
    string RunId,
    int ExpectedLifecycleVersion,
    string OperationId);

public sealed record LoopRunControlResponse(
    string Status,
    LoopRunSnapshot? Run,
    string OperationId,
    string Detail);

public sealed record LoopRunSummarySnapshot(
    string Id,
    string LoopId,
    string AdmissionOperationId,
    int DefinitionVersion,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Iteration,
    int NextStepIndex,
    string? FailureCode,
    bool IsDeleted);

public sealed record LoopRunSnapshot(
    int SchemaVersion,
    string Id,
    string LoopId,
    int LifecycleVersion,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Surface,
    LoopRunModelSnapshot Model,
    string AdmissionOperationId,
    string AdmissionActor,
    string AdmissionRequestHash,
    LoopDefinitionSnapshot AdmittedDefinition,
    string TriggerPrompt,
    LoopRunConversationReference? InvokingConversation,
    LoopRunContextSnapshot Context,
    LoopRunExecutionClockSnapshot ExecutionClock,
    LoopRunCheckpointSnapshot Checkpoint,
    IReadOnlyList<LoopRunEventSnapshot> Events,
    string? FinalOutput,
    string? FailureCode,
    string? FailureDetail);

public sealed record LoopRunModelSnapshot(string Provider, string? Model);

public sealed record LoopRunConversationReference(
    string ConversationId,
    string CapturedVersion,
    DateTimeOffset CapturedAtUtc);

public sealed record LoopRunContextSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    string ManifestHash,
    IReadOnlyList<LoopRunContextManifestSourceSnapshot> SourceManifest,
    IReadOnlyList<LoopRunMessageSnapshot> DirectoryRoleMessages,
    IReadOnlyList<LoopRunMessageSnapshot> InvokingConversationMessages);

public sealed record LoopRunContextManifestSourceSnapshot(
    int Order,
    string SourceType,
    string SourceId,
    string SourcePath,
    string Provenance,
    string TrustClass,
    string Role,
    string Content,
    string ContentHash,
    int OriginalCharacterCount,
    int UsedCharacterCount,
    bool Truncated,
    string? TruncationReason,
    string? OmissionReason,
    DateTimeOffset CapturedAtUtc);

public sealed record LoopRunMessageSnapshot(string Role, string Content);

public sealed record LoopRunExecutionClockSnapshot(
    long AccumulatedRunningMilliseconds,
    DateTimeOffset? ActiveSinceUtc);

public sealed record LoopRunCheckpointSnapshot(
    int Iteration,
    int NextStepIndex,
    int AcceptedRepeatCount,
    bool PendingExitDecision,
    IReadOnlyList<LoopRunRetainedOutputSnapshot> EarlierRetainedOutputs,
    LoopRunRetainedOutputSnapshot? PreviousIterationResult,
    LoopRunRetainedOutputSnapshot? CurrentIterationResult,
    int ToolRequestsUsed,
    long LastCommittedSequence);

public sealed record LoopRunRetainedOutputSnapshot(
    string StepId,
    int Iteration,
    string Content,
    string ContentHash);

public sealed record LoopRunEventSnapshot(
    long Sequence,
    string EventId,
    DateTimeOffset TimestampUtc,
    string Kind,
    int? Iteration,
    string? StepId,
    int? Attempt,
    string Detail,
    IReadOnlyList<LoopRunContextBlockSnapshot> ContextBlocks,
    string? CanonicalOutput,
    int? OriginalOutputCharacterCount,
    bool? CanonicalOutputTruncated,
    bool? RetainedForLoopReasoning,
    bool? PublishedToInvokingConversation,
    string? ConversationPublicationId,
    string? Provider,
    string? Model,
    string? ProviderResponseId,
    string? ExitDecision,
    LoopRunToolAuthoritySnapshot? ToolAuthority,
    LoopRunToolEvidenceSnapshot? ToolEvidence);

public sealed record LoopRunToolAuthoritySnapshot(
    string RoleId,
    IReadOnlyList<string> AdmittedMaximum,
    IReadOnlyList<string> CurrentRoleCeiling,
    IReadOnlyList<string> ImplementedCatalog,
    IReadOnlyList<string> EffectiveAssignments,
    string RoleCeilingHash,
    string CatalogHash,
    DateTimeOffset EvaluatedAtUtc,
    bool IsValid,
    string Detail);

public sealed record LoopRunToolEvidenceSnapshot(
    string Phase,
    int RequestOrdinal,
    string RequestCorrelationId,
    string? BrokerRequestId,
    string Command,
    string TargetPath,
    string? Content,
    string? Pattern,
    string? ResolvedTarget,
    LoopRunToolAuthoritySnapshot Authority,
    LoopRunToolGovernanceSnapshot? Governance,
    string? Outcome,
    string? CanonicalResultReturnedToModel,
    string? CanonicalResultHash,
    int? CanonicalResultCharacterCount,
    bool ReturnedToModel,
    int ReservedUtf8Bytes);

public sealed record LoopRunToolGovernanceSnapshot(
    string AuthorityDecision,
    string AuthorityDetail,
    string? PermissionDecision,
    string? PermissionMatchedPath,
    string? PermissionDetail,
    string? PermissionPolicyHash,
    string ApprovalDecision,
    string? ApprovalDecisionBy,
    string? ApprovalDetail);

public sealed record LoopRunContextBlockSnapshot(
    string Source,
    string SourceId,
    string Role,
    bool Included,
    string? OmissionReason,
    string Content,
    string ContentHash,
    int CharacterCount,
    bool Truncated,
    string? SourceVersion);
