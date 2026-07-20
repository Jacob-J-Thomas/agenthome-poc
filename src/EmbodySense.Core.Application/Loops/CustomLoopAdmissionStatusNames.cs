namespace EmbodySense.Core.Application.Loops;

public static class CustomLoopAdmissionStatusNames
{
    public const string Unknown = nameof(Execution.Custom.CustomLoopAdmissionStatus.Unknown);
    public const string Admitted = nameof(Execution.Custom.CustomLoopAdmissionStatus.Admitted);
    public const string Replayed = nameof(Execution.Custom.CustomLoopAdmissionStatus.Replayed);
    public const string Invalid = nameof(Execution.Custom.CustomLoopAdmissionStatus.Invalid);
    public const string Conflict = nameof(Execution.Custom.CustomLoopAdmissionStatus.Conflict);
    public const string NonterminalRunExists = nameof(Execution.Custom.CustomLoopAdmissionStatus.NonterminalRunExists);
    public const string LimitExceeded = nameof(Execution.Custom.CustomLoopAdmissionStatus.LimitExceeded);
    public const string NotFound = nameof(Execution.Custom.CustomLoopAdmissionStatus.NotFound);
    public const string AuditUnavailable = nameof(Execution.Custom.CustomLoopAdmissionStatus.AuditUnavailable);
}
