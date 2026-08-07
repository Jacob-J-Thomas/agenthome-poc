namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a content entry.
/// </summary>
/// <param name="Id">The ID.</param>
/// <param name="Hash">The hash.</param>
/// <param name="Utf16Characters">The utf16 characters.</param>
/// <param name="Utf8Bytes">The UTF-8 bytes.</param>
/// <param name="Base64">The base64.</param>
/// <param name="Text">The text.</param>
internal sealed record ContentEntry(
    string Id,
    string Hash,
    int Utf16Characters,
    int Utf8Bytes,
    string Base64,
    string Text);
