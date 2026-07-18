using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopControlOperation(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    CustomLoopControlKind Kind,
    string RunId,
    int ExpectedLifecycleVersion,
    string Actor,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopControlOperationState State,
    CustomLoopControlStatus Outcome,
    int? ResultLifecycleVersion,
    CustomLoopRunStatus? ResultRunStatus,
    bool OutcomeAuditRecorded,
    string Detail)
{
    public const int CurrentSchemaVersion = 1;
}
