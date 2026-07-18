using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

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
    [JsonIgnore]
    public bool Included => OmissionReason is null;
}
