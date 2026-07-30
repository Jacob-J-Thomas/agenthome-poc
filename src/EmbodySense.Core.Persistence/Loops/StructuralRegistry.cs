using System.Text.Json.Nodes;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class StructuralRegistry
{
    private readonly string _prefix;
    private readonly string _description;
    private readonly List<StructuralEntry> _entries;
    private readonly Dictionary<string, StructuralEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StructuralEntry> _byHash = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _referencedIds = new(StringComparer.Ordinal);

    public StructuralRegistry(string prefix, string description, IReadOnlyList<StructuralEntry> seeds)
    {
        _prefix = prefix;
        _description = description;
        _entries = new List<StructuralEntry>(seeds.Count);
        for (var index = 0; index < seeds.Count; index++)
        {
            var entry = seeds[index];
            if (!string.Equals(entry.Id, CustomLoopRunArtifactCodec.IndexedId(_prefix, index), StringComparison.Ordinal))
            {
                throw new FormatException($"The {_description} table ids are not the canonical contiguous first-use base-36 sequence.");
            }

            var bytes = CustomLoopRunArtifactCodec.SerializeNode(entry.Value);
            var hash = CustomLoopRunArtifactCodec.Hash(bytes);
            if (_byHash.TryGetValue(hash, out var duplicate))
            {
                if (!CustomLoopRunArtifactCodec.SerializeNode(duplicate.Value).AsSpan().SequenceEqual(bytes))
                {
                    throw new FormatException($"The {_description} table has a SHA-256 collision with unequal exact bytes.");
                }

                throw new FormatException($"The exact same {_description} structure is stored under different ids `{duplicate.Id}` and `{entry.Id}`.");
            }

            if (!_byId.TryAdd(entry.Id, entry) || !_byHash.TryAdd(hash, entry))
            {
                throw new FormatException($"The {_description} table contains duplicate ids or hashes.");
            }

            _entries.Add(entry);
            _seedIds.Add(entry.Id);
        }
    }

    public IReadOnlyList<StructuralEntry> Entries => _entries;

    public string Reference(JsonObject value)
    {
        var bytes = CustomLoopRunArtifactCodec.SerializeNode(value);
        var hash = CustomLoopRunArtifactCodec.Hash(bytes);
        if (_byHash.TryGetValue(hash, out var existing))
        {
            if (!CustomLoopRunArtifactCodec.SerializeNode(existing.Value).AsSpan().SequenceEqual(bytes))
            {
                throw new FormatException($"A {_description} hash collision did not compare byte-for-byte equal.");
            }

            _referencedIds.Add(existing.Id);
            return existing.Id;
        }

        var id = CustomLoopRunArtifactCodec.IndexedId(_prefix, _entries.Count);
        var entry = new StructuralEntry(id, value.DeepClone().AsObject());
        _entries.Add(entry);
        _byId.Add(id, entry);
        _byHash.Add(hash, entry);
        _referencedIds.Add(id);
        return id;
    }

    public JsonObject Resolve(string id)
    {
        if (!_byId.TryGetValue(id, out var entry))
        {
            throw new FormatException($"{_description} reference `{id}` is dangling.");
        }

        _referencedIds.Add(id);
        return entry.Value.DeepClone().AsObject();
    }

    public void RequireEverySeedReferenced()
    {
        if (_seedIds.Any(id => !_referencedIds.Contains(id)))
        {
            throw new FormatException($"The canonical {_description} table contains an unreferenced or noncanonical entry.");
        }
    }
}
