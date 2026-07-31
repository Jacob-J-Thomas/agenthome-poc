using System.Text;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Maintains the canonical first-use, hash-verified content table for a compact run artifact.
/// </summary>
internal sealed class ContentRegistry
{
    private readonly List<ContentEntry> _entries;
    private readonly Dictionary<string, ContentEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentEntry> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentEntry> _byText = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _referencedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentRegistry"/> type.
    /// </summary>
    /// <param name="seeds">The seeds.</param>
    public ContentRegistry(IReadOnlyList<ContentEntry> seeds)
    {
        _entries = new List<ContentEntry>(seeds.Count);
        for (var index = 0; index < seeds.Count; index++)
        {
            var entry = seeds[index];
            if (!string.Equals(entry.Id, CustomLoopRunArtifactCodec.IndexedId("c", index), StringComparison.Ordinal))
            {
                throw new FormatException("The content table ids are not the canonical contiguous first-use base-36 sequence.");
            }

            if ((_byId.TryGetValue(entry.Id, out var sameId) && !sameId.Bytes.AsSpan().SequenceEqual(entry.Bytes))
                || (_byHash.TryGetValue(entry.Hash, out var sameHash) && !sameHash.Bytes.AsSpan().SequenceEqual(entry.Bytes)))
            {
                throw new FormatException("The content table reuses an id or SHA-256 for different exact bytes.");
            }

            if (_byText.TryGetValue(entry.Text, out var duplicate))
            {
                throw new FormatException($"The exact same content is stored under different table entries `{duplicate.Id}` and `{entry.Id}`.");
            }

            if (!_byId.TryAdd(entry.Id, entry) || !_byHash.TryAdd(entry.Hash, entry) || !_byText.TryAdd(entry.Text, entry))
            {
                throw new FormatException("The content table contains duplicate ids or hashes.");
            }

            _entries.Add(entry);
            _seedIds.Add(entry.Id);
        }
    }

    /// <summary>
    /// Gets the content entries.
    /// </summary>
    /// <value>The content entries.</value>
    public IReadOnlyList<ContentEntry> Entries => _entries;

    /// <summary>
    /// Returns the canonical content identifier, adding a new first-use entry when necessary.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The canonical compact content identifier.</returns>
    public string Reference(string text)
    {
        byte[] bytes;
        try
        {
            bytes = CustomLoopRunArtifactCodec.StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new FormatException("Content-bearing run text is not strict UTF-8.", exception);
        }

        var hash = CustomLoopRunArtifactCodec.Hash(bytes);
        if (_byHash.TryGetValue(hash, out var existing))
        {
            if (!existing.Bytes.AsSpan().SequenceEqual(bytes))
            {
                throw new FormatException("A content hash collision did not compare byte-for-byte equal.");
            }

            _referencedIds.Add(existing.Id);
            return existing.Id;
        }

        if (_byText.TryGetValue(text, out var duplicate))
        {
            throw new FormatException($"The exact same content would be assigned inconsistently after `{duplicate.Id}`.");
        }

        var id = CustomLoopRunArtifactCodec.IndexedId("c", _entries.Count);
        var entry = new ContentEntry(id, hash, text.Length, bytes.Length, Convert.ToBase64String(bytes), text, bytes);
        _entries.Add(entry);
        _byId.Add(id, entry);
        _byHash.Add(hash, entry);
        _byText.Add(text, entry);
        _referencedIds.Add(id);
        return id;
    }

    /// <summary>
    /// Resolves the requested value.
    /// </summary>
    /// <param name="id">The ID.</param>
    /// <returns>The exact strict-UTF-8 text bound to the identifier.</returns>
    public string Resolve(string id)
    {
        if (!_byId.TryGetValue(id, out var entry))
        {
            throw new FormatException($"Content reference `{id}` is dangling.");
        }

        _referencedIds.Add(id);
        return entry.Text;
    }

    /// <summary>
    /// Rejects decoded seed entries that were never referenced by the projected run.
    /// </summary>
    public void RequireEverySeedReferenced()
    {
        if (_seedIds.Any(id => !_referencedIds.Contains(id)))
        {
            throw new FormatException("The canonical content table contains an unreferenced or noncanonical entry.");
        }
    }
}
