using System.Collections;

namespace EmbodySense.Core.Common.Tests.Authority.Delegation;

internal sealed class ThrowingReadOnlyList<TValue> : IReadOnlyList<TValue>
{
    public TValue this[int index] => throw new InvalidOperationException("Injected index failure.");

    public int Count => throw new InvalidOperationException("Injected count failure.");

    public IEnumerator<TValue> GetEnumerator() => throw new InvalidOperationException("Injected enumeration failure.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
