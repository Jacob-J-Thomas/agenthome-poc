using System.Globalization;
using System.Text;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class DefaultConversationTurnSourceProofPublicationIntent : IDisposable
{
    private const byte StateVersion = 1;
    private readonly FileStream _stream;
    private readonly byte[] _bytes;

    private DefaultConversationTurnSourceProofPublicationIntent(string path, FileStream stream, byte[] bytes, DefaultConversationTurnFileIdentity sourceIdentity, DefaultConversationTurnFileIdentity historyIdentity)
    {
        Path = path;
        _stream = stream;
        _bytes = bytes;
        Identity = DefaultConversationTurnNativeFileSystem.GetIdentity(stream);
        SourceIdentity = sourceIdentity;
        HistoryIdentity = historyIdentity;
    }

    public string Path { get; private set; }

    public DefaultConversationTurnFileIdentity Identity { get; }

    public DefaultConversationTurnFileIdentity SourceIdentity { get; }

    public DefaultConversationTurnFileIdentity HistoryIdentity { get; }

    public static DefaultConversationTurnSourceProofPublicationIntent Create(string path, DefaultConversationTurnFileIdentity sourceIdentity, DefaultConversationTurnFileIdentity historyIdentity)
    {
        var bytes = Serialize(sourceIdentity, historyIdentity);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 128, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            DefaultConversationTurnNativeFileSystem.RequireSingleLinkRegularFile(stream);
            var intent = new DefaultConversationTurnSourceProofPublicationIntent(path, stream, bytes, sourceIdentity, historyIdentity);
            stream = null;
            if (!intent.RevalidatesCurrentPathname())
            {
                intent.Dispose();
                throw new IOException($"Default-conversation source-proof publication intent `{path}` changed during creation.");
            }

            return intent;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public static DefaultConversationTurnSourceProofPublicationIntent Open(string path)
    {
        FileStream? stream = null;
        try
        {
            stream = DefaultConversationTurnNativeFileSystem.OpenRegularReadForRetirement(path);
            DefaultConversationTurnNativeFileSystem.RequireSingleLinkRegularFile(stream);
            var bytes = ReadAll(stream);
            var (sourceIdentity, historyIdentity) = Parse(bytes);
            var intent = new DefaultConversationTurnSourceProofPublicationIntent(path, stream, bytes, sourceIdentity, historyIdentity);
            stream = null;
            return intent;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public bool RevalidatesCurrentPathname()
    {
        try
        {
            if (!IsStillValid())
            {
                return false;
            }

            return DefaultConversationTurnNativeFileSystem.RegularPathMatchesIdentity(Path, Identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    public bool TryRelocate(string destinationPath)
    {
        if (!RevalidatesCurrentPathname())
        {
            return false;
        }

        try
        {
            File.Move(Path, destinationPath, overwrite: false);
            if (!DefaultConversationTurnNativeFileSystem.RegularPathMatchesIdentity(destinationPath, Identity) || !IsStillValid())
            {
                return false;
            }

            Path = destinationPath;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    public static string FormatIdentity(DefaultConversationTurnFileIdentity identity)
    {
        return $"{identity.DeviceId:x16}-{identity.FileId:x16}";
    }

    public static bool TryParseIdentity(string text, out DefaultConversationTurnFileIdentity identity)
    {
        identity = default;
        if (text.Length != 33 || text[16] != '-'
            || !ulong.TryParse(text[..16], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var deviceId)
            || !ulong.TryParse(text[17..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var fileId))
        {
            return false;
        }

        identity = new DefaultConversationTurnFileIdentity(deviceId, fileId);
        return true;
    }

    private bool IsStillValid()
    {
        try
        {
            DefaultConversationTurnNativeFileSystem.RequireSingleLinkRegularFile(_stream);
            return DefaultConversationTurnNativeFileSystem.GetIdentity(_stream) == Identity
                && ReadAll(_stream).AsSpan().SequenceEqual(_bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Serialize(DefaultConversationTurnFileIdentity sourceIdentity, DefaultConversationTurnFileIdentity historyIdentity)
    {
        return Encoding.ASCII.GetBytes($"{StateVersion}\n{FormatIdentity(sourceIdentity)}\n{FormatIdentity(historyIdentity)}\n");
    }

    private static (DefaultConversationTurnFileIdentity SourceIdentity, DefaultConversationTurnFileIdentity HistoryIdentity) Parse(byte[] bytes)
    {
        if (bytes.Length != 70 || bytes[0] != (byte)'1' || bytes[1] != (byte)'\n' || bytes[35] != (byte)'\n' || bytes[69] != (byte)'\n')
        {
            throw new FormatException("The default-conversation source-proof publication intent has invalid version-1 evidence.");
        }

        var text = Encoding.ASCII.GetString(bytes);
        if (!TryParseIdentity(text[2..35], out var sourceIdentity) || !TryParseIdentity(text[36..69], out var historyIdentity))
        {
            throw new FormatException("The default-conversation source-proof publication intent has invalid identity evidence.");
        }

        return (sourceIdentity, historyIdentity);
    }

    private static byte[] ReadAll(FileStream stream)
    {
        stream.Position = 0;
        if (stream.Length <= 0 || stream.Length > 256)
        {
            throw new FormatException("The default-conversation source-proof publication intent has an invalid size.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new FormatException("The default-conversation source-proof publication intent changed while it was read.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new FormatException("The default-conversation source-proof publication intent changed while it was read.");
        }

        return bytes;
    }
}
