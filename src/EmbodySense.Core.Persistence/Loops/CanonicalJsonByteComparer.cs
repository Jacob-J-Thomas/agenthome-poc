using System.Buffers;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class CanonicalJsonByteComparer(ReadOnlyMemory<byte> expected) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _offset;
    private int _firstDifference = -1;

    public bool IsEqual => _firstDifference < 0 && _offset == expected.Length;

    public int FirstDifference => _firstDifference < 0 ? Math.Min(_offset, expected.Length) : _firstDifference;

    public int Length => _offset;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var remaining = Math.Max(0, expected.Length - _offset);
        var shared = Math.Min(count, remaining);
        if (_firstDifference < 0)
        {
            var difference = _buffer.AsSpan(0, shared).CommonPrefixLength(expected.Span.Slice(_offset, shared));
            if (difference != shared || count > remaining)
            {
                _firstDifference = _offset + difference;
            }
        }

        _offset += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer;
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (sizeHint <= _buffer.Length)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(sizeHint);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = replacement;
    }
}
