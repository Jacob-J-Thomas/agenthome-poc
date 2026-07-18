namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopCreateOperationLookupStatus
{
    Unknown = 0,
    NotFound = 1,
    PendingDefinitionCommit = 2,
    Committed = 3
}
