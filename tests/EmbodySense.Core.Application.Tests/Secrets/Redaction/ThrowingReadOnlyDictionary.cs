using System.Collections;

namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

internal sealed class ThrowingReadOnlyDictionary : IReadOnlyDictionary<string, object?>
{
    public int Count => 1;

    public IEnumerable<string> Keys => ["unreadable"];

    public IEnumerable<object?> Values => ["unreadable"];

    public object? this[string key] => throw new KeyNotFoundException();

    public bool ContainsKey(string key)
    {
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        throw new InvalidOperationException("Hostile enumerator.");
    }

    public bool TryGetValue(string key, out object? value)
    {
        value = null;
        return false;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
