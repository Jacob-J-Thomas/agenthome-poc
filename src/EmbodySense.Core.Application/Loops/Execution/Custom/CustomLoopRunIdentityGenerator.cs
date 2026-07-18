using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopRunIdentityGenerator : ICustomLoopRunIdentityGenerator
{
    public string NewRunId() => "run-" + Guid.NewGuid().ToString("N");

    public string NewEventId() => "event-" + Guid.NewGuid().ToString("N");
}
