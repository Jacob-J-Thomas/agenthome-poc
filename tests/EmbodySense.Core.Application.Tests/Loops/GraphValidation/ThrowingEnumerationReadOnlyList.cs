using System.Collections;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

internal sealed class ThrowingEnumerationReadOnlyList<T>(int count) : IReadOnlyList<T>
{
    public int Count { get; } = count;

    public T this[int index] => throw new InvalidOperationException($"Index {index} must not be read.");

    public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("An oversized provider collection must not be enumerated.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
