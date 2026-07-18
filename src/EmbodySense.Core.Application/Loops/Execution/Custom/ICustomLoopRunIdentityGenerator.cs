using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopRunIdentityGenerator
{
    string NewRunId();

    string NewEventId();
}
