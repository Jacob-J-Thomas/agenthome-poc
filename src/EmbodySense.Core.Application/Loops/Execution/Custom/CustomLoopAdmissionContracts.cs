using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopAdmissionRequest(
    string LoopId,
    int ExpectedDefinitionVersion,
    string ExpectedDefinitionHash,
    string OperationId,
    string Actor,
    string Surface,
    string CurrentRoleId,
    string? InvocationPrompt,
    CustomLoopModelSnapshot ModelSnapshot,
    CustomLoopConversationReference? InvokingConversation,
    CustomLoopContextSnapshot ContextSnapshot);

public enum CustomLoopAdmissionStatus
{
    Unknown = 0,
    Admitted = 1,
    Replayed = 2,
    Invalid = 3,
    Conflict = 4,
    NonterminalRunExists = 5,
    LimitExceeded = 6,
    NotFound = 7,
    AuditUnavailable = 8
}

public sealed record CustomLoopAdmissionResult(
    CustomLoopAdmissionStatus Status,
    CustomLoopRunRecord? Run,
    IReadOnlyList<CustomLoopValidationError> ValidationErrors,
    string Detail)
{
    public bool IsAdmitted => Status is CustomLoopAdmissionStatus.Admitted or CustomLoopAdmissionStatus.Replayed;

    public static CustomLoopAdmissionResult Invalid(IReadOnlyList<CustomLoopValidationError> errors) => new(CustomLoopAdmissionStatus.Invalid, null, errors, "The custom-loop invocation is invalid.");
}

public interface ICustomLoopRunIdentityGenerator
{
    string NewRunId();

    string NewEventId();
}

public sealed class CustomLoopRunIdentityGenerator : ICustomLoopRunIdentityGenerator
{
    public string NewRunId() => "run-" + Guid.NewGuid().ToString("N");

    public string NewEventId() => "event-" + Guid.NewGuid().ToString("N");
}
