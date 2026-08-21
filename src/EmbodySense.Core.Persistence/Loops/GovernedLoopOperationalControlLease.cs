using EmbodySense.Core.Application.Loops.Posture;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class GovernedLoopOperationalControlLease(FileStream stream) : IGovernedLoopOperationalControlLease
{
    private FileStream? _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
