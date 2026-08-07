using EmbodySense.Core.Application.Governance.Authority.Models;

namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityProfileOperationDocument(string OperationId, string RequestHash, AuthorityProfileMutationKind Kind, AuthorityProfileMutationStatus Outcome, string ProfileId, int? ResultingRevision, string ActorId, string Reason, DateTimeOffset RecordedAtUtc);
