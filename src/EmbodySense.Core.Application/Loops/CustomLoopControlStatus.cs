using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopControlStatus
{
    Unknown = 0,
    PauseRequested = 1,
    Paused = 2,
    CancelRequested = 3,
    Cancelled = 4,
    Resumed = 5,
    Completed = 6,
    Failed = 7,
    NeedsReview = 8,
    Replayed = 9,
    Conflict = 10,
    InvalidState = 11,
    NotFound = 12,
    AuditWarning = 13,
    WorkspaceExecutionBusy = 14,
    OperationInProgress = 15,
    WorkspaceHostUnavailable = 16
}
