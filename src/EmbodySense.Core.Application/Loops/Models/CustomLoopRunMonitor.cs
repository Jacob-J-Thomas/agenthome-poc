using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop run monitor.
/// </summary>
/// <param name="Summary">The summary.</param>
/// <param name="ArtifactHash">The artifact hash.</param>
public sealed record CustomLoopRunMonitor(CustomLoopRunSummary Summary, string ArtifactHash);
