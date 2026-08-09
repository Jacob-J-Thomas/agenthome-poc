using System.Security.Cryptography;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class DefaultConversationTurnRetirementEvidenceProof : IDisposable
{
    private readonly Dictionary<string, RetainedFileProof> _files = new(StringComparer.Ordinal);

    public bool CanReservePair(int maximumEntries)
    {
        return maximumEntries >= 2 && _files.Count <= maximumEntries - 2;
    }

    public bool TryAddPair(string intentPath, string retiredPath, DefaultConversationTurnFileIdentity retiredIdentity)
    {
        FileStream? intentStream = null;
        FileStream? retiredStream = null;
        try
        {
            intentStream = DefaultConversationTurnNativeFileSystem.OpenRegularRead(intentPath);
            retiredStream = DefaultConversationTurnNativeFileSystem.OpenRegularReadForRetirement(retiredPath);
            var intentIdentity = DefaultConversationTurnNativeFileSystem.GetIdentity(intentStream);
            if (!IsValidIntent(intentStream)
                || !IsValidRetired(retiredStream, retiredIdentity)
                || _files.ContainsKey(intentPath)
                || _files.ContainsKey(retiredPath))
            {
                return false;
            }

            _files.Add(intentPath, new RetainedFileProof(intentStream, intentIdentity, ComputeContentHash(intentStream), true));
            _files.Add(retiredPath, new RetainedFileProof(retiredStream, retiredIdentity, ComputeContentHash(retiredStream), false));
            intentStream = null;
            retiredStream = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            intentStream?.Dispose();
            retiredStream?.Dispose();
        }
    }

    public bool Matches(DefaultConversationTurnRetirementEvidenceProof current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_files.Count != current._files.Count || !IsStillValid() || !current.IsStillValid())
        {
            return false;
        }

        return _files.All(item => current._files.TryGetValue(item.Key, out var currentProof)
            && currentProof.Identity == item.Value.Identity
            && currentProof.ContentHash.AsSpan().SequenceEqual(item.Value.ContentHash)
            && currentProof.IsIntent == item.Value.IsIntent);
    }

    public bool RevalidatesCurrentPathnames()
    {
        try
        {
            foreach (var item in _files)
            {
                using var currentStream = item.Value.IsIntent
                    ? DefaultConversationTurnNativeFileSystem.OpenRegularRead(item.Key)
                    : DefaultConversationTurnNativeFileSystem.OpenRegularReadForRetirement(item.Key);
                if (DefaultConversationTurnNativeFileSystem.GetIdentity(currentStream) != item.Value.Identity
                    || (item.Value.IsIntent ? !IsValidIntent(currentStream) : !IsValidRetired(currentStream, item.Value.Identity))
                    || !ComputeContentHash(currentStream).AsSpan().SequenceEqual(item.Value.ContentHash))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var proof in _files.Values)
        {
            proof.Stream.Dispose();
        }

        _files.Clear();
    }

    private bool IsStillValid()
    {
        try
        {
            return _files.Values.All(proof => proof.IsIntent
                ? DefaultConversationTurnNativeFileSystem.GetIdentity(proof.Stream) == proof.Identity
                    && IsValidIntent(proof.Stream)
                    && ComputeContentHash(proof.Stream).AsSpan().SequenceEqual(proof.ContentHash)
                : IsValidRetired(proof.Stream, proof.Identity)
                    && ComputeContentHash(proof.Stream).AsSpan().SequenceEqual(proof.ContentHash));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsValidIntent(FileStream stream)
    {
        DefaultConversationTurnNativeFileSystem.RequireSingleLinkRegularFile(stream);
        stream.Position = 0;
        return stream.Length == 1 && stream.ReadByte() == 1 && stream.ReadByte() == -1;
    }

    private static bool IsValidRetired(FileStream stream, DefaultConversationTurnFileIdentity expectedIdentity)
    {
        DefaultConversationTurnNativeFileSystem.RequireSingleLinkRegularFile(stream);
        return DefaultConversationTurnNativeFileSystem.GetIdentity(stream) == expectedIdentity;
    }

    private static byte[] ComputeContentHash(FileStream stream)
    {
        stream.Position = 0;
        return SHA256.HashData(stream);
    }

    private sealed record RetainedFileProof(FileStream Stream, DefaultConversationTurnFileIdentity Identity, byte[] ContentHash, bool IsIntent);
}
