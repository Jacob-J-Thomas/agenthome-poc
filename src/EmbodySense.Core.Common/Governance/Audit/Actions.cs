namespace EmbodySense.Core.Common.Governance.Audit;

/// <summary>
/// Defines the versioned audit schema and its canonical vocabulary.
/// </summary>
public static partial class AuditSchema
{
    /// <summary>
    /// Defines canonical audit action identifiers.
    /// </summary>
    public static class Actions
    {
        /// <summary>
        /// Identifies the workspace init audit action.
        /// </summary>
        public const string WorkspaceInit = "workspace.init";

        /// <summary>
        /// Identifies the LLM inference start audit action.
        /// </summary>
        public const string LlmInferenceStart = "llm.inference.start";

        /// <summary>
        /// Identifies the LLM inference complete audit action.
        /// </summary>
        public const string LlmInferenceComplete = "llm.inference.complete";

        /// <summary>
        /// Identifies the LLM app server request audit action.
        /// </summary>
        public const string LlmAppServerRequest = "llm.appserver.request";

        /// <summary>
        /// Identifies the loop definition mutation intent audit action.
        /// </summary>
        public const string LoopDefinitionMutationIntent = "loop.definition.mutation.intent";

        /// <summary>
        /// Identifies the loop definition mutation outcome audit action.
        /// </summary>
        public const string LoopDefinitionMutationOutcome = "loop.definition.mutation.outcome";

        /// <summary>
        /// Identifies the loop run admission audit action.
        /// </summary>
        public const string LoopRunAdmission = "loop.run.admission";

        /// <summary>
        /// Identifies the loop run lifecycle audit action.
        /// </summary>
        public const string LoopRunLifecycle = "loop.run.lifecycle";

        /// <summary>
        /// Identifies the loop invocation receipt retention intent audit action.
        /// </summary>
        public const string LoopInvocationReceiptRetentionIntent = "loop.invocation_receipt.retention.intent";

        /// <summary>
        /// Identifies the loop invocation receipt retention outcome audit action.
        /// </summary>
        public const string LoopInvocationReceiptRetentionOutcome = "loop.invocation_receipt.retention.outcome";

        /// <summary>
        /// Identifies the lifecycle-control receipt retention intent audit action.
        /// </summary>
        public const string LoopControlReceiptRetentionIntent = "loop.control_receipt.retention.intent";

        /// <summary>
        /// Identifies the lifecycle-control receipt retention outcome audit action.
        /// </summary>
        public const string LoopControlReceiptRetentionOutcome = "loop.control_receipt.retention.outcome";

        /// <summary>
        /// Identifies a governed definition authoring-receipt cleanup intent.
        /// </summary>
        public const string LoopDefinitionReceiptRetentionIntent = "loop.definition_receipt.retention.intent";

        /// <summary>
        /// Identifies a governed definition authoring-receipt cleanup outcome.
        /// </summary>
        public const string LoopDefinitionReceiptRetentionOutcome = "loop.definition_receipt.retention.outcome";

        /// <summary>
        /// Identifies the loop trace deletion intent audit action.
        /// </summary>
        public const string LoopTraceDeletionIntent = "loop.trace.deletion.intent";

        /// <summary>
        /// Identifies the loop trace deletion outcome audit action.
        /// </summary>
        public const string LoopTraceDeletionOutcome = "loop.trace.deletion.outcome";

        /// <summary>
        /// Identifies the loop node attempt audit action.
        /// </summary>
        public const string LoopNodeAttempt = "loop.node.attempt";

        /// <summary>
        /// Identifies the loop exit decision audit action.
        /// </summary>
        public const string LoopExitDecision = "loop.exit.decision";

        /// <summary>
        /// Identifies the tool permission evaluate audit action.
        /// </summary>
        public const string ToolPermissionEvaluate = "tool.permission.evaluate";

        /// <summary>
        /// Identifies the tool loop authority evaluate audit action.
        /// </summary>
        public const string ToolLoopAuthorityEvaluate = "tool.loop_authority.evaluate";

        /// <summary>
        /// Identifies the tool approval request audit action.
        /// </summary>
        public const string ToolApprovalRequest = "tool.approval.request";

        /// <summary>
        /// Identifies the tool approval decision audit action.
        /// </summary>
        public const string ToolApprovalDecision = "tool.approval.decision";

        /// <summary>
        /// Identifies the tool execution intent audit action.
        /// </summary>
        public const string ToolExecutionIntent = "tool.execution.intent";

        /// <summary>
        /// Identifies the tool response retain audit action.
        /// </summary>
        public const string ToolResponseRetain = "tool.response.retain";

        /// <summary>
        /// Identifies the tool execute audit action.
        /// </summary>
        public const string ToolExecute = "tool.execute";

        /// <summary>
        /// Identifies a capability artifact intake outcome.
        /// </summary>
        public const string CapabilityArtifactIntake = "capability.artifact.intake";

        /// <summary>Identifies a capability artifact verification outcome.</summary>
        public const string CapabilityArtifactVerification = "capability.artifact.verification";

        /// <summary>Identifies a capability artifact activation or rollback outcome.</summary>
        public const string CapabilityArtifactActivation = "capability.artifact.activation";

        /// <summary>Identifies capability lifecycle mutation intent.</summary>
        public const string CapabilityLifecycleIntent = "capability.lifecycle.intent";

        /// <summary>Identifies deterministic capability lifecycle impact preview evidence.</summary>
        public const string CapabilityLifecyclePreview = "capability.lifecycle.preview";

        /// <summary>Identifies a capability lifecycle conflict.</summary>
        public const string CapabilityLifecycleConflict = "capability.lifecycle.conflict";

        /// <summary>Identifies an atomic capability lifecycle mutation.</summary>
        public const string CapabilityLifecycleMutation = "capability.lifecycle.mutation";

        /// <summary>Identifies an atomic capability rollback.</summary>
        public const string CapabilityLifecycleRollback = "capability.lifecycle.rollback";

        /// <summary>Identifies the final durable lifecycle outcome.</summary>
        public const string CapabilityLifecycleFinal = "capability.lifecycle.final";

        /// <summary>
        /// Identifies a capability executable invocation intent or outcome.
        /// </summary>
        public const string CapabilityExecutableInvocation = "capability.executable.invocation";
    }
}
