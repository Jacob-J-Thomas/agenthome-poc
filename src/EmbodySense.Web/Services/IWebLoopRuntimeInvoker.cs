using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Web.Services;

public interface IWebLoopRuntimeInvoker
{
    Task<LoopRunInvocationResponse> InvokeLoopAsync(LoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default);

    Task<LoopRunControlResponse> ResumeLoopAsync(LoopRunControlInput input, string ownerConnectionId, CancellationToken cancellationToken = default);
}
