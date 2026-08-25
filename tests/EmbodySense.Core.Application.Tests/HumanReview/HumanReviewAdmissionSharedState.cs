using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewAdmissionSharedState(CustomLoopRunRecord run, bool gateInitialReads = false)
{
    public object Gate { get; } = new();
    public CustomLoopRunRecord? Run { get; set; } = run;
    public int UpdateCount { get; set; }
    public bool GateInitialReads { get; set; } = gateInitialReads;
    public int InitialReadCount { get; set; }
    public TaskCompletionSource InitialReadsReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
