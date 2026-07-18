using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

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
