using System.Collections;

namespace EmbodySense.Core.Common.Tests.Inference.Profiles;

internal sealed class HostileReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _values;

    internal HostileReadOnlyList(IReadOnlyList<T> values, int reportedCount)
    {
        _values = values;
        Count = reportedCount;
    }

    public int Count { get; }
    public T this[int index] => _values[index];
    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
