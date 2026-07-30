namespace EmbodySense.Core.Common.Governance.Audit;

public static partial class AuditSchema
{
    public static class Actions
    {
        public const string WorkspaceInit = "workspace.init";

        public const string LlmInferenceStart = "llm.inference.start";

        public const string LlmInferenceComplete = "llm.inference.complete";

        public const string LlmAppServerRequest = "llm.appserver.request";

        public const string LoopDefinitionMutationIntent = "loop.definition.mutation.intent";

        public const string LoopDefinitionMutationOutcome = "loop.definition.mutation.outcome";

        public const string LoopRunAdmission = "loop.run.admission";

        public const string LoopRunLifecycle = "loop.run.lifecycle";

        public const string LoopInvocationReceiptRetentionIntent = "loop.invocation_receipt.retention.intent";

        public const string LoopInvocationReceiptRetentionOutcome = "loop.invocation_receipt.retention.outcome";

        public const string LoopTraceDeletionIntent = "loop.trace.deletion.intent";

        public const string LoopTraceDeletionOutcome = "loop.trace.deletion.outcome";

        public const string LoopNodeAttempt = "loop.node.attempt";

        public const string LoopExitDecision = "loop.exit.decision";

        public const string ToolPermissionEvaluate = "tool.permission.evaluate";

        public const string ToolLoopAuthorityEvaluate = "tool.loop_authority.evaluate";

        public const string ToolApprovalRequest = "tool.approval.request";

        public const string ToolApprovalDecision = "tool.approval.decision";

        public const string ToolExecutionIntent = "tool.execution.intent";

        public const string ToolResponseRetain = "tool.response.retain";

        public const string ToolExecute = "tool.execute";
    }
}
