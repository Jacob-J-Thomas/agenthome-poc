using System.Collections;

namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

internal sealed class ThrowingSequence : IEnumerable
{
    public IEnumerator GetEnumerator()
    {
        throw new InvalidOperationException("Hostile sequence enumerator.");
    }
}
