using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop resume execution request.
/// </summary>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="RunningLifecycleVersion">The running lifecycle version.</param>
/// <param name="ResumeOperationId">The resume operation ID.</param>
/// <param name="Actor">The actor.</param>
/// <param name="ActiveRunAlreadyRegistered">The active run already registered.</param>
public sealed record CustomLoopResumeExecutionRequest(string RunId, int RunningLifecycleVersion, string ResumeOperationId, string Actor, bool ActiveRunAlreadyRegistered = false);
