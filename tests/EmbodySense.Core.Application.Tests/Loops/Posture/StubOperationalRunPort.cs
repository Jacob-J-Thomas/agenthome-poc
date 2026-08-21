using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Posture;

internal sealed class StubOperationalRunPort : IGovernedLoopRunOperationalPosturePort
{
    internal GovernedLoopRunEvidenceReadResult Result { get; set; } = new(GovernedLoopOperationalEvidenceReadStatus.Empty, false, null, []);

    public Task<GovernedLoopRunEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result);
}
