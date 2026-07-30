using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Records one ordered context source together with provenance, trust, truncation, omission, and integrity evidence.
/// </summary>
/// <param name="Order">The deterministic source order.</param>
/// <param name="SourceType">The source type.</param>
/// <param name="SourceId">The stable source identifier.</param>
/// <param name="SourcePath">The source path retained for provenance.</param>
/// <param name="Provenance">The origin classification of the content.</param>
/// <param name="TrustClass">The authority and trust classification applied to the content.</param>
/// <param name="Role">The model message role assigned to the content.</param>
/// <param name="Content">The exact content.</param>
/// <param name="ContentHash">The lowercase SHA-256 digest of the exact content.</param>
/// <param name="OriginalCharacterCount">The character count before truncation or omission.</param>
/// <param name="UsedCharacterCount">The character count retained in the content.</param>
/// <param name="Truncated">Whether content was truncated to a configured limit.</param>
/// <param name="TruncationReason">The truncation reason, or <see langword="null"/> when content was not truncated.</param>
/// <param name="OmissionReason">The omission reason, or <see langword="null"/> when the source was included.</param>
/// <param name="CapturedAtUtc">The UTC capture time.</param>
public sealed record CustomLoopContextManifestSource(
    int Order,
    CustomLoopContextSource SourceType,
    string SourceId,
    string SourcePath,
    CustomLoopContextProvenance Provenance,
    CustomLoopContextTrustClass TrustClass,
    LlmMessageRole Role,
    string Content,
    string ContentHash,
    int OriginalCharacterCount,
    int UsedCharacterCount,
    bool Truncated,
    string? TruncationReason,
    string? OmissionReason,
    DateTimeOffset CapturedAtUtc)
{
    /// <summary>
    /// Gets a value indicating whether this source contributed content to the snapshot.
    /// </summary>
    /// <value><see langword="true"/> when <see cref="OmissionReason"/> is <see langword="null"/>; otherwise, <see langword="false"/>.</value>
    [JsonIgnore]
    public bool Included => OmissionReason is null;
}
