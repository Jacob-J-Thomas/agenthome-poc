using System.Buffers;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Owns one bounded, verified artifact-byte snapshot rented from the shared pool.
/// </summary>
/// <remarks>
/// The snapshot is valid only after the reader observed identical first and verification passes over the same open file
/// object. Disposing it clears and returns the rented bytes.
/// </remarks>
internal sealed class LoopArtifactStableSnapshot : IDisposable
{
    private byte[]? _buffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopArtifactStableSnapshot"/> class.
    /// </summary>
    /// <param name="buffer">The rented artifact bytes.</param>
    /// <param name="length">The validated artifact byte count.</param>
    public LoopArtifactStableSnapshot(byte[] buffer, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (length < 1 || length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _buffer = buffer;
        Length = length;
    }

    /// <summary>
    /// Gets the validated byte count.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// Gets the verified artifact bytes.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The snapshot has already been disposed.</exception>
    public ReadOnlyMemory<byte> Content => (_buffer ?? throw new ObjectDisposedException(nameof(LoopArtifactStableSnapshot))).AsMemory(0, Length);

    /// <inheritdoc />
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
