using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopModelAvailability
{
    Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default);
}
