namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

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
