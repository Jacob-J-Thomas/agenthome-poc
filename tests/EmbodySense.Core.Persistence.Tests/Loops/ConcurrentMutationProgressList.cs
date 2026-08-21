using System.Collections;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class ConcurrentMutationProgressList(
    IReadOnlyList<GovernedLoopOperationalControlProgress> items) : IReadOnlyList<GovernedLoopOperationalControlProgress>
{
    public int Count => items.Count;

    public GovernedLoopOperationalControlProgress this[int index] => items[index];

    public IEnumerator<GovernedLoopOperationalControlProgress> GetEnumerator()
        => throw new InvalidOperationException("Collection was modified during capture.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
