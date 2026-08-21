using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Posture;

internal sealed class StubOperationalSchedulePort : IScheduleOperationalPosturePort
{
    internal GovernedLoopScheduleEvidenceReadResult Result { get; set; } = new(GovernedLoopOperationalEvidenceReadStatus.Empty, 0, false, null, []);

    public Task<GovernedLoopScheduleEvidenceReadResult> ReadAsync(GovernedLoopOperationalEvidencePageRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(Result);
}
