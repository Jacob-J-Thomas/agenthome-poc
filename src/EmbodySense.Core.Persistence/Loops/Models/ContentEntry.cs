namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record ContentEntry(
    string Id,
    string Hash,
    int Utf16Characters,
    int Utf8Bytes,
    string Base64,
    string Text,
    byte[] Bytes);
