using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.Authority.Delegation;

internal sealed class AuthorityDelegationCanonicalHashWriter : IDisposable
{
    private readonly MemoryStream _stream = new();

    internal AuthorityDelegationCanonicalHashWriter(string domain)
    {
        Append(domain);
    }

    internal void Append(string? value)
    {
        if (value is null)
        {
            Append(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        Append(bytes.Length);
        _stream.Write(bytes);
    }

    internal void Append(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    internal void Append(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    internal void Append(bool value) => _stream.WriteByte(value ? (byte)1 : (byte)0);

    internal void Append(DateTimeOffset value) => Append(value.UtcTicks);

    internal void Append(DateTimeOffset? value)
    {
        Append(value.HasValue);
        if (value is { } timestamp)
        {
            Append(timestamp);
        }
    }

    internal string Digest() => Convert.ToHexString(SHA256.HashData(_stream.GetBuffer().AsSpan(0, checked((int)_stream.Length)))).ToLowerInvariant();

    public void Dispose() => _stream.Dispose();
}
