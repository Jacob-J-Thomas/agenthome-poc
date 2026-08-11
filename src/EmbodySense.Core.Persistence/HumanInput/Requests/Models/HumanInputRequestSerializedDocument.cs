namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

internal sealed record HumanInputRequestSerializedDocument(
    string Json,
    string ContentDigest,
    string AuthenticationTag);
