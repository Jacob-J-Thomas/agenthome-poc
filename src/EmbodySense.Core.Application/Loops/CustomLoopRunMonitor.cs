using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopRunMonitor(CustomLoopRunSummary Summary, string ArtifactHash);
