using System.Collections;

namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

internal sealed class ThrowingDictionary : Hashtable
{
    public override IDictionaryEnumerator GetEnumerator()
    {
        throw new InvalidOperationException("Hostile dictionary enumerator.");
    }
}
