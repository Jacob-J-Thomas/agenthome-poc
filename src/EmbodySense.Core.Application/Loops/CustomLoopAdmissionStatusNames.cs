using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
namespace EmbodySense.Core.Application.Loops;

public static class CustomLoopAdmissionStatusNames
{
    public const string Unknown = nameof(CustomLoopAdmissionStatus.Unknown);
    public const string Admitted = nameof(CustomLoopAdmissionStatus.Admitted);
    public const string Replayed = nameof(CustomLoopAdmissionStatus.Replayed);
    public const string Invalid = nameof(CustomLoopAdmissionStatus.Invalid);
    public const string Conflict = nameof(CustomLoopAdmissionStatus.Conflict);
    public const string NonterminalRunExists = nameof(CustomLoopAdmissionStatus.NonterminalRunExists);
    public const string LimitExceeded = nameof(CustomLoopAdmissionStatus.LimitExceeded);
    public const string NotFound = nameof(CustomLoopAdmissionStatus.NotFound);
    public const string AuditUnavailable = nameof(CustomLoopAdmissionStatus.AuditUnavailable);
    public const string ReceiptUnavailable = nameof(CustomLoopAdmissionStatus.ReceiptUnavailable);
}
