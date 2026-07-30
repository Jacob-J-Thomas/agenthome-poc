using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop run conflict.
/// </summary>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="ExpectedLifecycleVersion">The expected lifecycle version.</param>
/// <param name="ActualLifecycleVersion">The actual lifecycle version.</param>
/// <param name="ActualStatus">The actual status.</param>
/// <param name="ActualUpdatedAtUtc">The actual updated at UTC.</param>
public sealed record CustomLoopRunConflict(
    string RunId,
    int ExpectedLifecycleVersion,
    int ActualLifecycleVersion,
    CustomLoopRunStatus ActualStatus,
    DateTimeOffset ActualUpdatedAtUtc);
