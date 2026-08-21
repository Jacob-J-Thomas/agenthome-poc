using System.Collections;

namespace EmbodySense.Core.Common.Tests.Authority.Delegation;

internal sealed class LyingCountReadOnlyList<TValue>(IReadOnlyList<TValue> values, int declaredCount) : IReadOnlyList<TValue>
{
    public TValue this[int index] => values[index];

    public int Count => declaredCount;

    public IEnumerator<TValue> GetEnumerator() => values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
