using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EmbodySense.Core.Common.Secrets;

/// <summary>
/// Owns a bounded, temporary copy of character-based secret material and clears that memory when disposed.
/// </summary>
/// <remarks>
/// This type deliberately exposes no public plaintext accessor. Consumers pass an instance to other
/// secret-aware primitives, such as a per-use redaction scope, rather than converting it back to a string.
/// Disposal must not race with a consuming operation.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class EphemeralSecretMaterial : IDisposable
{
    /// <summary>
    /// Maximum character count accepted by a single owned buffer.
    /// </summary>
    public const int MaxCharacters = 4_096;

    private const string ProjectionMarker = "[ephemeral-secret-material]";
    private readonly string _safeProjectionMarker;
    private readonly object _sync = new();

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private char[]? _characters;

    private EphemeralSecretMaterial(char[] characters)
    {
        _characters = characters;
        _safeProjectionMarker = ProjectionMarker.AsSpan().Contains(characters, StringComparison.Ordinal) ? "" : ProjectionMarker;
    }

    /// <summary>
    /// Gets the number of owned characters without exposing their value.
    /// </summary>
    public int Length
    {
        get
        {
            lock (_sync)
            {
                return _characters?.Length ?? 0;
            }
        }
    }

    /// <summary>
    /// Gets whether the owned memory has been cleared and released by this instance.
    /// </summary>
    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _characters is null;
            }
        }
    }

    private string DebuggerDisplay => GetSafeProjectionMarker();

    /// <summary>
    /// Creates an owned temporary copy of the supplied characters.
    /// </summary>
    /// <param name="value">The secret characters to copy. Empty material is allowed and is ignored by redaction scopes.</param>
    /// <returns>A disposable owner whose memory is independent of the caller's buffer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> exceeds <see cref="MaxCharacters"/>.</exception>
    public static EphemeralSecretMaterial Create(ReadOnlySpan<char> value)
    {
        if (value.Length > MaxCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Secret material exceeds the {MaxCharacters}-character ownership limit.");
        }

        return new EphemeralSecretMaterial(value.ToArray());
    }

    /// <summary>
    /// Transfers exclusive ownership of an existing bounded character array to a temporary secret owner.
    /// </summary>
    /// <param name="value">The array to own and clear. The caller must not access it again until observing disposal for verification.</param>
    /// <returns>A disposable owner over the exact supplied array.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> exceeds <see cref="MaxCharacters"/>.</exception>
    public static EphemeralSecretMaterial TakeOwnership(char[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Secret material exceeds the {MaxCharacters}-character ownership limit.");
        }

        return new EphemeralSecretMaterial(value);
    }

    /// <summary>
    /// Returns a value-free diagnostic projection.
    /// </summary>
    /// <returns>A constant value-free marker, or an empty string when that marker would contain the owned value.</returns>
    public override string ToString()
    {
        return GetSafeProjectionMarker();
    }

    /// <summary>
    /// Clears the owned memory and releases this instance. Repeated calls are safe.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_characters is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_characters.AsSpan()));
            _characters = null;
        }
    }

    internal char[] CopyCharacters()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_characters is null, this);
            return _characters.ToArray();
        }
    }

    private string GetSafeProjectionMarker()
    {
        lock (_sync)
        {
            return _safeProjectionMarker;
        }
    }
}
