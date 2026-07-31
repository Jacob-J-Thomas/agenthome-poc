using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class NullToolEvidenceSink : ICustomLoopToolEvidenceSink
{
    /// <summary>
    /// Accepts evidence without persistence for explicitly isolated test compositions.
    /// </summary>
    /// <param name="runId">The ignored run identity.</param>
    /// <param name="iteration">The ignored iteration.</param>
    /// <param name="stepId">The ignored step identity.</param>
    /// <param name="attempt">The ignored attempt number.</param>
    /// <param name="evidence">The ignored evidence.</param>
    /// <param name="cancellationToken">The ignored cancellation token.</param>
    /// <returns>An already-completed task.</returns>
    public Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
