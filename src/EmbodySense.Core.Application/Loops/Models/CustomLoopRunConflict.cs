using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Models;

public sealed record CustomLoopRunConflict(
    string RunId,
    int ExpectedLifecycleVersion,
    int ActualLifecycleVersion,
    CustomLoopRunStatus ActualStatus,
    DateTimeOffset ActualUpdatedAtUtc);
