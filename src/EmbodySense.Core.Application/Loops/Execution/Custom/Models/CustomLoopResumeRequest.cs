using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

public sealed record CustomLoopResumeRequest(string RunId, int ExpectedLifecycleVersion, string OperationId, string Actor);
