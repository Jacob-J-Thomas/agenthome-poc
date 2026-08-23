using System.Buffers;
using System.Security.Cryptography;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Reads a bounded artifact only after two matching passes over the same shared file handle.
/// </summary>
/// <remarks>
/// A repository-owned atomic replacement leaves an existing handle bound to its old file object, so both passes retain
/// that valid snapshot. A non-cooperating in-place writer instead changes the handle's bytes or length; only that proven
/// change is retried, with all malformed, authorization, cancellation, and other I/O failures remaining fail-closed.
/// The paired passes use direct handle reads so a <see cref="FileStream"/> read buffer cannot hide a same-length rewrite.
/// </remarks>
internal static class LoopArtifactStableSnapshotReader
{
    /// <summary>
    /// Reads one stable bounded snapshot.
    /// </summary>
    /// <param name="stream">The already-open shared artifact stream.</param>
    /// <param name="maximumBytes">The inclusive maximum artifact length.</param>
    /// <param name="label">The artifact label used in validation errors.</param>
    /// <param name="path">The already-validated artifact path used only in internal validation errors.</param>
    /// <param name="maximumAttempts">The positive bounded number of matching-pass attempts.</param>
    /// <param name="retryDelay">The delay after a proven in-place mutation.</param>
    /// <param name="afterFirstSnapshot">An optional lifecycle observer run after each first pass and before verification.</param>
    /// <param name="cancellationToken">The token used to cancel reading, observing, or retry delay.</param>
    /// <returns>An owned stable snapshot that must be disposed after consuming its bytes.</returns>
    /// <exception cref="IOException">The artifact changed throughout all bounded verification attempts.</exception>
    public static async Task<LoopArtifactStableSnapshot> ReadAsync(
        FileStream stream,
        int maximumBytes,
        string label,
        string path,
        int maximumAttempts,
        TimeSpan retryDelay,
        Func<CancellationToken, ValueTask>? afterFirstSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            byte[]? buffer = null;
            try
            {
                var length = checked((int)ReadValidatedLength(stream, maximumBytes, label, path));
                buffer = ArrayPool<byte>.Shared.Rent(length);
                await ReadExactPassAsync(stream, buffer.AsMemory(0, length), length, cancellationToken).ConfigureAwait(false);
                var firstHash = SHA256.HashData(buffer.AsSpan(0, length));
                if (afterFirstSnapshot is not null)
                {
                    await afterFirstSnapshot(cancellationToken).ConfigureAwait(false);
                }

                await ReadExactPassAsync(stream, buffer.AsMemory(0, length), length, cancellationToken).ConfigureAwait(false);
                var verificationHash = SHA256.HashData(buffer.AsSpan(0, length));
                if (!CryptographicOperations.FixedTimeEquals(firstHash, verificationHash))
                {
                    throw new LoopArtifactSnapshotChangedException();
                }

                return new LoopArtifactStableSnapshot(buffer, length);
            }
            catch (LoopArtifactSnapshotChangedException) when (attempt + 1 < maximumAttempts)
            {
                if (buffer is not null)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (buffer is not null)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                throw;
            }
        }

        throw new LoopArtifactSnapshotChangedException();
    }

    private static long ReadValidatedLength(FileStream stream, int maximumBytes, string label, string path)
    {
        var length = stream.Length;
        if (length <= 0 || length > maximumBytes)
        {
            throw new FormatException($"{label} `{path}` must contain between 1 and {maximumBytes} UTF-8 bytes.");
        }

        return length;
    }

    private static async Task ReadExactPassAsync(FileStream stream, Memory<byte> destination, long expectedLength, CancellationToken cancellationToken)
    {
        if (stream.Length != expectedLength)
        {
            throw new LoopArtifactSnapshotChangedException();
        }

        try
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                var read = await RandomAccess.ReadAsync(stream.SafeFileHandle, destination[offset..], offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }
        }
        catch (EndOfStreamException)
        {
            throw new LoopArtifactSnapshotChangedException();
        }

        if (stream.Length != expectedLength)
        {
            throw new LoopArtifactSnapshotChangedException();
        }
    }
}
