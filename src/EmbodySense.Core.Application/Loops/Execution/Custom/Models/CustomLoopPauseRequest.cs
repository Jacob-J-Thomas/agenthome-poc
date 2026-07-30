using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop pause request.
/// </summary>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="ExpectedLifecycleVersion">The expected lifecycle version.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="Actor">The actor.</param>
public sealed record CustomLoopPauseRequest(string RunId, int ExpectedLifecycleVersion, string OperationId, string Actor);
