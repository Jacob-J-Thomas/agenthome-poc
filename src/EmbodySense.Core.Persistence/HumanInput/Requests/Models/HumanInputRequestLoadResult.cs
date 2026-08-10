namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

internal sealed record HumanInputRequestLoadResult(
    HumanInputRequestStoreDocument? Document,
    HumanInputRequestStoreDocument? Pending,
    HumanInputRequestLoadDisposition Disposition);
