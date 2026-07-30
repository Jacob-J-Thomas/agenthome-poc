using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

public sealed record CustomLoopResumeExecutionRequest(string RunId, int RunningLifecycleVersion, string ResumeOperationId, string Actor, bool ActiveRunAlreadyRegistered = false);
